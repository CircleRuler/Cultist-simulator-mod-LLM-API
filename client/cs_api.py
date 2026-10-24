#!/usr/bin/env python3
"""Minimal client for the LLM_API mod running inside Cultist Simulator.

Standard library only, so an LLM agent or any other process can drive the game with no
setup beyond a Python install.

    python cs_api.py state
    python cs_api.py state --brief
    python cs_api.py tokens
    python cs_api.py slots
    python cs_api.py place  --token '!introjob_2' --slot '~/tabletop!work_0/work'
    python cs_api.py start  --token '!work_0'
    python cs_api.py move   --token '!introjob_2' --to '~/tabletop'
    python cs_api.py click  --token '!work_0'
    python cs_api.py watch  --interval 2

The address of every container is a FucinePath string, e.g. '~/tabletop',
'~/tabletop!work_0/work'. GET /state lists all of them.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

DEFAULT_BASE = "http://127.0.0.1:8710"


class ApiError(RuntimeError):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


def call(base, method, path, body=None, timeout=30.0):
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    url = base.rstrip("/") + path
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            payload = json.loads(raw)
            raise ApiError(payload.get("error", raw), e.code) from None
        except json.JSONDecodeError:
            raise ApiError(raw or str(e), e.code) from None
    except urllib.error.URLError as e:
        raise ApiError(
            f"cannot reach the game at {url}: {e.reason}. "
            "Is Cultist Simulator running with the LLM_API mod enabled?"
        ) from None

    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        raise ApiError("the game returned a non-JSON response: " + raw[:400]) from None


# --- presentation -------------------------------------------------------------------


def brief(state):
    """The condensed view an agent should reason over: what is where, and what each verb can do."""
    tokens = state.get("tokens", [])
    lines = [
        f"game {state.get('gameVersion')}  spheres={state.get('sphereCount')}  tokens={state.get('tokenCount')}",
        "",
        "CARDS",
    ]
    for t in tokens:
        if t.get("payloadType") == "Situation":
            continue
        bits = [f"  {t.get('id'):<24} {t.get('label') or t.get('entityId')}"]
        if (t.get("quantity") or 1) != 1:
            bits.append(f"x{t['quantity']}")
        bits.append(f"@ {t.get('spherePath')}")
        if t.get("aspects"):
            bits.append("aspects=" + ",".join(sorted(t["aspects"])))
        lines.append(" ".join(bits))

    lines.append("")
    lines.append("VERBS (situations)")
    for t in tokens:
        if t.get("payloadType") != "Situation":
            continue
        lines.append(f"  {t.get('id'):<24} verb={t.get('verbId')} state={t.get('situationState')} recipe={t.get('currentRecipeId')}")
        for slot in t.get("slots") or []:
            mark = "EMPTY" if slot.get("empty") else "filled=" + ",".join(slot.get("tokenIds") or [])
            lines.append(f"      slot {slot.get('path')}  [{slot.get('specId')}]  {mark}")
    return "\n".join(lines)


def print_spheres(state):
    for s in state.get("spheres", []):
        print(f"{s.get('path'):<44} {s.get('type'):<22} spec={s.get('specId')} tokens={s.get('tokenCount')}")


def print_slots(state):
    found = False
    for t in state.get("tokens", []):
        if t.get("payloadType") != "Situation":
            continue
        for slot in t.get("slots") or []:
            found = True
            state_txt = "EMPTY" if slot.get("empty") else "filled by " + ",".join(slot.get("tokenIds") or [])
            print(f"{slot.get('path'):<48} [{slot.get('specId')}] {state_txt}   (situation {t.get('id')})")
    if not found:
        print("no card slots on the board")


def wait_for_board(base, timeout=60.0, interval=1.0):
    """A game load is asynchronous: poll /state until the board answers."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            state = call(base, "GET", "/state", timeout=10.0)
            print(f"\nboard ready: spheres={state.get('sphereCount')} tokens={state.get('tokenCount')}")
            return state
        except ApiError as e:
            if e.status not in (503, 504):
                raise
            time.sleep(interval)
    print(f"\nstill no board after {timeout:.0f}s (the game may be on a loading or menu screen)")
    return None


# --- commands -----------------------------------------------------------------------


def main(argv=None):
    parser = argparse.ArgumentParser(description="Drive Cultist Simulator through the LLM_API mod.")
    parser.add_argument("--base", default=DEFAULT_BASE, help=f"API base URL (default {DEFAULT_BASE})")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("state", help="full board state as JSON")
    p.add_argument("--brief", action="store_true", help="condensed human/agent readable summary")

    p = sub.add_parser("spheres", help="list every sphere path")
    p = sub.add_parser("tokens", help="list every token as JSON")
    p = sub.add_parser("slots", help="list every card slot and whether it is empty")
    p = sub.add_parser("legacies", help="list the legacies a new game could start with")
    p = sub.add_parser("help", help="show the server's own route documentation")
    p = sub.add_parser("ping", help="check that the API is alive")

    p = sub.add_parser("newgame", help="start a fresh game (works from the main menu)")
    p.add_argument("--legacy", default=None, help="legacy id; omit for the game's default")
    p.add_argument("--wait", action="store_true", help="poll until the board is ready")

    p = sub.add_parser("continue", help="resume the existing save (works from the main menu)")
    p.add_argument("--wait", action="store_true", help="poll until the board is ready")

    p = sub.add_parser("place", help="put a card into a card slot")
    p.add_argument("--token", required=True)
    p.add_argument("--slot", required=True)

    p = sub.add_parser("start", help="press begin on a verb / situation")
    p.add_argument("--token", required=True)

    p = sub.add_parser("move", help="move a card to another sphere, or to x/y inside its current one")
    p.add_argument("--token", required=True)
    p.add_argument("--to", default=None)
    p.add_argument("--x", type=float, default=None)
    p.add_argument("--y", type=float, default=None)

    p = sub.add_parser("click", help="primary interaction with a card")
    p.add_argument("--token", required=True)

    p = sub.add_parser("watch", help="poll state and print changes")
    p.add_argument("--interval", type=float, default=2.0)

    args = parser.parse_args(argv)
    base = args.base

    try:
        if args.command == "ping":
            print(json.dumps(call(base, "GET", "/ping"), indent=2))

        elif args.command == "help":
            print(json.dumps(call(base, "GET", "/help"), indent=2))

        elif args.command == "state":
            state = call(base, "GET", "/state")
            if args.brief:
                print(brief(state))
            else:
                print(json.dumps(state, indent=2, ensure_ascii=False))

        elif args.command == "spheres":
            print_spheres(call(base, "GET", "/state"))

        elif args.command == "slots":
            print_slots(call(base, "GET", "/state"))

        elif args.command == "tokens":
            state = call(base, "GET", "/state")
            print(json.dumps(state.get("tokens", []), indent=2, ensure_ascii=False))

        elif args.command == "legacies":
            result = call(base, "GET", "/legacies")
            for entry in result.get("legacies", []):
                marker = "  (default)" if entry.get("isDefault") else ""
                print(f"{entry.get('id'):<24} {entry.get('label')}{marker}")
            if not result.get("legacies"):
                print("no legacies reported")
            print(f"\nin a game already: {result.get('inGame')}")

        elif args.command == "newgame":
            print(json.dumps(call(base, "POST", "/action/newgame",
                                  {"legacy": args.legacy}), indent=2, ensure_ascii=False))
            if args.wait:
                wait_for_board(base)

        elif args.command == "continue":
            print(json.dumps(call(base, "POST", "/action/continue"), indent=2, ensure_ascii=False))
            if args.wait:
                wait_for_board(base)

        elif args.command == "place":
            print(json.dumps(call(base, "POST", "/action/place",
                                  {"token": args.token, "slot": args.slot}), indent=2, ensure_ascii=False))

        elif args.command == "start":
            print(json.dumps(call(base, "POST", "/action/start",
                                  {"token": args.token}), indent=2, ensure_ascii=False))

        elif args.command == "move":
            body = {"token": args.token}
            if args.to is not None:
                body["to"] = args.to
            if args.x is not None:
                body["x"] = args.x
            if args.y is not None:
                body["y"] = args.y
            print(json.dumps(call(base, "POST", "/action/move", body), indent=2, ensure_ascii=False))

        elif args.command == "click":
            print(json.dumps(call(base, "POST", "/action/click",
                                  {"token": args.token}), indent=2, ensure_ascii=False))

        elif args.command == "watch":
            previous = None
            while True:
                state = call(base, "GET", "/state")
                signature = json.dumps(
                    [(t.get("id"), t.get("spherePath"), t.get("quantity"), t.get("timeRemaining"))
                     for t in state.get("tokens", [])],
                    sort_keys=True)
                if signature != previous:
                    print(f"\n--- {time.strftime('%H:%M:%S')} ---")
                    print(brief(state))
                    previous = signature
                time.sleep(args.interval)

    except ApiError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        return 130

    return 0


if __name__ == "__main__":
    sys.exit(main())
