using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CsGameApi
{
    /// <summary>
    /// Boot-time smoke test. It exercises the same code paths the HTTP routes use and writes a
    /// human-readable report next to the log. This exists because the game cannot be driven
    /// from outside a desktop session: one game launch then tells us whether state reading
    /// works against the live board.
    ///
    /// With <paramref name="replay"/> set it also performs the three mutating actions, which
    /// change the running save, so that is opt-in rather than the default.
    /// </summary>
    internal static class SelfTest
    {
        private static readonly List<string> Lines = new List<string>();
        private static readonly Dictionary<string, object> Report = new Dictionary<string, object>();

        public static void Run(bool replay = false)
        {
            Lines.Clear();
            Report.Clear();

            Say("LLM_API self-test" + (replay ? " (read + replay)" : " (read only)"));
            Say("game=" + Application.version + "  unity=" + Application.unityVersion);
            Say("");

            // 1. READ ---------------------------------------------------------------------
            Dictionary<string, object> state = null;
            try
            {
                state = GameStateSerializer.Capture();
                int spheres = Convert.ToInt32(state["sphereCount"]);
                int tokens = Convert.ToInt32(state["tokenCount"]);
                Say("READ      ok: " + spheres + " spheres, " + tokens + " tokens");
                Report["read"] = new Dictionary<string, object>
                {
                    { "success", true },
                    { "spheres", spheres },
                    { "tokens", tokens }
                };
                DumpSphereSummary(state);
                DumpTokenSummary(state);
            }
            catch (Exception e)
            {
                Say("READ      FAILED: " + e.Message);
                Report["read"] = new Dictionary<string, object> { { "success", false }, { "error", e.Message } };
                Finish();
                return;
            }

            // Choose subjects for the mutating tests.
            string tokenId = PickToken(state);
            string slotPath = PickSlot(state);

            Say("");
            Say("selected token = " + (tokenId ?? "<none>"));
            Say("selected slot  = " + (slotPath ?? "<none>"));
            Say("");

            if (!replay)
            {
                // Read-only pass: report what the actions *would* target, change nothing.
                var preview = new Dictionary<string, object>
                {
                    { "success", true },
                    { "skipped", true },
                    { "reason", "read-only pass; POST /selftest with {\"replay\":true} to run the mutating actions" },
                    { "wouldPlace", tokenId },
                    { "intoSlot", slotPath },
                    { "wouldStart", PickSituation(state) },
                    { "wouldMove", tokenId },
                    { "to", PickMoveDestination(state, tokenId) }
                };
                Say("ACTIONS   dry run: " + Json.Serialize(preview));
                Report["place"] = preview;
                Report["start"] = preview;
                Report["move"] = preview;
                Finish();
                return;
            }

            // 2. PLACE --------------------------------------------------------------------
            if (ApiConfig.SelfTestSlotPath.Length > 0) slotPath = ApiConfig.SelfTestSlotPath;
            if (tokenId == null || slotPath == null)
            {
                Say("PLACE     skipped: needs both a movable card and an empty slot");
                Report["place"] = new Dictionary<string, object> { { "success", false }, { "skipped", true } };
            }
            else
            {
                RunAction("PLACE", "place", () => GameActions.Place(tokenId, slotPath));
            }

            // 3. START --------------------------------------------------------------------
            string situationId = PickSituation(state);
            if (situationId == null)
            {
                Say("START     skipped: no Situation token on the board");
                Report["start"] = new Dictionary<string, object> { { "success", false }, { "skipped", true } };
            }
            else
            {
                Say("starting situation = " + situationId);
                RunAction("START", "start", () => GameActions.Start(situationId));
            }

            // 4. MOVE ---------------------------------------------------------------------
            string moveToken = PickToken(state);
            string moveDestination = PickMoveDestination(state, moveToken);
            if (moveToken == null || moveDestination == null)
            {
                Say("MOVE      skipped: no card or no destination sphere");
                Report["move"] = new Dictionary<string, object> { { "success", false }, { "skipped", true } };
            }
            else
            {
                Say("moving " + moveToken + " -> " + moveDestination);
                RunAction("MOVE", "move", () => GameActions.Move(moveToken, moveDestination, null, null));
            }

            Finish();
        }

        private static void RunAction(string label, string key, Func<Dictionary<string, object>> action)
        {
            try
            {
                Dictionary<string, object> result = action();
                bool ok = result.ContainsKey("success") && result["success"] is bool && (bool)result["success"];
                Say(label.PadRight(10) + (ok ? "ok" : "returned failure") + ": " + Json.Serialize(result));
                Report[key] = result;
            }
            catch (Exception e)
            {
                Say(label.PadRight(10) + "FAILED: " + e.Message);
                Report[key] = new Dictionary<string, object> { { "success", false }, { "error", e.Message } };
            }
        }

        private static void DumpSphereSummary(Dictionary<string, object> state)
        {
            var spheres = state["spheres"] as List<object>;
            if (spheres == null) return;
            Say("");
            Say("  spheres:");
            foreach (Dictionary<string, object> s in spheres)
            {
                Say("    " + Val(s, "path") + "  [" + Val(s, "type") + "]  spec=" + Val(s, "specId")
                    + "  label=" + Val(s, "label") + "  tokens=" + Val(s, "tokenCount"));
            }
        }

        private static void DumpTokenSummary(Dictionary<string, object> state)
        {
            var tokens = state["tokens"] as List<object>;
            if (tokens == null) return;
            Say("");
            Say("  tokens:");
            int shown = 0;
            foreach (Dictionary<string, object> t in tokens)
            {
                if (shown++ >= 60) { Say("    ... (" + (tokens.Count - 60) + " more)"); break; }
                Say("    " + Val(t, "id") + "  entity=" + Val(t, "entityId") + "  label=" + Val(t, "label")
                    + "  qty=" + Val(t, "quantity") + "  @" + Val(t, "spherePath")
                    + "  pos=" + (t.ContainsKey("position") ? Json.Serialize(t["position"]) : "null"));
            }
        }

        private static string PickToken(Dictionary<string, object> state)
        {
            if (ApiConfig.SelfTestTokenId.Length > 0) return ApiConfig.SelfTestTokenId;
            var tokens = state["tokens"] as List<object>;
            if (tokens == null) return null;

            // Prefer a plain card on the tabletop that the game itself considers draggable.
            string fallback = null;
            foreach (Dictionary<string, object> t in tokens)
            {
                if (!Equals(Val(t, "spherePath"), "~/tabletop")) continue;
                if (Val(t, "payloadType") != "ElementStack") continue;
                if (Val(t, "defunct") == "True") continue;
                string id = Val(t, "id");
                if (string.IsNullOrEmpty(id)) continue;
                if (fallback == null) fallback = id;
                if (Val(t, "draggable") == "True") return id;
            }
            return fallback;
        }

        private static string PickSituation(Dictionary<string, object> state)
        {
            var tokens = state["tokens"] as List<object>;
            if (tokens == null) return null;
            foreach (Dictionary<string, object> t in tokens)
            {
                if (Val(t, "payloadType") != "Situation") continue;
                string id = Val(t, "id");
                if (!string.IsNullOrEmpty(id)) return id;
            }
            return null;
        }

        /// <summary>First empty slot inside any situation, i.e. a card frame waiting for input.</summary>
        private static string PickSlot(Dictionary<string, object> state)
        {
            var tokens = state["tokens"] as List<object>;
            if (tokens == null) return null;
            foreach (Dictionary<string, object> t in tokens)
            {
                if (Val(t, "payloadType") != "Situation") continue;
                var slots = t.ContainsKey("slots") ? t["slots"] as List<object> : null;
                if (slots == null) continue;
                foreach (Dictionary<string, object> slot in slots)
                {
                    if (Val(slot, "empty") != "True") continue;
                    string path = Val(slot, "path");
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            return null;
        }

        /// <summary>A sphere that simply accepts cards, used by the move test.</summary>
        private static string PickMoveDestination(Dictionary<string, object> state, string tokenId)
        {
            var tokens = state["tokens"] as List<object>;
            if (tokens == null || tokenId == null) return null;
            string from = null;
            foreach (Dictionary<string, object> t in tokens)
            {
                if (Val(t, "id") == tokenId) { from = Val(t, "spherePath"); break; }
            }

            var spheres = state["spheres"] as List<object>;
            if (spheres == null) return null;
            foreach (Dictionary<string, object> s in spheres)
            {
                string path = Val(s, "path");
                if (string.IsNullOrEmpty(path) || path == from) continue;
                if (Val(s, "plumbing") == "True") continue;
                if (Val(s, "type") != "TabletopSphere") continue;
                return path;
            }
            return null;
        }

        private static string Val(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v == null ? "null" : v.ToString();
            return "-";
        }

        private static void Say(string line)
        {
            Lines.Add(line);
            Reflect.Log("[selftest] " + line);
        }

        /// <summary>The most recent report, so the HTTP route can hand it straight back.</summary>
        public static Dictionary<string, object> LastReport()
        {
            return new Dictionary<string, object>(Report);
        }

        private static void Finish()
        {
            Report["config"] = new Dictionary<string, object>
            {
                { "selfTestSlotPath", ApiConfig.SelfTestSlotPath },
                { "selfTestTokenId", ApiConfig.SelfTestTokenId }
            };
            Report["log"] = Lines;

            try
            {
                Directory.CreateDirectory(ApiConfig.Directory);
                File.WriteAllText(Path.Combine(ApiConfig.Directory, "selftest.json"), Json.Serialize(Report, true), new UTF8Encoding(false));
                Say("");
                Say("report written to " + Path.Combine(ApiConfig.Directory, "selftest.json"));
            }
            catch (Exception e)
            {
                Reflect.LogError("writing self-test report", e);
            }
        }
    }
}
