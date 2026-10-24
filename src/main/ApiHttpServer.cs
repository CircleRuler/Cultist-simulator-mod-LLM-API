using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CsGameApi
{
    /// <summary>
    /// A request that must be executed on the Unity main thread, with the caller blocking on
    /// the response. The HTTP thread enqueues one of these and waits; the mod's Update pump
    /// completes it.
    /// </summary>
    internal sealed class GameCommand
    {
        public Func<Dictionary<string, object>> Work;
        public Dictionary<string, object> Result;
        public Exception Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

        public void Complete(Dictionary<string, object> result)
        {
            Result = result;
            Done.Set();
        }

        public void Fail(Exception error)
        {
            Error = error;
            Done.Set();
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on localhost, bound to 127.0.0.1 only.
    ///
    /// It deliberately knows nothing about the game: it parses the route, turns mutating calls
    /// into GameCommands for the main thread, and serialises whatever comes back.
    /// </summary>
    internal sealed class ApiHttpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentQueue<GameCommand> _queue;
        private Thread _thread;
        private volatile bool _stopping;
        private long _requestsServed;

        public ApiHttpServer(int port, ConcurrentQueue<GameCommand> queue)
        {
            _queue = queue;
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public int Port { get; private set; }

        public long RequestsServed { get { return Interlocked.Read(ref _requestsServed); } }

        public bool Start(out string error)
        {
            error = null;
            try
            {
                _listener.Start();
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            foreach (string prefix in _listener.Prefixes)
            {
                int colon = prefix.LastIndexOf(':');
                int slash = prefix.IndexOf('/', colon);
                int port;
                if (colon >= 0 && slash > colon && int.TryParse(prefix.Substring(colon + 1, slash - colon - 1), out port))
                {
                    Port = port;
                }
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "LLM_API-http" };
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void Loop()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (Exception)
                {
                    if (_stopping) return;
                    Thread.Sleep(50);
                    continue;
                }

                Interlocked.Increment(ref _requestsServed);
                // Handle inline: responses are cheap and requests are serialised by design,
                // so a client can never observe a partially applied action.
                try { Handle(ctx); }
                catch (Exception e) { Reflect.LogError("unhandled in http loop", e); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string method = ctx.Request.HttpMethod;
            string path = (ctx.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            int status = 200;
            object payload;
            try
            {
                string body = ReadBody(ctx.Request);
                payload = Route(method, path, body, WantsFlag(ctx.Request, "full", "detail"), WantsFlag(ctx.Request, "brief", "lean"));
            }
            catch (ApiException e)
            {
                status = e.Status;
                payload = ErrorBody(e.Message);
            }
            catch (Exception e)
            {
                Exception inner = Reflect.Unwrap(e);
                status = 500;
                payload = ErrorBody(inner.Message);
                Reflect.LogError("route " + method + " " + path, inner);
            }

            Write(ctx, status, payload);
        }

        private static readonly HashSet<string> GetRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/", "/help", "/ping", "/state", "/legacies", "/screen"
        };

        private static readonly HashSet<string> PostRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/action/place", "/action/start", "/action/move", "/action/click",
            "/action/newgame", "/action/continue", "/action/menu",
            "/action/unpause", "/action/speed",
            "/action/conclude", "/action/collect", "/action/closewindows",
            "/action/retire", "/action/closeworld", "/action/tidy", "/diagnose", "/selftest"
        };

        /// <summary>
        /// Whether the caller asked for the complete payload - ?full=1 / ?full=true / ?full
        /// (also accepted as ?detail=full). /state omits non-empty sphere entries by default;
        /// measured, that is 32.9 KB of a 188.7 KB response.
        /// </summary>
        private static bool WantsFlag(HttpListenerRequest request, string primary, string alias)
        {
            try
            {
                string v = request.QueryString[primary];
                if (v == null) v = request.QueryString[alias];
                if (v == null) return false;
                if (v.Length == 0) return true;          // "?full" with no value counts as set
                v = v.Trim();
                return string.Equals(v, "1", StringComparison.Ordinal)
                    || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v, "full", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private object Route(string method, string path, string body, bool full, bool brief)
        {
            if (method == "OPTIONS") return new Dictionary<string, object> { { "success", true } };

            // Resolve the path before the method, so an unknown route is a 404 even when it was
            // called with the wrong verb.
            if (!GetRoutes.Contains(path) && !PostRoutes.Contains(path))
                throw new ApiException(404, "unknown route: " + path + " (GET /help for the route list)");

            if (method == "GET")
            {
                if (!GetRoutes.Contains(path))
                    throw new ApiException(405, path + " is a POST route; send a JSON body");

                switch (path)
                {
                    case "/":
                    case "/help":
                        return Help();

                    case "/ping":
                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "service", "LLM_API" },
                            { "requestsServed", RequestsServed },
                            { "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                        };

                    case "/state":
                        return OnGameThread(() => GameStateSerializer.Capture(full, brief));

                    case "/legacies":
                        return OnGameThread(() => GameActions.ListLegacies());

                    case "/screen":
                        return OnGameThread(() => GameActions.Screen());
                }
            }

            if (method != "POST")
                throw new ApiException(405, "unsupported method " + method);

            if (!PostRoutes.Contains(path))
                throw new ApiException(405, path + " is a GET route; call it without a body");

            var o = Json.ParseObject(body);

            switch (path)
            {
                case "/action/place":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "card");
                        string slot = Json.Str(o, "slot") ?? Json.Str(o, "sphere") ?? Json.Str(o, "to");
                        return OnGameThread(() => GameActions.Place(token, slot));
                    }

                case "/action/start":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "situation") ?? Json.Str(o, "verb");
                        string recipe = Json.Str(o, "recipe") ?? Json.Str(o, "recipeId");
                        return OnGameThread(() => GameActions.Start(token, recipe));
                    }

                case "/action/move":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "card");
                        string to = Json.Str(o, "to") ?? Json.Str(o, "slot") ?? Json.Str(o, "sphere");
                        float? x = Json.Float(o, "x");
                        float? y = Json.Float(o, "y");
                        return OnGameThread(() => GameActions.Move(token, to, x, y));
                    }

                case "/action/click":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "card");
                        return OnGameThread(() => GameActions.Click(token));
                    }

                case "/action/newgame":
                    {
                        string legacy = Json.Str(o, "legacy") ?? Json.Str(o, "legacyId");
                        return OnGameThread(() => GameActions.NewGame(legacy));
                    }

                case "/action/continue":
                    return OnGameThread(() => GameActions.ContinueGame());

                case "/action/menu":
                    return OnGameThread(() => Playback.GoToMenu());

                case "/action/unpause":
                    return OnGameThread(() => Playback.Unpause());

                case "/action/conclude":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "situation");
                        return OnGameThread(() => GameActions.Conclude(token));
                    }

                case "/action/collect":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "situation");
                        return OnGameThread(() => GameActions.Collect(token));
                    }

                case "/action/closewindows":
                    {
                        string except = Json.Str(o, "except");
                        return OnGameThread(() => GameActions.CloseWindows(except));
                    }

                case "/action/retire":
                    {
                        string token = Json.Str(o, "token") ?? Json.Str(o, "card");
                        return OnGameThread(() => GameActions.Retire(token));
                    }

                case "/action/tidy":
                    return OnGameThread(() => GameActions.Tidy());

                case "/action/closeworld":
                    return OnGameThread(() => GameActions.CloseWorld());

                case "/diagnose":
                    return OnGameThread(() => GameActions.Diagnose());

                case "/action/speed":
                    {
                        string speed = Json.Str(o, "speed") ?? Json.Str(o, "gameSpeed");
                        bool withSfx = Json.Bool(o, "withSfx") ?? true;
                        if (string.IsNullOrEmpty(speed))
                            throw new ApiException(400, "'speed' is required, e.g. {\"speed\":\"Normal\"}");
                        return OnGameThread(() => Playback.SetSpeed(speed, withSfx));
                    }

                case "/selftest":
                    {
                        bool replay = Json.Bool(o, "replay") ?? false;
                        // Runs on the game thread and reports back through the same queue, so a
                        // client can ask for a health check and read the result in one call.
                        return OnGameThread(() =>
                        {
                            SelfTest.Run(replay);
                            return SelfTest.LastReport();
                        });
                    }
            }

            throw new ApiException(404, "unknown route: " + path);
        }

        /// <summary>Hand work to the Unity main thread and block until it has run.</summary>
        private Dictionary<string, object> OnGameThread(Func<Dictionary<string, object>> work)
        {
            var command = new GameCommand { Work = work };
            _queue.Enqueue(command);

            if (!command.Done.Wait(ApiConfig.RequestTimeoutMs))
                throw new ApiException(504, "the game did not process the request within " + ApiConfig.RequestTimeoutMs + "ms; is the game paused on a loading screen?");

            if (command.Error != null)
            {
                var api = command.Error as ApiException;
                if (api != null) throw api;
                throw new ApiException(500, command.Error.Message);
            }

            return command.Result ?? new Dictionary<string, object> { { "success", true } };
        }

        private static object Help()
        {
            return new Dictionary<string, object>
            {
                { "service", "LLM_API" },
                { "description", "External API for Cultist Simulator. All addresses are FucinePaths, e.g. '~/tabletop' or '~/tabletop!work_0/work'." },
                { "routes", new object[]
                    {
                        new Dictionary<string, object> { { "method", "GET" },  { "path", "/state" },        { "body", "none; ?full=1 for the complete sphere list, ?brief=1 to drop card descriptions (saves ~10 KB, for token budget)" }, { "returns", "board: tokens (all cards and verbs), empty spheres with their slot requirements, tabletop view, playback. Compact JSON. Non-empty spheres are omitted unless full=1" } },
                        new Dictionary<string, object> { { "method", "GET" },  { "path", "/ping" },         { "body", "none" }, { "returns", "liveness" } },
                        new Dictionary<string, object> { { "method", "GET" },  { "path", "/legacies" },     { "body", "none" }, { "returns", "legacies the player can start, and which one newgame would default to" } },
                        new Dictionary<string, object> { { "method", "GET" },  { "path", "/screen" },       { "body", "none" }, { "returns", "current scene, whether the menu is active, whether a tabletop game is loaded" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/newgame" }, { "body", "{legacy?}" }, { "returns", "start a fresh game from the main menu; omit legacy for the game's default" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/continue" }, { "body", "none" }, { "returns", "resume the existing save, like the Continue button" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/menu" },     { "body", "none" }, { "returns", "leave the tabletop and return to the main menu (so a new game can be started)" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/unpause" },  { "body", "none" }, { "returns", "release a pause and resume at normal speed" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/speed" },    { "body", "{speed}" }, { "returns", "set the game speed by GameSpeed name, e.g. Normal / Fast / Paused" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/conclude" }, { "body", "{token}" }, { "returns", "close a verb's window (the X in its corner) so it stops covering the board" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/collect" },  { "body", "{token?}" }, { "returns", "take cards out of verb output frames and lay them on the tabletop, grouped" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/closewindows" }, { "body", "{except?}" }, { "returns", "close EVERY situation window (no concludable gate); the game re-opens one by itself, so call this each cycle" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/place" }, { "body", "{token, slot}" }, { "returns", "puts a card into a card slot; an OutputSphere is redirected to the EgressThreshold that feeds it" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/start" }, { "body", "{token, recipe?}" }, { "returns", "clicks begin on a verb/situation; 'recipe' names an exact recipe, otherwise an unstartable (hint-only) prediction is skipped for a craftable one" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/retire" }, { "body", "{token}" }, { "returns", "retire a token - removes a card the API can no longer act on" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/closeworld" }, { "body", "none" }, { "returns", "dismiss the mansus (otherworld) overlay - Otherworld.Close(); reports whether IsOpen actually changed" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/tidy" }, { "body", "none" }, { "returns", "merge every stack of identical cards on the tabletop (the tidy button); reports stacks and split stacks before and after" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/move" },  { "body", "{token, to?, x?, y?}" }, { "returns", "move a card: 'to' = destination sphere path (an OutputSphere is redirected to its EgressThreshold); x/y = tabletop-local coordinates, only effective on a sphere that manages its own layout" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/action/click" }, { "body", "{token}" }, { "returns", "primary interaction with a card" } },
                        new Dictionary<string, object> { { "method", "POST" }, { "path", "/selftest" },    { "body", "{replay?}" }, { "returns", "health check; replay=true also tests place/start/move (changes the game)" } }
                    }
                }
            };
        }

        private static Dictionary<string, object> ErrorBody(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", message }
            };
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return "";
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void Write(HttpListenerContext ctx, int status, object payload)
        {
            try
            {
                // Compact, not indented. Indentation cost roughly a third of the payload and carries
            // no information; a 188.7 KB board fitted in considerably less without it.
            string json = Json.Serialize(payload, false);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Reflect.LogError("writing response", e);
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }
    }
}
