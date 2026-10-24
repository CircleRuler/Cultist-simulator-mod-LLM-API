using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using CsGameApi;
using Newtonsoft.Json.Linq;

namespace cs_gameapi_harness
{
    /// <summary>
    /// Offline harness for cs_gameapi.
    ///
    /// The game cannot be launched from a non-interactive session (the sandbox terminates the
    /// D3D process), so everything in the mod that does not require the real board is exercised
    /// here against the fake game types in FakeGame.dll: configuration, JSON handling, HTTP
    /// routing and status codes, the main-thread command pump, timeout behaviour, state
    /// serialisation, and all four actions.
    ///
    /// usage: Harness.exe <cs_gameapi.dll> <FakeGame.dll>
    /// </summary>
    internal static class Harness
    {
        /// <summary>
        /// The module id. It has to equal the mod folder name, the DLL file name and the entry
        /// class name - ModManager derives all three from the same GetNameAlphanumericsOnly()
        /// of the synopsis "name". If it ever changes again, change it here and in install.ps1.
        /// </summary>
        internal const string ModId = "LLM_API";

        private static int _passed;
        private static int _failed;

        private const string Base = "http://127.0.0.1:18710";

        private static int Main(string[] args)
        {
            string modDll = args.Length > 0 ? args[0] : @"F:\WorkSpace\cs_gameapi\build\" + ModId + @"\" + ModId + ".dll";
            string fakeDll = args.Length > 1 ? args[1] : @"F:\WorkSpace\cs_gameapi\tools\harness\bin\FakeGame.dll";

            Console.WriteLine("cs_gameapi offline harness");
            Console.WriteLine("mod   = " + modDll);
            Console.WriteLine("fake  = " + fakeDll);
            Console.WriteLine();

            Assembly.LoadFrom(modDll);
            Assembly.LoadFrom(fakeDll);

            // Unity stubs first: the mod's static fields touch UnityEngine types on load.
            Check("Vector3 stub present", typeof(UnityEngine.Vector3) != null);
            Check("Application stub present", typeof(UnityEngine.Application) != null);

            // The mod's own entry point namespace is the mod id; grab it by reflection so this
            // harness needs no compile-time reference to the mod either.
            Type modType = FindModType();
            Check("Assembly.GetType(\"" + ModId + "\") resolves the entry class", modType != null);
            if (modType == null) { Finish(); return 1; }
            Check("entry class is public", modType.IsPublic);
            Check("entry class sits in the global namespace", modType.Namespace == null || modType.Namespace.Length == 0,
                "namespace=" + (modType.Namespace ?? "<null>"));
            Check("entry class exposes public static Initialise()",
                modType.GetMethod("Initialise", BindingFlags.Public | BindingFlags.Static) != null);

            // Drive configuration the way Initialise() would, but without creating the
            // MonoBehaviour host (there is no Unity runtime here).
            string cfgDir = Path.Combine(Path.GetTempPath(), "cs_gameapi_harness_cfg");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "config.txt"),
                "# harness\nport=18710\nenabled=1\nselftest=0\nrequest_timeout_ms=4000\ndirectory=" + cfgDir + "\n");
            ApiConfig.Load(cfgDir);

            Section("config");
            Check("port parsed", ApiConfig.Port == 18710, "got " + ApiConfig.Port);
            Check("selftest parsed off", ApiConfig.SelfTest == false);
            Check("timeout parsed", ApiConfig.RequestTimeoutMs == 4000, "got " + ApiConfig.RequestTimeoutMs);
            Check("directory parsed", ApiConfig.Directory == cfgDir, "got " + ApiConfig.Directory);

            Section("JSON helpers");
            var o = Json.ParseObject("{\"token\":\"!a\",\"x\":12.5,\"flag\":true,\"n\":3}");
            Check("string field", Json.Str(o, "token") == "!a");
            Check("float field", Json.Float(o, "x") == 12.5f);
            Check("bool field", Json.Bool(o, "flag") == true);
            Check("int field", Json.Int(o, "n") == 3);
            Check("missing string falls back", Json.Str(o, "nope", "dflt") == "dflt");
            Check("missing int is null", Json.Int(o, "nope") == null);
            Check("empty body yields empty object", Json.ParseObject("").Count == 0);
            Check("number coercion from string", Json.Int(Json.ParseObject("{\"n\":\"7\"}"), "n") == 7);

            try
            {
                Json.ParseObject("{not json");
                Check("malformed JSON raises ApiException(400)", false);
            }
            catch (ApiException e)
            {
                Check("malformed JSON raises ApiException(400)", e.Status == 400, "got " + e.Status);
            }

            Check("Vector3 serialises as x/y/z",
                Json.Serialize(Json.Vec3ToJson(new UnityEngine.Vector3(1.5f, -2f, 0f))).Contains("\"x\":1.5"));

            // --- start the server against an empty world -------------------------------
            var queue = new System.Collections.Concurrent.ConcurrentQueue<GameCommand>();
            var server = new ApiHttpServer(ApiConfig.Port, queue);
            string startError;
            bool started = server.Start(out startError);
            Check("HTTP listener starts on " + ApiConfig.Port, started, startError);
            if (!started) { Finish(); return 1; }

            var pump = new PumpThread(queue);
            pump.Start();

            Section("HTTP surface, no board loaded");
            var ping = Call("GET", "/ping");
            Check("GET /ping -> 200", ping.Status == 200, ping.Body);
            Check("GET /ping reports success", ping.Body.Contains("\"success\": true"), ping.Body);

            var help = Call("GET", "/help");
            Check("GET /help -> 200", help.Status == 200);
            Check("GET /help lists every action route",
                help.Body.Contains("/action/place") && help.Body.Contains("/action/start")
                && help.Body.Contains("/action/move") && help.Body.Contains("/action/click"));

            var unknown = Call("GET", "/nope");
            Check("unknown route -> 404", unknown.Status == 404, unknown.Status + " " + unknown.Body);

            var wrongMethod = Call("POST", "/state", "{}");
            Check("POST /state -> 405", wrongMethod.Status == 405, wrongMethod.Status + " " + wrongMethod.Body);

            var noBoard = Call("GET", "/state");
            Check("GET /state with no board -> 503", noBoard.Status == 503, noBoard.Status + " " + noBoard.Body);
            Check("error body is JSON with a message",
                noBoard.Body.Contains("\"success\": false") && noBoard.Body.Contains("\"error\""), noBoard.Body);

            var noSlot = Call("POST", "/action/place", "{\"token\":\"!x\"}");
            Check("place without slot -> 400", noSlot.Status == 400, noSlot.Status + " " + noSlot.Body);
            var noStart = Call("POST", "/action/start", "{}");
            Check("start without token -> 400", noStart.Status == 400, noStart.Status + " " + noStart.Body);
            var noMove = Call("POST", "/action/move", "{\"token\":\"!x\"}");
            Check("move without destination -> 400", noMove.Status == 400, noMove.Status + " " + noMove.Body);
            var noClick = Call("POST", "/action/click", "{}");
            Check("click without token -> 400", noClick.Status == 400, noClick.Status + " " + noClick.Body);
            var badJson = Call("POST", "/action/place", "{oops");
            Check("malformed body -> 400", badJson.Status == 400, badJson.Status + " " + badJson.Body);

            // --- bring up the fake board ----------------------------------------------
            object board = cs_gameapi_fake.SampleBoard.Build();

            Section("game type bridging");
            Type rootType = Reflect.FindType("SecretHistories.Entities.FucineRoot");
            Check("FucineRoot resolved by bare name", rootType != null, "null");
            Check("FucineRoot.Get() now returns the board", rootType != null && Reflect.CallStatic(rootType, "Get") != null);

            object path = Reflect.MakeFucinePath("~/tabletop!work_0/work");
            Check("FucinePath constructed from a path string",
                path != null && path.ToString() == "~/tabletop!work_0/work", path == null ? "null" : path.ToString());

            object ctx = Reflect.MakeContext("Debug");
            Check("Context constructed with ActionSource", ctx != null && ctx.ToString().Contains("Debug"),
                ctx == null ? "null" : ctx.ToString());

            var spheres = Reflect.AllSpheres();
            Check("HornedAxe.GetSpheres() returns every fixture sphere", spheres.Count == 8, "got " + spheres.Count);

            object slot = Reflect.SphereByPath("~/tabletop!work_0/work");
            Check("SphereByPath resolves a slot", slot != null);
            Check("SpherePath round-trips", Reflect.SpherePath(slot) == "~/tabletop!work_0/work", Reflect.SpherePath(slot));

            try
            {
                Reflect.SphereByPath("~/does_not_exist");
                Check("SphereByPath on a bad path raises 404", false);
            }
            catch (ApiException e)
            {
                Check("SphereByPath on a bad path raises 404", e.Status == 404, "got " + e.Status);
            }

            // Regression: the catalogue answers an unknown path with a fallback sphere, so a
            // path validation that only checks for null silently writes to the wrong sphere.
            object validPath = null;
            try { validPath = Reflect.SphereByPath("~/tabletop"); } catch { }
            Check("SphereByPath still resolves a real path", validPath != null);

            object foundToken = GameActions.FindToken("!funds_1");
            Check("FindToken locates a card", foundToken != null);
            Check("FindToken returns null for a ghost", GameActions.FindToken("!ghost") == null);

            // Regression: the real Sphere.GetTokenById() returns a NullToken (payload type
            // NullElementStack) instead of null when it cannot find a token. FindToken used to
            // accept that as a hit, which made every action operate on a useless token.
            object notATwo = GameActions.FindToken("!not_a_two");
            Check("FindToken ignores a NullToken from GetTokenById", notATwo != null,
                notATwo == null ? "returned null instead of the real token" : "ok");
            if (notATwo != null)
            {
                object payload = Reflect.Get(notATwo, "Payload");
                string payloadType = payload == null ? "<null>" : payload.GetType().Name;
                Check("FindToken returned the real payload, not NullElementStack",
                    payloadType != "NullElementStack", "payloadType=" + payloadType);
                Check("FindToken returned the token that actually matches the id",
                    Reflect.GetString(notATwo, "PayloadId") == "!not_a_two",
                    "PayloadId=" + Reflect.GetString(notATwo, "PayloadId"));
            }

            // --- state serialisation ---------------------------------------------------
            Section("state serialisation");
            var rich = Call("GET", "/state");
            Check("GET /state -> 200 on a live board", rich.Status == 200, rich.Status + " " + rich.Body);

            // Compactness: the wire body must not be indented. Indentation used to cost roughly a
            // third of the payload, which matters when an agent reads the whole board every turn.
            Check("GET /state is sent compact, not indented",
                rich.RawBody != null && rich.RawBody.IndexOf("\n  ", StringComparison.Ordinal) < 0,
                "raw body contains indentation");
            Check("token state is the short class name, not the CLR full name",
                rich.Body.IndexOf("SecretHistories.States.", StringComparison.Ordinal) < 0,
                "a token state still carries its namespace");

            // The default response lists only EMPTY spheres; cards and verbs come from tokens[].
            var lean = Json.ParseObject(rich.Body);
            Check("default /state still reports the true sphereCount",
                Json.Int(lean, "sphereCount") == 8, "got " + Json.Int(lean, "sphereCount"));
            var leanSpheres = lean["spheres"] as JArray;
            Check("default /state omits non-empty spheres",
                leanSpheres != null && leanSpheres.Count < 8,
                leanSpheres == null ? "null" : leanSpheres.Count + " sphere(s) listed");
            Check("default /state says how many were omitted",
                Json.Int(lean, "spheresOmitted") > 0, "spheresOmitted=" + Json.Int(lean, "spheresOmitted"));
            Check("default /state keeps every token",
                (lean["tokens"] as JArray) != null && ((JArray)lean["tokens"]).Count == 4,
                "tokens=" + ((lean["tokens"] as JArray)?.Count ?? -1));
            Check("default /state still builds the tabletop view",
                lean["tabletop"] != null && (int)lean["tabletop"]["tokenCount"] == 3,
                lean["tabletop"] == null ? "null" : "present");

            var fullState = Call("GET", "/state?full=1");
            Check("GET /state?full=1 -> 200", fullState.Status == 200, fullState.Status + " " + fullState.Body);
            Check("full /state returns every sphere",
                (Json.ParseObject(fullState.Body)["spheres"] as JArray)?.Count == 8,
                "spheres=" + ((Json.ParseObject(fullState.Body)["spheres"] as JArray)?.Count ?? -1));

            var briefState = Call("GET", "/state?brief=1");
            Check("GET /state?brief=1 -> 200", briefState.Status == 200, briefState.Status + " " + briefState.Body);
            // Sphere entries carry their own "description", so look inside tokens[] only.
            bool AnyTokenDescription(string body)
            {
                var arr = Json.ParseObject(body)["tokens"] as JArray;
                if (arr == null) return false;
                foreach (var tok in arr) if (tok["description"] != null) return true;
                return false;
            }
            Check("brief drops card descriptions", !AnyTokenDescription(briefState.Body), "a token still has a description in brief mode");
            Check("default keeps card descriptions", AnyTokenDescription(rich.Body), "default mode lost token descriptions");
            Check("brief keeps aspects (slot matching still works)",
                briefState.RawBody.IndexOf("\"aspects\"", StringComparison.Ordinal) >= 0,
                "aspects missing in brief mode");
            Check("brief is smaller than the default payload",
                briefState.RawBody.Length < rich.RawBody.Length,
                "brief=" + briefState.RawBody.Length + " default=" + rich.RawBody.Length);

            var parsed = Json.ParseObject(fullState.Body);
            Check("sphereCount = 8", Json.Int(parsed, "sphereCount") == 8, "got " + Json.Int(parsed, "sphereCount"));
            Check("tokenCount = 4", Json.Int(parsed, "tokenCount") == 4, "got " + Json.Int(parsed, "tokenCount"));

            var tokens = parsed["tokens"] as JArray;
            Check("tokens array present", tokens != null && tokens.Count == 4, tokens == null ? "null" : tokens.Count.ToString());

            var fundsToken = tokens != null ? tokens.FirstOrDefault(t => (string)t["id"] == "!funds_1") : null;
            Check("card id serialised", fundsToken != null);
            if (fundsToken != null)
            {
                Check("card entityId serialised", (string)fundsToken["entityId"] == "funds");
                Check("card label serialised (UTF-8 survives)", (string)fundsToken["label"] == "资金", (string)fundsToken["label"]);
                Check("card position serialised", fundsToken["position"] != null && (double)fundsToken["position"]["x"] == -150.0);
                Check("card aspects serialised", fundsToken["aspects"] != null && (int)fundsToken["aspects"]["funds"] == 2);
                Check("card draggable flag serialised", (bool)fundsToken["draggable"] == true);
                Check("card spherePath serialised", (string)fundsToken["spherePath"] == "~/tabletop");
                Check("card payloadType serialised", (string)fundsToken["payloadType"] == "ElementStack");
            }

            var situationToken = tokens != null ? tokens.FirstOrDefault(t => (string)t["payloadType"] == "Situation") : null;
            Check("situation serialised", situationToken != null);
            if (situationToken != null)
            {
                Check("situation verbId", (string)situationToken["verbId"] == "work");
                Check("situation recipe id", (string)situationToken["currentRecipeId"] == "nullrecipe");
                Check("situation timer", (double)situationToken["timeRemaining"] == 0.0);
                var slots = situationToken["slots"] as JArray;
                // The real game gives a situation several dominions, most of them empty, so the
                // slot list is longer than the number of visible card frames.
                Check("situation exposes its slots", slots != null && slots.Count >= 2, slots == null ? "null" : slots.Count.ToString());
                if (slots != null && slots.Count >= 1)
                {
                    var workSlotJson = slots.FirstOrDefault(s => (string)s["path"] == "~/tabletop!work_0/work");
                    Check("slot path is a FucinePath", workSlotJson != null);
                    Check("empty slot flagged", workSlotJson != null && (bool)workSlotJson["empty"] == true);
                    Check("slot carries its spec id", workSlotJson != null && (string)workSlotJson["specId"] == "work");
                    Check("slot carries its dominion", workSlotJson != null && (string)workSlotJson["dominion"] == "VerbThresholds");
                }
            }

            Check("tabletop view present", parsed["tabletop"] != null && (int)parsed["tabletop"]["tokenCount"] == 3,
                parsed["tabletop"] == null ? "null" : parsed["tabletop"]["tokenCount"].ToString());
            Check("plumbing sphere flagged", (bool)((JArray)parsed["spheres"]).First(s => (string)s["path"] == "~/windows")["plumbing"] == true);
            Check("root sphere path reported", (string)parsed["rootSpherePath"] != null);

            // The clock readout. Without it a caller cannot tell a stalled game from a slow one,
            // which is exactly how an API-driven session sat frozen with no explanation.
            Check("state carries a playback block", parsed["playback"] != null,
                parsed["playback"] == null ? "null" : "present");
            Check("state reports open verb windows", parsed["openWindows"] != null,
                parsed["openWindows"] == null ? "null" : "present");

            // Reading the pause state must never throw, even with no Heart in the scene.
            var playbackSnapshot = Playback.Snapshot();
            Check("Playback.Snapshot() is safe without a Heart component", playbackSnapshot != null);

            // --- actions ---------------------------------------------------------------
            Section("actions");
            var place = Call("POST", "/action/place", "{\"token\":\"!funds_1\",\"slot\":\"~/tabletop!work_0/work\"}");
            Check("place -> 200", place.Status == 200, place.Body);
            Check("place reports accepted=true", place.Body.Contains("\"accepted\": true"), place.Body);
            Check("place moved the card into the slot",
                Board(board).SphereAt("~/tabletop!work_0/work").Tokens.Count == 1, place.Body);
            Check("place removed the card from the tabletop",
                Board(board).SphereAt("~/tabletop").Tokens.Count == 2, place.Body);

            var start = Call("POST", "/action/start", "{\"token\":\"!work_0\"}");
            Check("start -> 200", start.Status == 200, start.Body);
            Check("start invoked Situation.TryStart exactly once",
                Board(board).Situation("!work_0").TryStartCalls == 1, start.Body);
            Check("start reports the verb id", start.Body.Contains("\"verbId\": \"work\""), start.Body);
            Check("start reports the new state", start.Body.Contains("\"stateAfter\": \"Ongoing\""), start.Body);
            Check("start reports a clean state name (not a type name)", !start.Body.Contains("SecretHistories.States"), start.Body);
            Check("start reports stateBefore", start.Body.Contains("\"stateBefore\": \"Unstarted\""), start.Body);

            var startOnCard = Call("POST", "/action/start", "{\"token\":\"!introjob_2\"}");
            Check("start on a non-situation -> 409", startOnCard.Status == 409, startOnCard.Body);

            var move = Call("POST", "/action/move", "{\"token\":\"!funds_1\",\"to\":\"~/tabletop\"}");
            Check("move -> 200", move.Status == 200, move.Body);
            Check("move reports success", move.Body.Contains("\"success\": true"), move.Body);
            Check("move reports the origin", move.Body.Contains("\"from\": \"~/tabletop!work_0/work\""), move.Body);
            Check("move emptied the slot", Board(board).SphereAt("~/tabletop!work_0/work").Tokens.Count == 0, move.Body);
            Check("move landed on the tabletop", Board(board).SphereAt("~/tabletop").Tokens.Count == 3, move.Body);
            Check("move used the animated itinerary", move.Body.Contains("\"viaItinerary\": true"), move.Body);

            var movePos = Call("POST", "/action/move", "{\"token\":\"!introjob_2\",\"x\":42,\"y\":-7}");
            Check("position-only move -> 200", movePos.Status == 200, movePos.Body);

            var moveNoop = Call("POST", "/action/move", "{\"token\":\"!funds_1\",\"to\":\"~/tabletop\"}");
            Check("move to the same sphere is a no-op success",
                moveNoop.Status == 200 && moveNoop.Body.Contains("\"noop\": true"), moveNoop.Body);

            var click = Call("POST", "/action/click", "{\"token\":\"!work_0\"}");
            Check("click on a situation -> 200", click.Status == 200, click.Body);
            Check("click opened the situation", Board(board).Situation("!work_0").OpenCalls == 1, click.Body);
            Check("click reports the action", click.Body.Contains("\"action\": \"open\""), click.Body);

            var clickCard = Call("POST", "/action/click", "{\"token\":\"!funds_1\"}");
            Check("click on a card -> 200", clickCard.Status == 200, clickCard.Body);
            Check("click selected the card", (bool)clickCard.Body.Contains("\"action\": \"select\""), clickCard.Body);

            var rejected = Call("POST", "/action/place", "{\"token\":\"!funds_1\",\"slot\":\"~/tabletop!work_0/locked\"}");
            Check("rejecting slot -> 200 with accepted=false",
                rejected.Status == 200 && rejected.Body.Contains("\"accepted\": false"), rejected.Body);
            Check("rejecting slot explains itself", rejected.Body.Contains("\"reason\""), rejected.Body);

            Check("place into an unknown sphere -> 404",
                Call("POST", "/action/place", "{\"token\":\"!funds_1\",\"slot\":\"~/nowhere\"}").Status == 404);
            Check("start on an unknown token -> 404",
                Call("POST", "/action/start", "{\"token\":\"!ghost\"}").Status == 404);
            Check("move to an unknown sphere -> 404",
                Call("POST", "/action/move", "{\"token\":\"!funds_1\",\"to\":\"~/nowhere\"}").Status == 404);

            // --- game start / resume from the main menu ---------------------------------
            Section("game start and resume");
            var legacies = Call("GET", "/legacies");
            Check("GET /legacies -> 200", legacies.Status == 200, legacies.Status + " " + legacies.Body);
            var legacyJson = Json.ParseObject(legacies.Body);
            Check("legacies are listed", Json.Int(legacyJson, "count") == 2, legacies.Body);
            Check("the default legacy is flagged", Json.Str(legacyJson, "defaultLegacyId") == "aspirant", legacies.Body);
            Check("/legacies reports a loaded tabletop", Json.Bool(legacyJson, "inGame") == true, legacies.Body);

            // A game is loaded in this fake, but the start endpoints deliberately do NOT guard on
            // that: every heuristic for "in a game" misjudged the menu, and the game itself
            // guards via canTakeInput. So these must go through and report what happened.
            var busy = Call("POST", "/action/newgame", "{\"legacy\":\"aspirant\"}");
            Check("newgame is not blocked by a heuristic 409", busy.Status == 200, busy.Status + " " + busy.Body);
            Check("newgame reports whether input was accepted", busy.Body.Contains("\"inputAccepted\""), busy.Body);

            var screen = Call("GET", "/screen");
            Check("GET /screen -> 200", screen.Status == 200, screen.Status + " " + screen.Body);
            Check("/screen reports the scene name", screen.Body.Contains("\"scene\": \"Menu\""), screen.Body);
            Check("/screen reports the menu scene as active", screen.Body.Contains("\"menuSceneActive\": true"), screen.Body);

            // Simulate standing on the main menu: no board exists yet.
            ClearBoard();
            Console.WriteLine("    [debug] after ClearBoard: HasActiveGame=" + GameStateSerializer.HasActiveGame()
                + " spheres=" + Reflect.AllSpheres().Count);
            foreach (object dbg in Reflect.AllSpheres())
            {
                Console.WriteLine("    [debug]   sphere " + Reflect.SpherePath(dbg)
                    + " tokens=" + Reflect.ToList(Reflect.Get(dbg, "Tokens")).Count);
            }
            SecretHistories.Infrastructure.MenuScreenController.BeginDefaultCalls = 0;
            SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls = 0;
            SecretHistories.Infrastructure.MenuScreenController.ContinueCalls = 0;

            var atMenu = Call("GET", "/state");
            Check("/state on the main menu -> 503", atMenu.Status == 503, atMenu.Status + " " + atMenu.Body);

            var menuLegacies = Call("GET", "/legacies");
            Check("/legacies still works on the main menu", menuLegacies.Status == 200, menuLegacies.Body);

            var badLegacy = Call("POST", "/action/newgame", "{\"legacy\":\"not_a_legacy\"}");
            Check("newgame with an unknown legacy -> 404", badLegacy.Status == 404, badLegacy.Status + " " + badLegacy.Body);
            Check("an unknown legacy never reaches the game",
                SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls == 0);
            var newGame = Call("POST", "/action/newgame", "{\"legacy\":\"adept\"}");
            Check("newgame with a known legacy -> 200", newGame.Status == 200, newGame.Status + " " + newGame.Body);
            Check("newgame called BeginNewSaveWithSpecifiedLegacy",
                SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls == 1,
                "calls=" + SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls);
            Check("newgame passed the requested legacy id",
                SecretHistories.Infrastructure.MenuScreenController.LastLegacyId == "adept",
                "got " + SecretHistories.Infrastructure.MenuScreenController.LastLegacyId);

            var defaultGame = Call("POST", "/action/newgame", "{}");
            Check("newgame with no legacy -> 200", defaultGame.Status == 200, defaultGame.Status + " " + defaultGame.Body);
            Check("newgame used the default-legacy path",
                SecretHistories.Infrastructure.MenuScreenController.BeginDefaultCalls == 1,
                "calls=" + SecretHistories.Infrastructure.MenuScreenController.BeginDefaultCalls);

            var resume = Call("POST", "/action/continue", "{}");
            Check("continue on the main menu -> 200", resume.Status == 200, resume.Status + " " + resume.Body);
            Check("continue called MenuScreenController.ContinueGame",
                SecretHistories.Infrastructure.MenuScreenController.ContinueCalls == 1,
                "calls=" + SecretHistories.Infrastructure.MenuScreenController.ContinueCalls);

            // Regression: FucineRoot exists on the menu and credits screens too (which carry
            // ~/cardsexhibit), so "root exists" is not a usable "a game is in progress" test -
            // it made /action/newgame answer 409 on the very screen it exists for.
            SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls = 0;
            cs_gameapi_fake.SampleBoard.BuildExhibitOnlyBoard();
            Check("a root with no populated tabletop counts as not in a game",
                !GameStateSerializer.HasActiveGame(),
                "HasActiveGame() returned true on an exhibit-only board");
            var newGameOnExhibit = Call("POST", "/action/newgame", "{\"legacy\":\"aspirant\"}");
            Check("newgame is allowed on an exhibit-only screen", newGameOnExhibit.Status == 200,
                newGameOnExhibit.Status + " " + newGameOnExhibit.Body);
            Check("newgame reached the game on an exhibit-only screen",
                SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls == 1,
                "calls=" + SecretHistories.Infrastructure.MenuScreenController.BeginSpecifiedCalls);

            // Put the board back: this fake has no real scene loading, so restore it by hand.
            RestoreBoard();

            // --- continue when the menu is NOT in the scene -------------------------------
            // The menu controller only exists while the menu scene is loaded, so resume has to be
            // able to work without it. In the harness FindComponentInScene finds nothing (the
            // Unity stubs have no scene search), which is exactly the no-menu case.
            SecretHistories.Infrastructure.MenuScreenController.ContinueCalls = 0;
            SecretHistories.Services.StageHand.LoadGameOnTabletopCalls = 0;
            SecretHistories.Infrastructure.Persistence.DefaultGamePersistenceProvider.Constructed = 0;

            var stillAtMenu = Call("GET", "/screen");
            Check("/screen reports no menu controller available in this fake",
                stillAtMenu.Status == 200, stillAtMenu.Body);

            // Hide the controller the way an unloaded menu scene would.
            SecretHistories.UI.Watchman.SimulateMenuControllerAbsent = true;
            var resumeNoMenu = Call("POST", "/action/continue", "{}");
            SecretHistories.UI.Watchman.SimulateMenuControllerAbsent = false;
            Check("continue works without the menu controller -> 200", resumeNoMenu.Status == 200,
                resumeNoMenu.Status + " " + resumeNoMenu.Body);
            Check("continue without a menu built a DefaultGamePersistenceProvider",
                SecretHistories.Infrastructure.Persistence.DefaultGamePersistenceProvider.Constructed >= 1,
                "constructed=" + SecretHistories.Infrastructure.Persistence.DefaultGamePersistenceProvider.Constructed);
            Check("continue without a menu called StageHand.LoadGameOnTabletop",
                SecretHistories.Services.StageHand.LoadGameOnTabletopCalls >= 1,
                "calls=" + SecretHistories.Services.StageHand.LoadGameOnTabletopCalls);
            Check("continue reports which path it took", resumeNoMenu.Body.Contains("\"via\""), resumeNoMenu.Body);

            // --- timeout ---------------------------------------------------------------
            Section("main-thread backpressure");
            int savedTimeout = ApiConfig.RequestTimeoutMs;
            ApiConfig.RequestTimeoutMs = 300;
            pump.Pause();
            var timedOut = Call("GET", "/state");
            Check("a request the game never services -> 504", timedOut.Status == 504, timedOut.Status + " " + timedOut.Body);
            pump.Resume();

            // The queued command is still sitting there; let the pump drain it.
            Thread.Sleep(200);
            ApiConfig.RequestTimeoutMs = savedTimeout;
            Check("server recovers after the timeout", Call("GET", "/state").Status == 200);

            Check("request counter advanced", server.RequestsServed >= 20, "got " + server.RequestsServed);

            server.Stop();
            FucineRootReset();
            Finish();
            return _failed == 0 ? 0 : 1;
        }

        // --- helpers ---------------------------------------------------------------------

        private static cs_gameapi_fake.SampleBoard.Board Board(object b)
        {
            return (cs_gameapi_fake.SampleBoard.Board)b;
        }

        private static void FucineRootReset()
        {
            try
            {
                Type t = Reflect.FindType("SecretHistories.Entities.FucineRoot");
                if (t != null) t.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            catch { }
        }

        /// <summary>Clear the board so the mod sees a "main menu" world, then put it back.</summary>
        private static void ClearBoard()
        {
            FucineRootReset();
        }

        private static void RestoreBoard()
        {
            cs_gameapi_fake.SampleBoard.Build();
        }

        private static Type FindModType()
        {
            // Exactly what ModManager.TryInitialiseAssembly does: Assembly.GetType(<name alnum>).
            // This must resolve for the real game loader too, so it is worth asserting.
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(ModId, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private class HttpResult
        {
            public int Status;
            /// <summary>Re-indented copy of the body, used by the substring assertions below.</summary>
            public string Body;
            /// <summary>Exactly what came off the wire.</summary>
            public string RawBody;
        }

        /// <summary>
        /// Hand-rolled HTTP/1.1 over a raw socket.
        ///
        /// System.Net.WebRequest is unusable here: this Unity-flavoured mono build has a
        /// WebRequest.PrefixList that pokes at a Console field it does not ship, and it throws
        /// MissingFieldException on the first call. TcpClient has no such dependency.
        /// </summary>
        private static HttpResult Call(string method, string path, string body = null)
        {
            var uri = new Uri(Base + path);
            using (var client = new System.Net.Sockets.TcpClient())
            {
                client.Connect(uri.Host, uri.Port);
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 20000;

                using (var stream = client.GetStream())
                {
                    byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                    var head = new StringBuilder();
                    head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                    head.Append("Host: ").Append(uri.Host).Append(':').Append(uri.Port).Append("\r\n");
                    head.Append("Accept: application/json\r\n");
                    head.Append("Connection: close\r\n");
                    if (body != null)
                    {
                        head.Append("Content-Type: application/json\r\n");
                        head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
                    }
                    head.Append("\r\n");

                    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    stream.Write(headBytes, 0, headBytes.Length);
                    if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();

                    var raw = new MemoryStream();
                    var buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        raw.Write(buffer, 0, read);
                    }

                    return ParseResponse(Encoding.UTF8.GetString(raw.ToArray()));
                }
            }
        }

        private static HttpResult ParseResponse(string raw)
        {
            int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) throw new InvalidOperationException("malformed HTTP response: " + Truncate(raw));

            string headerBlock = raw.Substring(0, headerEnd);
            string body = raw.Substring(headerEnd + 4);
            var lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);

            int status = 0;
            string[] statusParts = lines[0].Split(' ');
            if (statusParts.Length >= 2) int.TryParse(statusParts[1], out status);

            bool chunked = false;
            foreach (string line in lines)
            {
                if (line.ToLowerInvariant().StartsWith("transfer-encoding:") && line.ToLowerInvariant().Contains("chunked"))
                    chunked = true;
            }
            if (chunked) body = Dechunk(body);

            // The server now emits COMPACT JSON to keep the payload small, but the assertions in
            // this file were written against indented JSON ("\"success\": true"). Keep the raw
            // bytes for the compactness checks and hand the assertions a re-indented copy, so the
            // tests keep asserting behaviour instead of whitespace.
            string pretty = body;
            try { pretty = Newtonsoft.Json.Linq.JToken.Parse(body).ToString(Newtonsoft.Json.Formatting.Indented); }
            catch { }

            return new HttpResult { Status = status, Body = pretty, RawBody = body };
        }

        private static string Dechunk(string body)
        {
            var result = new StringBuilder();
            int index = 0;
            while (index < body.Length)
            {
                int lineEnd = body.IndexOf("\r\n", index, StringComparison.Ordinal);
                if (lineEnd < 0) break;
                string sizeLine = body.Substring(index, lineEnd - index).Trim();
                int semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                int size;
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out size) || size == 0)
                    break;
                int start = lineEnd + 2;
                if (start + size > body.Length) size = body.Length - start;
                result.Append(body.Substring(start, size));
                index = start + size + 2;
            }
            return result.ToString();
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name + " ==");
        }

        private static void Check(string description, bool ok, string detail = null)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("  PASS  " + description);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + description + (detail != null ? "   -> " + Truncate(detail) : ""));
            }
        }

        private static string Truncate(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 240 ? s : s.Substring(0, 240) + "...";
        }

        private static void Finish()
        {
            Console.WriteLine();
            Console.WriteLine("passed=" + _passed + " failed=" + _failed);
            try
            {
                Directory.CreateDirectory(@"F:\WorkSpace\cs_gameapi\logs");
                File.WriteAllText(@"F:\WorkSpace\cs_gameapi\logs\harness.txt",
                    "passed=" + _passed + " failed=" + _failed + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Stands in for the mod's Update() pump, with a pause switch for timeout tests.</summary>
    internal sealed class PumpThread
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<GameCommand> _queue;
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(true);
        private readonly Thread _thread;

        public PumpThread(System.Collections.Concurrent.ConcurrentQueue<GameCommand> queue)
        {
            _queue = queue;
            _thread = new Thread(Loop) { IsBackground = true, Name = "harness-pump" };
        }

        public void Start() { _thread.Start(); }

        public void Pause() { _gate.Reset(); }

        public void Resume() { _gate.Set(); }

        private void Loop()
        {
            while (true)
            {
                _gate.Wait();
                GameCommand cmd;
                if (_queue.TryDequeue(out cmd))
                {
                    try { cmd.Complete(cmd.Work()); }
                    catch (Exception e) { cmd.Fail(Reflect.Unwrap(e)); }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
    }
}
