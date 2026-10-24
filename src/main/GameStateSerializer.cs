using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CsGameApi
{
    /// <summary>
    /// Reads the live board into plain, JSON-friendly data.
    ///
    /// Runs on the Unity main thread (called from the mod's Update pump), and every field is
    /// read defensively so one unexpected payload type degrades to a missing field rather than
    /// an exception mid-serialisation.
    /// </summary>
    internal static class GameStateSerializer
    {
        /// <summary>Sphere paths that hold the player's real working cards, as opposed to plumbing.</summary>
        private static readonly HashSet<string> PlumbedSpheres = new HashSet<string>(StringComparer.Ordinal)
        {
            "~", "~/windows", "~/enroute"
        };

        /// <summary>
        /// True when a real game is on the table.
        ///
        /// Not simply "does FucineRoot exist": the root is present on the menu and credits
        /// screens too (which carry ~/cardsexhibit), so that test is always true and would block
        /// /action/newgame with a 409 on the very screen it is meant for. A populated tabletop
        /// is the signal that a game is actually underway.
        /// </summary>
        public static bool HasActiveGame()
        {
            try
            {
                foreach (object s in Reflect.AllSpheres())
                {
                    if (Reflect.SpherePath(s) != TabletopSphere) continue;
                    if (Reflect.ToList(Reflect.Get(s, "Tokens")).Count > 0) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>True for null, an empty string, or an empty dictionary/list - i.e. a value
        /// that costs JSON bytes without saying anything.</summary>
        private static bool IsEmptyValue(object v)
        {
            if (v == null) return true;
            string s = v as string;
            if (s != null) return s.Length == 0;
            System.Collections.IDictionary d = v as System.Collections.IDictionary;
            if (d != null) return d.Count == 0;
            System.Collections.ICollection c = v as System.Collections.ICollection;
            if (c != null) return c.Count == 0;
            return false;
        }

        /// <summary>
        /// "SecretHistories.States.TokenStates.DroppedInSphereState" -> "DroppedInSphereState".
        /// The namespace is game-internal noise; the class name is what a caller reasons about.
        /// </summary>
        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            int dot = fullName.LastIndexOf('.');
            if (dot < 0 || dot >= fullName.Length - 1) return fullName;
            return fullName.Substring(dot + 1);
        }

        /// <summary>
        /// The whole board.
        ///
        /// By default the sphere list only describes spheres that are EMPTY. A sphere that holds
        /// cards adds nothing a reader cannot already derive from tokens[] (each token carries its
        /// spherePath), while costing roughly 1.7 KB per entry - measured: 32.9 KB of a 188.7 KB
        /// response, i.e. 17%. Empty spheres are kept because they carry the required/forbidden
        /// aspects a caller needs in order to decide what to put in them.
        ///
        /// Pass full:true for the old, complete sphere list.
        /// </summary>
        public static Dictionary<string, object> Capture(bool full = false, bool brief = false)
        {
            var root = new Dictionary<string, object>();

            object fucineRoot = Reflect.FucineRoot();
            string rootPath = RootPath();

            root["gameVersion"] = SafeString(() => Application.version);
            root["unityVersion"] = SafeString(() => Application.unityVersion);
            root["capturedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Sphere enumeration is the backbone of the whole state: tokens are addressed by
            // "sphere path" + "token id", and that is exactly what actions accept.
            var axes = Reflect.AllSpheres();
            var tokens = new List<object>();
            var sphereList = new List<object>();
            var everySphere = new List<object>();   // unfiltered, used by the tabletop view
            int omitted = 0;

            foreach (object s in axes)
            {
                string path = Reflect.SpherePath(s);

                // Emptiness comes from the token list, not from the game's IsEmpty member:
                // Sphere exposes no such bool, so Reflect.GetBool was silently returning false and
                // every sphere reported "isEmpty": false - including the 35 that hold nothing.
                var sphereTokens = Reflect.ToList(Reflect.Get(s, "Tokens"));
                bool isEmpty = sphereTokens.Count == 0;

                var sphereEntry = DescribeSphere(s, path, isEmpty);
                everySphere.Add(sphereEntry);

                if (full || isEmpty) sphereList.Add(sphereEntry);
                else omitted++;

                foreach (object t in sphereTokens)
                {
                    tokens.Add(DescribeToken(t, s, path, brief));
                }
            }

            root["sphereCount"] = everySphere.Count;
            root["spheres"] = sphereList;
            if (!full)
            {
                root["spheresOmitted"] = omitted;
                root["spheresNote"] = "only EMPTY spheres are listed by default (they carry slot requirements); "
                                    + "cards and verbs are all in tokens[]. GET /state?full=1 for the complete sphere list";
            }
            root["tokenCount"] = tokens.Count;
            root["tokens"] = tokens;

            // A tabletop-only convenience view: the cards a player (or model) actually looks at.
            root["tabletop"] = TabletopView(everySphere, tokens);

            // Tokens in transit. A moved token passes through the game's en-route sphere while
            // its travel animation plays, so a reader can observe a card there with no useful
            // destination. Flag it explicitly rather than letting a caller mistake a transient
            // position for a settled one.
            var inTransit = new List<object>();
            foreach (Dictionary<string, object> t in tokens)
            {
                object pathValue;
                if (t.TryGetValue("spherePath", out pathValue) && Equals(pathValue, EnRouteSphere))
                    inTransit.Add(t.TryGetValue("id", out pathValue) ? pathValue : null);
            }
            root["tokensInTransit"] = inTransit;
            root["enRouteSpherePath"] = EnRouteSphere;

            // Whether the clock is actually running. Without this an external driver cannot tell
            // a stalled game from a slow one - an entire API-driven session was lost to exactly
            // that, with every verb timer frozen and nothing reporting why.
            root["playback"] = Playback.Snapshot();

            // A verb window left open holds the tabletop paused, so name the open ones.
            var openWindows = new List<object>();
            foreach (Dictionary<string, object> t in tokens)
            {
                object isOpen;
                if (t.TryGetValue("isOpen", out isOpen) && isOpen is bool && (bool)isOpen)
                {
                    object id;
                    if (t.TryGetValue("id", out id)) openWindows.Add(id);
                }
            }
            root["openWindows"] = openWindows;

            root["rootSpherePath"] = rootPath;

            return root;
        }

        private const string EnRouteSphere = "~/enroute";
        private const string TabletopSphere = "~/tabletop";

        /// <summary>
        /// The path of the sphere tree's root. Taken from FucinePath.Root() rather than from
        /// whichever sphere happened to be enumerated last - HornedAxe keeps its spheres in a
        /// HashSet, so enumeration order is not stable.
        /// </summary>
        private static string RootPath()
        {
            try
            {
                Type t = Reflect.FindType("SecretHistories.Fucine.FucinePath");
                if (t != null)
                {
                    MethodInfo root = Reflect.Method(t, "Root", 0);
                    if (root != null)
                    {
                        object p = root.Invoke(null, null);
                        if (p != null) return p.ToString();
                    }
                }
            }
            catch { }
            return "~";
        }

        private static Dictionary<string, object> DescribeSphere(object sphere, string path, bool isEmpty)
        {
            var d = new Dictionary<string, object>();
            d["path"] = path;
            d["type"] = sphere == null ? null : sphere.GetType().Name;
            d["id"] = Reflect.GetString(sphere, "Id");
            d["owner"] = Reflect.GetString(sphere, "OwnerSphereIdentifier");

            object spec = Reflect.Get(sphere, "GoverningSphereSpec");
            if (spec != null)
            {
                d["specId"] = Reflect.GetString(spec, "Id");
                d["label"] = Reflect.GetString(spec, "Label");
                d["description"] = Reflect.GetString(spec, "Description");
                d["greedy"] = Reflect.GetBool(spec, "Greedy");
                d["consumes"] = Reflect.GetBool(spec, "Consumes");
                d["required"] = AspectsToDict(Reflect.Get(spec, "Required"));
                d["forbidden"] = AspectsToDict(Reflect.Get(spec, "Forbidden"));
            }

            d["shrouded"] = Reflect.GetBool(sphere, "Shrouded");
            d["defunct"] = Reflect.GetBool(sphere, "Defunct");
            d["category"] = SafeString(() => Reflect.Get(sphere, "SphereCategory")?.ToString());
            d["allowDrag"] = Reflect.GetBool(sphere, "AllowDrag");
            d["contentsHidden"] = Reflect.GetBool(sphere, "ContentsHidden");
            d["tokenCount"] = Reflect.ToList(Reflect.Get(sphere, "Tokens")).Count;
            d["isEmpty"] = isEmpty;
            d["plumbing"] = path != null && PlumbedSpheres.Contains(path);

            var tokensHere = new List<string>();
            foreach (object t in Reflect.ToList(Reflect.Get(sphere, "Tokens")))
            {
                string id = Reflect.GetString(t, "PayloadId");
                if (!string.IsNullOrEmpty(id)) tokensHere.Add(id);
            }
            d["tokenIds"] = tokensHere;

            return d;
        }

        private static Dictionary<string, object> DescribeToken(object token, object sphere, string spherePath, bool brief)
        {
            var d = new Dictionary<string, object>();
            if (token == null) return d;

            string id = SafeString(() => Reflect.GetString(token, "PayloadId"));
            d["id"] = id;
            d["spherePath"] = spherePath;

            object payload = Reflect.Get(token, "Payload");
            if (payload != null)
            {
                d["payloadType"] = payload.GetType().Name;
                d["entityId"] = Reflect.GetString(payload, "EntityId");
                d["quantity"] = Reflect.GetInt(payload, "Quantity", 1);
                d["label"] = SafeString(() => Reflect.GetString(payload, "Label"));
                if (!brief) d["description"] = SafeString(() => Reflect.GetString(payload, "Description"));
                d["unique"] = Reflect.GetBool(payload, "Unique");
                d["uniquenessGroup"] = Reflect.GetString(payload, "UniquenessGroup");
                d["defunct"] = Reflect.GetBool(payload, "Defunct");
                d["lifetimeRemaining"] = Json.Round(Reflect.GetFloat(payload, "LifetimeRemaining"));

                // Situations (the "verb" cards) carry the timer and the active recipe.
                string payloadType = payload.GetType().Name;
                if (payloadType == "Situation")
                {
                    d["verbId"] = SafeString(() => Reflect.GetString(payload, "VerbId"));
                    // StateIdentifier is a clean enum name ("Unstarted"); State is the state
                    // object itself, whose ToString() is the full type name.
                    string identifier = SafeString(() => Reflect.Get(payload, "StateIdentifier")?.ToString());
                    d["situationState"] = identifier ?? SafeString(() => Reflect.Get(payload, "State")?.ToString());
                    d["currentRecipeId"] = SafeString(() => Reflect.GetString(payload, "CurrentRecipeId"));
                    d["recipeId"] = SafeString(() => Reflect.GetString(payload, "RecipeId"));
                    d["timeRemaining"] = Json.Round(Reflect.GetFloat(payload, "TimeRemaining"));
                    d["warmup"] = Json.Round(Reflect.GetFloat(payload, "Warmup"));
                    d["isOpen"] = Reflect.GetBool(payload, "IsOpen");
                    // Whether the window CAN be closed right now. Situation.Conclude() returns
                    // immediately unless the state is concludable (only once the recipe has
                    // finished), so a caller needs this to know when to try.
                    d["concludable"] = SafeBool(() =>
                    {
                        object state = Reflect.Get(payload, "State");
                        if (state == null) return false;
                        MethodInfo can = Reflect.Method(state.GetType(), "IsConcludable", 0);
                        if (can == null) return false;
                        object r = can.Invoke(state, null);
                        return r is bool && (bool)r;
                    });
                    d["slots"] = SlotView(payload);
                }

                // Element stacks are the actual "cards".
                if (payloadType == "ElementStack" || payloadType == "NullElementStack")
                {
                    d["decays"] = Reflect.GetBool(payload, "Decays");
                    object element = Reflect.Get(payload, "Element");
                    if (element != null) d["elementId"] = Reflect.GetString(element, "Id");
                }
            }

            d["aspects"] = SafeAspects(payload, token);
            d["mutations"] = DictToJson(Reflect.Get(payload, "Mutations"));

            // Illuminations are usually empty. Emitting "illuminations": null for every token
            // cost 6.3 KB of a 188.7 KB response for no information at all, so only add the key
            // when there is something in it.
            object illuminations = IlluminateToJson(Reflect.Get(payload, "Illuminations"));
            if (!IsEmptyValue(illuminations)) d["illuminations"] = illuminations;

            // The state's ToString() is a full CLR type name, e.g.
            // "SecretHistories.States.TokenStates.DroppedInSphereState" - 55 bytes per token,
            // 6.6 KB across a normal board, of which the namespace is pure noise. Report the
            // class name only; nothing in the API keys off the namespace.
            d["state"] = ShortTypeName(SafeString(() => Reflect.Get(token, "CurrentState")?.ToString()));
            d["defunct"] = Reflect.GetBool(token, "Defunct");

            d["position"] = SafePosition(token);
            // Usually null - it only matters for a token that has been moved off its home spot.
            object homePosition = SafeHomePosition(token);
            if (!IsEmptyValue(homePosition) && !(homePosition is string)) d["homePosition"] = homePosition;
            d["draggable"] = SafeBool(() => Reflect.GetBool(token, "CanBeDragged"));
            d["interactable"] = SafeBool(() => Reflect.GetBool(token, "CanBePulled"));

            return d;
        }

        /// <summary>The child slots of a situation, with what currently occupies them.</summary>
        private static List<object> SlotView(object situation)
        {
            var slots = new List<object>();
            try
            {
                var dominant = Reflect.Get(situation, "Dominions");
                foreach (object dominion in Reflect.ToList(dominant))
                {
                    string identifier = Reflect.GetString(dominion, "Identifier");
                    foreach (object s in Reflect.ToList(Reflect.Get(dominion, "Spheres")))
                    {
                        var entry = new Dictionary<string, object>();
                        entry["dominion"] = identifier;
                        entry["path"] = Reflect.SpherePath(s);
                        entry["specId"] = Reflect.GetString(Reflect.Get(s, "GoverningSphereSpec"), "Id");
                        entry["type"] = s.GetType().Name;

                        var occupants = new List<string>();
                        foreach (object t in Reflect.ToList(Reflect.Get(s, "Tokens")))
                        {
                            string oid = Reflect.GetString(t, "PayloadId");
                            if (!string.IsNullOrEmpty(oid)) occupants.Add(oid);
                        }
                        entry["tokenIds"] = occupants;
                        entry["empty"] = occupants.Count == 0;
                        slots.Add(entry);
                    }
                }
            }
            catch (Exception e)
            {
                slots.Add(new Dictionary<string, object> { { "error", e.Message } });
            }
            return slots;
        }

        private static Dictionary<string, object> TabletopView(List<object> spheres, List<object> tokens)
        {
            var d = new Dictionary<string, object>();
            const string tabletop = "~/tabletop";
            d["path"] = tabletop;

            int tabletopCount = 0;
            foreach (Dictionary<string, object> s in spheres)
            {
                object p;
                if (s.TryGetValue("path", out p) && Equals(p, tabletop)) tabletopCount++;
            }

            var here = new List<object>();
            foreach (Dictionary<string, object> t in tokens)
            {
                object p;
                if (t.TryGetValue("spherePath", out p) && Equals(p, tabletop)) here.Add(t);
            }

            d["exists"] = tabletopCount > 0;
            d["tokenCount"] = here.Count;
            d["tokens"] = here;
            return d;
        }

        // --- small helpers ---------------------------------------------------------------

        private static Dictionary<string, object> AspectsToDict(object aspects)
        {
            var d = new Dictionary<string, object>();
            var e = Reflect.Enumerable(aspects);
            if (e == null) return d;
            try
            {
                foreach (object kv in e)
                {
                    var key = Reflect.Get(kv, "Key");
                    var value = Reflect.Get(kv, "Value");
                    if (key != null) d[key.ToString()] = value;
                }
            }
            catch { }
            return d;
        }

        private static Dictionary<string, object> DictToJson(object dict)
        {
            return dict == null ? null : AspectsToDict(dict);
        }

        private static Dictionary<string, object> IlluminateToJson(object illuminations)
        {
            var raw = AspectsToDict(illuminations);
            return raw.Count == 0 ? null : raw;
        }

        private static Dictionary<string, object> SafeAspects(object payload, object token)
        {
            try
            {
                if (payload == null) return new Dictionary<string, object>();
                var method = Reflect.Method(payload.GetType(), "GetAspects", 1);
                if (method != null)
                {
                    object aspects = method.Invoke(payload, new object[] { true });
                    return AspectsToDict(aspects);
                }
            }
            catch { }
            return new Dictionary<string, object>();
        }

        private static object SafePosition(object token)
        {
            try
            {
                object location = Reflect.Get(token, "Location");
                object local = Reflect.Get(location, "LocalPosition");
                if (local is Vector3) return Json.Vec3ToJson((Vector3)local);
            }
            catch { }
            return null;
        }

        private static object SafeHomePosition(object token)
        {
            try
            {
                object location = Reflect.Get(token, "HomeLocation");
                if (location == null) return null;
                object local = Reflect.Get(location, "LocalPosition");
                if (local is Vector3) return Json.Vec3ToJson((Vector3)local);
            }
            catch { }
            return null;
        }

        private static string SafeString(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }
}
