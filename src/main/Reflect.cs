using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace CsGameApi
{
    /// <summary>
    /// Narrow, defensive reflection layer over the Cultist Simulator assemblies.
    ///
    /// The mod ships as a DLL that the game loads by name, so it cannot take a compile-time
    /// dependency on every game type it touches. Everything the API needs goes through here,
    /// which keeps one place responsible for tolerating a game update that renames something.
    /// </summary>
    internal static class Reflect
    {
        private const string MainAsm = "SecretHistories.Main";

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PropertyInfo> PropCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        public static Type FindType(string fullName)
        {
            lock (TypeCache)
            {
                Type cached;
                if (TypeCache.TryGetValue(fullName, out cached)) return cached;
            }

            Type found = null;

            // UnityEngine and the BCL are fair game for a handful of well-known types (the mod
            // uses UnityEngine.Object to find scene components). Everything else is looked up
            // only in the game's own assemblies, so a same-named type from another mod can never
            // resolve first.
            bool wantsEngineType = fullName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                                || fullName.StartsWith("UnityEngine", StringComparison.Ordinal)
                                || fullName.StartsWith("System.", StringComparison.Ordinal);

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                // The game splits its types across several assemblies of its own - Main, Enums,
                // Constants, Interfaces - and e.g. GameSpeed lives in Enums. Looking only at
                // Main silently misses those. Other mods' assemblies stay excluded, so a
                // same-named type from elsewhere still cannot win.
                bool gameAssembly = name == MainAsm
                                 || name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)
                                 || name.StartsWith("SecretHistories.", StringComparison.Ordinal);
                bool engineAssembly = wantsEngineType && (name.StartsWith("UnityEngine", StringComparison.Ordinal)
                                                        || name == "mscorlib" || name == "System" || name == "System.Core");
                if (!gameAssembly && !engineAssembly) continue;

                try
                {
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t.FullName == fullName || t.Name == fullName) { found = t; break; }
                    }
                }
                catch { }
                if (found != null) break;
            }

            if (found == null)
            {
                // Still nothing: the type may live in the global namespace, so try the
                // assembly-qualified form for the bare name as well.
                string simple = fullName.Substring(fullName.LastIndexOf('.') + 1);
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.GetName().Name;
                    if (name != MainAsm && !name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) continue;
                    try
                    {
                        foreach (Type t in asm.GetTypes())
                        {
                            if (t.Name == simple) { found = t; break; }
                        }
                    }
                    catch { }
                    if (found != null) break;
                }
            }

            lock (TypeCache) { TypeCache[fullName] = found; }
            return found;
        }

        public static PropertyInfo Prop(Type t, string name)
        {
            if (t == null) return null;
            string key = t.FullName + "." + name;
            lock (PropCache)
            {
                PropertyInfo cached;
                if (PropCache.TryGetValue(key, out cached)) return cached;
            }
            PropertyInfo p = null;
            try { p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { }

            // Fall back to a field: the game mixes both, and a reflected read should not care
            // which one a member happens to be.
            if (p == null)
            {
                FieldInfo f = null;
                try { f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { }
                if (f != null)
                {
                    p = FieldBackedProperty.Create(f);
                }
            }

            lock (PropCache) { PropCache[key] = p; }
            return p;
        }

        /// <summary>Presents a field as a read-only PropertyInfo so callers see one shape.</summary>
        private static class FieldBackedProperty
        {
            public static PropertyInfo Create(FieldInfo field)
            {
                return new FieldProxy(field);
            }

            private sealed class FieldProxy : PropertyInfo
            {
                private readonly FieldInfo _field;

                public FieldProxy(FieldInfo field) { _field = field; }

                public override string Name { get { return _field.Name; } }
                public override Type PropertyType { get { return _field.FieldType; } }
                public override Type DeclaringType { get { return _field.DeclaringType; } }
                public override Type ReflectedType { get { return _field.ReflectedType; } }
                public override PropertyAttributes Attributes { get { return PropertyAttributes.None; } }
                public override bool CanRead { get { return true; } }
                public override bool CanWrite { get { return !_field.IsInitOnly; } }
                public override object GetValue(object obj, BindingFlags invokeAttr, Binder binder, object[] index, System.Globalization.CultureInfo culture)
                {
                    return _field.GetValue(obj);
                }
                public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, object[] index, System.Globalization.CultureInfo culture)
                {
                    _field.SetValue(obj, value);
                }
                public override MethodInfo[] GetAccessors(bool nonPublic) { return new MethodInfo[0]; }
                public override MethodInfo GetGetMethod(bool nonPublic) { return null; }
                public override MethodInfo GetSetMethod(bool nonPublic) { return null; }
                public override ParameterInfo[] GetIndexParameters() { return new ParameterInfo[0]; }
                public override object[] GetCustomAttributes(bool inherit) { return new object[0]; }
                public override object[] GetCustomAttributes(Type attributeType, bool inherit) { return new object[0]; }
                public override bool IsDefined(Type attributeType, bool inherit) { return false; }
            }
        }

        public static MethodInfo Method(Type t, string name, int argCount = -1)
        {
            if (t == null) return null;
            string key = t.FullName + "." + name + "/" + argCount;
            lock (MethodCache)
            {
                MethodInfo cached;
                if (MethodCache.TryGetValue(key, out cached)) return cached;
            }
            MethodInfo found = null;
            try
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name != name) continue;
                    if (argCount >= 0 && m.GetParameters().Length != argCount) continue;
                    found = m;
                    break;
                }
            }
            catch { }
            lock (MethodCache) { MethodCache[key] = found; }
            return found;
        }

        /// <summary>Read a property; returns null (never throws) when absent or faulting.</summary>
        public static object Get(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo p = Prop(target.GetType(), name);
                if (p == null || !p.CanRead) return null;
                return p.GetValue(target, null);
            }
            catch { return null; }
        }

        public static string GetString(object target, string name)
        {
            object v = Get(target, name);
            return v == null ? null : v.ToString();
        }

        public static int GetInt(object target, string name, int fallback = 0)
        {
            object v = Get(target, name);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); } catch { return fallback; }
        }

        public static float GetFloat(object target, string name, float fallback = 0f)
        {
            object v = Get(target, name);
            if (v == null) return fallback;
            try { return Convert.ToSingle(v); } catch { return fallback; }
        }

        public static bool GetBool(object target, string name, bool fallback = false)
        {
            object v = Get(target, name);
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v); } catch { return fallback; }
        }

        public static IEnumerable Enumerable(object target)
        {
            return target as IEnumerable;
        }

        public static List<object> ToList(object enumerable)
        {
            var result = new List<object>();
            var e = enumerable as IEnumerable;
            if (e == null) return result;
            foreach (object o in e) result.Add(o);
            return result;
        }

        /// <summary>Invoke a public static method with no arguments.</summary>
        public static object CallStatic(Type t, string method)
        {
            MethodInfo m = Method(t, method, 0);
            if (m == null) throw new ApiException(500, t.Name + "." + method + "() not found in this game build");
            return m.Invoke(null, null);
        }

        /// <summary>Watchman.Get&lt;T&gt;() with the service type supplied at runtime.</summary>
        public static object WatchmanGet(Type serviceType)
        {
            Type watchman = FindType("SecretHistories.UI.Watchman");
            if (watchman == null || serviceType == null) return null;

            foreach (MethodInfo m in watchman.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Get" || !m.IsGenericMethodDefinition || m.GetParameters().Length != 0) continue;
                try { return m.MakeGenericMethod(serviceType).Invoke(null, null); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>The loaded content compendium, which holds legacies and other entities. Lives in the global namespace.</summary>
        public static object Compendium()
        {
            object c = WatchmanGet(FindType("Compendium"));
            if (c != null) return c;
            return WatchmanGet(FindType("SecretHistories.Entities.Compendium"));
        }

        // --- recipes -----------------------------------------------------------------------

        /// <summary>
        /// The Recipe entity with this id, or null. Compendium.GetEntityById&lt;Recipe&gt;(id).
        ///
        /// Needed because a verb's recipe is normally chosen for it, and the choice can land on a
        /// hint-only recipe that cannot be executed - at which point the caller has no way to say
        /// "start THIS one instead".
        /// </summary>
        public static object FindRecipeById(string recipeId)
        {
            if (string.IsNullOrEmpty(recipeId)) return null;
            object compendium = Compendium();
            if (compendium == null) return null;
            Type recipeType = FindType("SecretHistories.Entities.Recipe");
            if (recipeType == null) return null;

            try
            {
                foreach (MethodInfo m in compendium.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetEntityById" || !m.IsGenericMethodDefinition) continue;
                    if (m.GetParameters().Length != 1) continue;
                    MethodInfo bound = m.MakeGenericMethod(recipeType);
                    object r = bound.Invoke(compendium, new object[] { recipeId });
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Every recipe the compendium considers valid for a verb -
        /// Compendium.GetValidRecipesForUnstartedVerb(verbId).
        /// </summary>
        public static List<object> ValidRecipesForVerb(string verbId)
        {
            var empty = new List<object>();
            if (string.IsNullOrEmpty(verbId)) return empty;
            object compendium = Compendium();
            if (compendium == null) return empty;
            try
            {
                MethodInfo m = Method(compendium.GetType(), "GetValidRecipesForUnstartedVerb", 1);
                if (m == null) return empty;
                object list = m.Invoke(compendium, new object[] { verbId });
                return list == null ? empty : ToList(list);
            }
            catch { return empty; }
        }

        /// <summary>
        /// Whether a recipe could actually run right now - Recipe.CanExecuteInContext(aspects, character).
        ///
        /// Note this does NOT consider Recipe.Craftable: the game's own CanExecuteInContext only
        /// tests IsPlatonic, HasExhaustedRecipe and RequirementsSatisfiedBy. That is exactly why a
        /// hint-only recipe can win the "first matching recipe" race and then refuse to start.
        /// </summary>
        public static bool RecipeCanExecute(object recipe, object aspectsInContext, object character)
        {
            if (recipe == null || aspectsInContext == null) return false;
            MethodInfo m = Method(recipe.GetType(), "CanExecuteInContext", 2);
            if (m == null) return false;
            try
            {
                object r = m.Invoke(recipe, new[] { aspectsInContext, character });
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>The aspects the game sees for a situation, used to test recipes against it.</summary>
        public static object AspectsInContextOf(object somethingWithTokens)
        {
            if (somethingWithTokens == null) return null;
            object axe;
            try { axe = HornedAxe(); } catch { return null; }
            if (axe == null) return null;

            foreach (MethodInfo m in axe.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetAspectsInContext" || m.GetParameters().Length != 1) continue;
                try
                {
                    object r = m.Invoke(axe, new[] { somethingWithTokens });
                    if (r != null) return r;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The player character - Stable.Protag().</summary>
        public static object Protag()
        {
            Type stableType = FindType("SecretHistories.Entities.Stable");
            if (stableType == null) return null;
            object stable = WatchmanGet(stableType);
            if (stable == null) return null;
            MethodInfo m = Method(stable.GetType(), "Protag", 0);
            if (m == null) return null;
            try { return m.Invoke(stable, null); } catch { return null; }
        }

        // --- menu / game-start entry points ----------------------------------------------

        /// <summary>
        /// The menu screen controller, whose ContinueGame / BeginNewSave… the buttons call.
        ///
        /// It is NOT registered with the Watchman service locator - it is a MonoBehaviour that
        /// lives in the menu scene - so we fall back to a scene search. That fallback doubles as
        /// a presence test: the component only exists while the menu is loaded.
        /// </summary>
        public static object MenuController()
        {
            object c = MenuControllerOrNull();
            if (c != null) return c;

            throw new ApiException(503,
                "no menu screen controller is loaded. This means the main menu is not on screen "
                + "(GET /screen shows the current state); return to the main menu and retry.");
        }

        /// <summary>As MenuController(), but returns null instead of throwing when absent.</summary>
        public static object MenuControllerOrNull()
        {
            Type t = FindType("SecretHistories.Infrastructure.MenuScreenController");
            object c = WatchmanGet(t);
            if (c != null) return c;
            return FindComponentInScene(t);
        }

        /// <summary>
        /// Locate a MonoBehaviour of the given type in the loaded scenes.
        ///
        /// Calls UnityEngine.Object.FindObjectOfType through reflection so no Unity reference is
        /// needed here; the mod runs inside the game, so the real implementation is present.
        /// </summary>
        public static object FindComponentInScene(Type componentType)
        {
            if (componentType == null) return null;
            try
            {
                Type unityObject = FindType("UnityEngine.Object");
                if (unityObject == null) return null;

                foreach (MethodInfo m in unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectOfType") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    // The non-generic overload takes a System.Type.
                    if (!ps[0].ParameterType.IsAssignableFrom(typeof(Type))) continue;

                    object found = m.Invoke(null, new object[] { componentType });
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        public static object StageHand()
        {
            object s = WatchmanGet(FindType("SecretHistories.Services.StageHand"));
            if (s == null) throw new ApiException(503, "StageHand service is not registered yet");
            return s;
        }

        /// <summary>
        /// The piece of the menu's Continue flow that does not need the menu.
        ///
        /// MenuScreenController.ContinueGame() is just:
        ///     new DefaultGamePersistenceProvider() -> StageHand.LoadGameOnTabletop(...)
        /// plus a check on whether a character already exists. Reproducing it lets resume work
        /// outside the menu scene; <paramref name="withLoadingScreen"/> picks
        /// LoadGameInPlayfieldWithLoadingScreen, which unloads the current scene first, versus
        /// LoadGameOnTabletop, which the menu uses.
        /// </summary>
        public static object NewDefaultPersistenceProvider()
        {
            Type t = FindType("SecretHistories.Infrastructure.Persistence.DefaultGamePersistenceProvider");
            if (t == null) throw new ApiException(500, "DefaultGamePersistenceProvider type not found");
            try { return Activator.CreateInstance(t); }
            catch (Exception e) { throw new ApiException(500, "cannot create a persistence provider: " + Unwrap(e).Message); }
        }

        public static void LoadGameOnTabletop(object provider)
        {
            object stageHand = StageHand();
            MethodInfo m = Method(stageHand.GetType(), "LoadGameOnTabletop", 1);
            if (m == null) throw new ApiException(500, "StageHand.LoadGameOnTabletop not found");
            try { m.Invoke(stageHand, new[] { provider }); }
            catch (Exception e) { throw new ApiException(500, "LoadGameOnTabletop threw: " + Unwrap(e).Message); }
        }

        /// <summary>Every legacy the player can start, as (id, label) pairs.</summary>
        public static List<KeyValuePair<string, string>> Legacies()
        {
            var result = new List<KeyValuePair<string, string>>();
            object compendium = Compendium();
            if (compendium == null) throw new ApiException(503, "the content compendium is not loaded yet");

            foreach (object legacy in ToList(CompendiumEntities(compendium, "SecretHistories.Entities.Legacy")))
            {
                string id = GetString(legacy, "Id");
                if (string.IsNullOrEmpty(id)) continue;
                string label = GetString(legacy, "Label");
                result.Add(new KeyValuePair<string, string>(id, label ?? id));
            }
            return result;
        }

        /// <summary>
        /// Compendium.GetEntitiesAsList&lt;T&gt;() resolved by type name. The compendium exposes
        /// the generic rather than a non-generic overload, so the type argument is looked up the
        /// same reflective way as everything else.
        /// </summary>
        private static IEnumerable CompendiumEntities(object compendium, string typeFullName)
        {
            Type entityType = FindType(typeFullName);
            if (entityType == null) return new List<object>();

            foreach (MethodInfo m in compendium.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetEntitiesAsList" || !m.IsGenericMethodDefinition || m.GetParameters().Length != 0) continue;
                try
                {
                    object result = m.MakeGenericMethod(entityType).Invoke(compendium, null);
                    if (result != null) return (IEnumerable)result;
                }
                catch { }
            }
            return new List<object>();
        }

        /// <summary>
        /// True when a real game is on the table. Delegates to GameStateSerializer, which owns
        /// the "a populated tabletop exists" test - see the note there on why the presence of
        /// FucineRoot is not a usable signal.
        /// </summary>
        public static bool HasActiveGame()
        {
            return GameStateSerializer.HasActiveGame();
        }

        /// <summary>
        /// Every situation the game currently knows about - HornedAxe.GetRegisteredSituations().
        /// This is the same list the interface walks to find open windows.
        /// </summary>
        public static List<object> RegisteredSituations(object hornedAxe)
        {
            var empty = new List<object>();
            if (hornedAxe == null) return empty;
            MethodInfo m = Method(hornedAxe.GetType(), "GetRegisteredSituations", 0);
            if (m == null) return empty;
            try
            {
                object list = m.Invoke(hornedAxe, null);
                return list == null ? empty : ToList(list);
            }
            catch { return empty; }
        }

        // --- token placement ---------------------------------------------------------------
        /// <summary>
        /// Put a token onto a sphere at a free position, reporting which step did the work.
        ///
        /// Two steps are needed, and the order matters:
        ///   1. the sphere adopts the token (TryAcceptToken re-parents it into this sphere)
        ///   2. the choreographer then chooses a free slot
        /// PlaceTokenAtFreeLocalPosition only sets a localPosition - it does not move a token
        /// between spheres, and on its own it left every collected card at the same default
        /// spot. Adopting first also means the free-position search sees the token as already
        /// placed, so it will not pick the spot it is vacating.
        /// </summary>
        public static bool PlaceTokenOnSphere(object sphere, object token, object context, out string how)
        {
            how = null;
            if (sphere == null || token == null) return false;

            bool adopted = false;

            MethodInfo accept = Method(sphere.GetType(), "TryAcceptToken", 2);
            if (accept != null)
            {
                try
                {
                    object r = accept.Invoke(sphere, new[] { token, context });
                    adopted = r is bool && (bool)r;
                    if (adopted) how = "TryAcceptToken";
                }
                catch { }
            }

            if (!adopted)
            {
                // A plain sphere may not implement acceptance; a direct sphere assignment is the
                // blunt equivalent.
                try
                {
                    MethodInfo set = Method(token.GetType(), "SetSphere", 1)
                                  ?? Method(token.GetType(), "set_Sphere", 1);
                    object setTarget = Reflect.Get(token, "Sphere");
                    MethodInfo onSphere = setTarget == null ? null : Method(setTarget.GetType(), "AddToken", 1);
                    if (onSphere != null) { onSphere.Invoke(setTarget, new[] { token }); }
                }
                catch { }
            }

            // Row first, on the game's grid: that is what produces the tidy left-to-right line
            // the game itself makes when you collect finished cards. The spread is only a
            // fallback for spheres that have no usable grid.
            bool placed = PlaceInRowOnGrid(sphere, token, context, out how);
            if (!placed) placed = PlaceSpreadFromCentre(sphere, token, context, out how);
            if (!placed) placed = PlaceAtFreePosition(sphere, token, context);
            if (placed)
            {
                if (how == null) how = "PlaceTokenAtFreeLocalPosition";
                if (adopted) how = "TryAcceptToken+" + how;
                return true;
            }
            return adopted;
        }

        /// <summary>
        /// Lay a token out using the card dropzone as the anchor.
        ///
        /// PlaceTokenAsCloseAsPossibleToSpecifiedPosition already means "as near that spot as
        /// you can, searching for a legal free position". So the correct thing to hand it is the
        /// dropzone's own (x, y) - the small C-shaped frame the player drags - and then let the
        /// choreographer decide where the card actually goes.
        ///
        /// An earlier version computed x itself as "rightmost occupied + one cell". That is an
        /// append-at-the-end rule, not a nearest-free-slot rule: cards started far to the right
        /// of the dropzone, never filled gaps on the left, and marched off the right edge of the
        /// table. Letting the game search fixes all three at once.
        ///
        /// Spheres without a dropzone (e.g. a mansus output frame) fall back to the row scan.
        /// </summary>
        public static bool PlaceInRowOnGrid(object sphere, object token, object context, out string how)
        {
            how = null;
            if (sphere == null || token == null) return false;

            object choreographer = Get(sphere, "Choreographer");
            if (choreographer == null) return false;

            MethodInfo place = Method(choreographer.GetType(), "PlaceTokenAsCloseAsPossibleToSpecifiedPosition", 3);
            if (place == null) return false;

            float step = GridStep();

            float targetX, targetY;
            bool haveDropzone = TryGetDropzonePosition(sphere, out targetX, out targetY);
            if (haveDropzone)
            {
                // Anchor one cell to the RIGHT of the dropzone, not on it. The dropzone is an
                // invisible marker and the choreographer treats its cell as free, so aiming
                // straight at it parks every card on the same pixel - which is the pile this
                // whole exercise is meant to avoid. This matches what the game does visually:
                // collected cards start beside the small C-shaped frame and run rightwards.
                targetX += step;
            }
            else
            {
                targetY = DropzoneRowY(sphere);
                targetX = NextFreeXInRow(sphere, token, targetY, step);
            }

            Type vector2 = FindType("UnityEngine.Vector2");
            if (vector2 == null) return false;
            object intended;
            try { intended = Activator.CreateInstance(vector2, new object[] { targetX, targetY }); }
            catch { return false; }

            try
            {
                // Signature is (Token, Context, Vector2) - verified from the IL, and the return
                // type is Void, so there is nothing to test here.
                place.Invoke(choreographer, new[] { token, context, intended });
                how = haveDropzone
                    ? "dropzone-anchored(" + targetX.ToString("0.#") + "," + targetY.ToString("0.#") + ")"
                    : "grid-row(x=" + targetX.ToString("0.#") + ",y=" + targetY.ToString("0.#")
                      + ",step=" + step.ToString("0.#") + ")";
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The EgressThreshold sphere that leads into this OutputSphere, or null.
        ///
        /// EgressThreshold.EvictToken is:
        ///     if (_evictionDestination != null) _evictionDestination.AcceptToken(token, context);
        ///     else base.EvictToken(token, context);
        /// and _evictionDestination is the OutputSphere. So a card belongs in the EgressThreshold
        /// - the "ticket gate" on the mansus map - not in the OutputSphere, which is where the
        /// gate sends it once the recipe has run.
        ///
        /// Writing straight into the OutputSphere skips AcceptToken, the recipe and EvictToken,
        /// so the Ingress that represents "entering the mansus" never learns it completed, is
        /// never retired, and the map overlay stays on screen forever.
        ///
        /// _evictionDestination is private in this build, read reflectively via Prop's field
        /// fallback. EgressThreshold derives ThresholdSphere in this version - it does not expose
        /// a separate "Threshold" child, so the EgressThreshold itself is the sphere to target.
        /// </summary>
        public static object FindEgressThresholdFor(object outputSphere)
        {
            if (outputSphere == null) return null;
            foreach (object s in AllSpheres())
            {
                if (s == null) continue;
                if (!string.Equals(s.GetType().Name, "EgressThreshold", StringComparison.Ordinal)) continue;
                object destination = Get(s, "_evictionDestination");
                if (destination == null) continue;
                if (ReferenceEquals(destination, outputSphere)) return s;
            }
            return null;
        }

        /// <summary>True for the sphere type that terminates a mansus/earthen route.</summary>
        public static bool IsOutputSphere(object sphere)
        {
            return sphere != null && string.Equals(sphere.GetType().Name, "OutputSphere", StringComparison.Ordinal);
        }

        /// <summary>Every component of this type in the loaded scenes - Object.FindObjectsOfType.</summary>
        public static List<object> AllComponentsInScene(Type type)
        {
            var found = new List<object>();
            if (type == null) return found;

            Type objectType = FindType("UnityEngine.Object");
            if (objectType == null) return found;

            foreach (MethodInfo m in objectType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "FindObjectsOfType" || m.GetParameters().Length != 1) continue;
                try
                {
                    object result = m.Invoke(null, new object[] { type });
                    if (result is Array)
                    {
                        foreach (object o in (Array)result) if (o != null) found.Add(o);
                        if (found.Count > 0) return found;
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>The card dropzone's full local position - the small C-shaped frame the player drags to
        /// choose where collected cards are laid out. False when this sphere has no such thing.
        /// </summary>
        public static bool TryGetDropzonePosition(object sphere, out float x, out float y)
        {
            x = 0f; y = 0f;
            if (sphere == null) return false;
            try
            {
                foreach (object t in ToList(Get(sphere, "Tokens")))
                {
                    if (t == null) continue;
                    string payloadId = GetString(t, "PayloadId");
                    string tokenId = GetString(t, "Id");
                    bool isDropzone =
                        (payloadId != null
                         && payloadId.IndexOf("dropzone", StringComparison.OrdinalIgnoreCase) >= 0
                         && payloadId.IndexOf("elementstack", StringComparison.OrdinalIgnoreCase) >= 0)
                     || (tokenId != null
                         && tokenId.IndexOf("dropzone_elementstack", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isDropzone) continue;
                    if (TryLocalPosition(t, out x, out y)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Grid cell width from Meniscate.GetGridWidth(), falling back to the measured 75.</summary>
        private static float GridStep()
        {
            object meniscate = MeniscateOrNull();
            if (meniscate == null) return 75f;

            MethodInfo m = Method(meniscate.GetType(), "GetGridWidth", 0);
            if (m == null) return 75f;
            try
            {
                object v = m.Invoke(meniscate, null);
                if (v is float && (float)v > 1f) return (float)v;
                if (v is double && (double)v > 1.0) return (float)(double)v;
            }
            catch { }
            return 75f;
        }

        private static object MeniscateOrNull()
        {
            Type t = FindType("SecretHistories.UI.Meniscate");
            return t == null ? null : FindComponentInScene(t);
        }

        /// <summary>
        /// A token's local position, via token.Location.LocalPosition.
        ///
        /// NOT token.Position: that is not the member the game keeps the position in, and reading
        /// it yields null, which silently made every row/column calculation fall back to zero and
        /// pile collected cards on the origin. /state has always used Location.LocalPosition
        /// (see GameStateSerializer.SafePosition); this matches it.
        /// </summary>
        public static bool TryLocalPosition(object token, out float x, out float y)
        {
            x = 0f; y = 0f;
            if (token == null) return false;
            try
            {
                object location = Get(token, "Location");
                if (location == null) return false;
                object local = Get(location, "LocalPosition");
                if (local == null) return false;

                object xv = Get(local, "x");
                object yv = Get(local, "y");
                if (xv == null || yv == null) return false;

                // Convert rather than "is float": these arrive boxed and the exact CLR type is
                // not worth depending on.
                x = Convert.ToSingle(xv);
                y = Convert.ToSingle(yv);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The y that collected cards should line up on: the element dropzone's own y, which the
        /// player drags to choose where collected cards land. Falls back to the tabletop centre.
        /// </summary>
        private static float DropzoneRowY(object sphere)
        {
            try
            {
                List<object> tokens = ToList(Get(sphere, "Tokens"));
                if (tokens.Count == 0) return 0f;

                // Match on the payload's own id first, then on the token id, so this does not
                // depend on which of the two carries the dropzone name.
                foreach (object t in tokens)
                {
                    string payloadId = GetString(t, "PayloadId");
                    string tokenId = GetString(t, "Id");
                    bool isDropzone = (payloadId != null && payloadId.IndexOf("dropzone", StringComparison.OrdinalIgnoreCase) >= 0
                                       && payloadId.IndexOf("elementstack", StringComparison.OrdinalIgnoreCase) >= 0)
                                   || (tokenId != null && tokenId.IndexOf("dropzone_elementstack", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isDropzone) continue;

                    float dx, dy;
                    if (TryLocalPosition(t, out dx, out dy)) return dy;
                }
            }
            catch { }
            return 0f;
        }

        /// <summary>
        /// The next x along the row that nothing already occupies, stepping by the grid cell
        /// width. Starts to the right of the rightmost card already on that row.
        /// </summary>
        private static float NextFreeXInRow(object sphere, object ignore, float rowY, float step)
        {
            float rightmost = float.NegativeInfinity;
            var taken = new List<float>();

            try
            {
                foreach (object t in ToList(Get(sphere, "Tokens")))
                {
                    if (t == null || ReferenceEquals(t, ignore)) continue;

                    float x, y;
                    if (!TryLocalPosition(t, out x, out y)) continue;
                    if (Math.Abs(y - rowY) > step * 0.5f) continue;   // a different row

                    taken.Add(x);
                    if (x > rightmost) rightmost = x;
                }
            }
            catch { }

            if (float.IsNegativeInfinity(rightmost)) return 0f;   // empty row: start at the centre

            float candidate = rightmost + step;
            // Step past anything already sitting there.
            for (int guard = 0; guard < 64; guard++)
            {
                bool clash = false;
                foreach (float x in taken)
                {
                    if (Math.Abs(x - candidate) < step * 0.5f) { clash = true; break; }
                }
                if (!clash) break;
                candidate += step;
            }
            return candidate;
        }

        /// <summary>
        /// Lay a token out at a position spread away from the sphere's centre.
        /// Kept for callers that want a board-filling spread rather than a tidy row.
        /// </summary>
        public static bool PlaceSpreadFromCentre(object sphere, object token, object context, out string how)
        {
            how = null;
            if (sphere == null || token == null) return false;

            object choreographer = Get(sphere, "Choreographer");
            if (choreographer == null) return false;

            MethodInfo place = Method(choreographer.GetType(), "PlaceTokenAsCloseAsPossibleToSpecifiedPosition", 3);
            if (place == null) return false;

            // A stable pseudo-random offset in grid-ish units, spread over roughly +/-600 in x
            // and +/-400 in y, which covers the visible table without reaching the edges.
            int hash = StableHash(GetString(token, "PayloadId") ?? GetString(token, "Id") ?? "token");
            float x = ((hash & 0xFF) / 255f - 0.5f) * 1200f;
            float y = (((hash >> 8) & 0xFF) / 255f - 0.5f) * 800f;

            // Construct the Vector2 by reflection: this file deliberately depends only on the
            // BCL so the offline harness can compile it against stubs.
            Type vector2 = FindType("UnityEngine.Vector2");
            if (vector2 == null) return false;
            object intended;
            try { intended = Activator.CreateInstance(vector2, new object[] { x, y }); }
            catch { return false; }

            try
            {
                place.Invoke(choreographer, new[] { token, context, intended });
                how = "PlaceTokenAsCloseAsPossibleToSpecifiedPosition";
                return true;
            }
            catch { return false; }
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7FFFFFFF;
            }
        }

        /// <summary>
        /// Ask a sphere's choreographer to lay a token out at a free position.
        ///
        /// The tabletop choreographer owns the grid, the "is this spot legal" test and the
        /// free-position search, so the game itself decides where a card goes:
        ///
        ///     TabletopChoreographer.PlaceTokenAtFreeLocalPosition(Token, Context)
        ///
        /// Returns false when the sphere has no choreographer or it cannot take the token.
        /// </summary>
        public static bool PlaceAtFreePosition(object sphere, object token, object context)
        {
            if (sphere == null || token == null) return false;

            object choreographer = Get(sphere, "Choreographer");
            if (choreographer == null) return false;

            MethodInfo m = Method(choreographer.GetType(), "PlaceTokenAtFreeLocalPosition", 2);
            if (m == null) return false;

            try { m.Invoke(choreographer, new[] { token, context }); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Merge every stack of like cards on a sphere - the interface's "group stacks" button.
        /// TabletopChoreographer.GroupAllStacks().
        /// </summary>
        public static bool GroupAllStacks(object sphere)
        {
            if (sphere == null) return false;
            object choreographer = Get(sphere, "Choreographer");
            if (choreographer == null) return false;

            MethodInfo m = Method(choreographer.GetType(), "GroupAllStacks", 0);
            if (m == null) return false;
            try { m.Invoke(choreographer, null); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Hand a token to a sphere, preferring the sphere's own acceptance path and falling back
        /// to laying it out at a free position. TryAcceptToken on a plain sphere is a no-op - it
        /// returns true without actually taking the token - which is why "move a card to the
        /// tabletop" used to report success and change nothing.
        /// </summary>
        public static bool TakeToken(object sphere, object token, object context, out string how)
        {
            how = null;
            if (sphere == null || token == null) return false;

            MethodInfo accept = Method(sphere.GetType(), "TryAcceptToken", 2);
            if (accept != null)
            {
                try
                {
                    object r = accept.Invoke(sphere, new[] { token, context });
                    if (r is bool && (bool)r) { how = "TryAcceptToken"; return true; }
                }
                catch { }
            }

            if (PlaceAtFreePosition(sphere, token, context))
            {
                how = "PlaceTokenAtFreeLocalPosition";
                return true;
            }
            return false;
        }

        // --- well-known game entry points ------------------------------------------------

        /// <summary>The root of the sphere tree: FucineRoot.Get().</summary>
        public static object FucineRoot()
        {
            Type t = FindType("SecretHistories.Entities.FucineRoot");
            if (t == null)
                throw new ApiException(503,
                    "the game's own types are not visible to this mod. If this mod's log is missing "
                    + "or stops at startup, DLL mods are still locked: enable the 'GHIRBI, the Gatekeeper' "
                    + "mod (Steam Workshop 2901287611) and put 'GHIRBI' on the first line of mods.txt.");
            object root = CallStatic(t, "Get");
            if (root == null) throw new ApiException(503, "no game in progress (FucineRoot.Get() returned null)");
            return root;
        }

        /// <summary>The global sphere catalogue: Watchman.Get&lt;HornedAxe&gt;().</summary>
        public static object HornedAxe()
        {
            Type watchman = FindType("SecretHistories.UI.Watchman");
            Type axe = FindType("SecretHistories.Entities.HornedAxe");
            if (watchman == null || axe == null) throw new ApiException(500, "Watchman/HornedAxe types not found");

            MethodInfo genericGet = null;
            foreach (MethodInfo m in watchman.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { genericGet = m; break; }
            }
            if (genericGet == null) throw new ApiException(500, "Watchman.Get<T>() not found");

            object service = genericGet.MakeGenericMethod(axe).Invoke(null, null);
            if (service == null) throw new ApiException(503, "HornedAxe service not registered yet");
            return service;
        }

        public static object SphereByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ApiException(400, "sphere path is required");
            object axe = HornedAxe();
            MethodInfo m = Method(axe.GetType(), "GetSphereByAbsolutePath", 1);
            if (m == null) throw new ApiException(500, "GetSphereByAbsolutePath not found");

            object fucinePath = MakeFucinePath(path);
            object sphere = m.Invoke(axe, new[] { fucinePath });

            // The catalogue falls back to a default sphere instead of returning null for a path
            // it does not know, so an unchecked result turns a typo into a silent write to the
            // wrong sphere. Verify that what came back really sits at the requested path.
            if (sphere != null)
            {
                string actual = SpherePath(sphere);
                if (string.IsNullOrEmpty(actual) || PathsMatch(actual, path)) return sphere;
            }

            // The catalogue does not resolve everything it lists. The mansus spheres
            // (~!mansus/wood0, ~!mansus/mansusthresholdwood, ...) resolve to ~/tabletop through
            // GetSphereByAbsolutePath even though they appear in GetSpheres, which made the map
            // completely undrivable. Fall back to matching the path against the sphere list.
            object scanned = FindSphereByScan(path);
            if (scanned != null) return scanned;

            if (sphere == null) throw new ApiException(404, "no sphere at path '" + path + "'");
            string fallback = SpherePath(sphere);
            throw new ApiException(404, "no sphere at path '" + path + "' (the catalogue resolved it to '" + fallback + "')");
        }

        /// <summary>
        /// Find a sphere by comparing its absolute path to the requested one, walking the whole
        /// sphere list. Used where the catalogue's own lookup is unreliable (the mansus).
        /// </summary>
        private static object FindSphereByScan(string path)
        {
            try
            {
                foreach (object s in AllSpheres())
                {
                    if (s == null) continue;
                    string actual = SpherePath(s);
                    if (actual == null) continue;
                    if (PathsMatch(actual, path)) return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Compares two sphere paths, tolerating the root marker being written as either
        /// "~!mansus/x" or "~!mansus/x" style variants of the same absolute location.
        /// </summary>
        private static bool PathsMatch(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            return string.Equals(NormalisePath(a), NormalisePath(b), StringComparison.Ordinal);
        }

        private static string NormalisePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            // A trailing separator carries no meaning.
            return p.TrimEnd('/');
        }

        public static object MakeFucinePath(string path)
        {
            Type t = FindType("SecretHistories.Fucine.FucinePath");
            if (t == null) throw new ApiException(500, "FucinePath type not found");
            try { return Activator.CreateInstance(t, new object[] { path }); }
            catch (Exception e) { throw new ApiException(500, "cannot build FucinePath('" + path + "'): " + Unwrap(e).Message); }
        }

        /// <summary>All spheres known to the catalogue, i.e. the whole token geography.</summary>
        public static List<object> AllSpheres()
        {
            object axe = HornedAxe();
            MethodInfo m = Method(axe.GetType(), "GetSpheres", 0);
            if (m == null) throw new ApiException(500, "HornedAxe.GetSpheres() not found");
            return ToList(m.Invoke(axe, null));
        }

        public static string SpherePath(object sphere)
        {
            if (sphere == null) return null;
            try
            {
                MethodInfo m = Method(sphere.GetType(), "GetAbsolutePath", 0);
                if (m != null)
                {
                    object p = m.Invoke(sphere, null);
                    if (p != null) return p.ToString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>Build a Context with a given ActionSource, e.g. "Debug" or "UI".</summary>
        public static object MakeContext(string actionSourceName)
        {
            Type ctx = FindType("Context");
            if (ctx == null) return null;
            Type src = ctx.GetNestedType("ActionSource", BindingFlags.Public | BindingFlags.NonPublic);
            if (src == null || !src.IsEnum) return null;
            try
            {
                object source = Enum.Parse(src, actionSourceName, true);
                return Activator.CreateInstance(ctx, new[] { source });
            }
            catch { return null; }
        }

        public static Exception Unwrap(Exception e)
        {
            var tie = e as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : e;
        }

        // --- logging --------------------------------------------------------------------

        private static readonly object LogLock = new object();

        public static string LogPath
        {
            get { return Path.Combine(ApiConfig.Directory, "LLM_API.log"); }
        }

        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(ApiConfig.Directory);
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;
                    File.AppendAllText(LogPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static void LogError(string context, Exception e)
        {
            Log("ERROR " + context + ": " + e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace);
        }
    }
}
