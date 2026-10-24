using System;
using System.Collections.Generic;
using System.Reflection;

namespace CsGameApi
{
    /// <summary>
    /// Pause and game-speed control - the bits the interface buttons drive.
    ///
    /// This matters more than it looks. The clock only advances while the game is not paused,
    /// and pausing is completely invisible through /state, so an external driver could sit
    /// waiting on a game that had stopped, with no way to tell. A session driven purely through
    /// the API hit exactly that: every verb timer froze and nothing reported why.
    ///
    /// The game models speed as a priority stack (GameSpeedState), where an effective speed of
    /// zero means paused. The three toolbar buttons are:
    ///     SpeedControlUI.PauseButton_OnClick / NormalSpeedButton_OnClick / FastSpeedButtonOnClick
    /// and they end up raising LocalNexus.SpeedControlEvent with a SpeedControlEventArgs. Rather
    /// than reimplementing the stack, we raise the same event the buttons raise.
    /// </summary>
    internal static class Playback
    {
        /// <summary>Heart, the component that owns the speed stack.</summary>
        private static object Heart()
        {
            Type t = Reflect.FindType("Heart");
            if (t == null) t = Reflect.FindType("SecretHistories.Infrastructure.Heart");
            if (t == null) throw new ApiException(500, "Heart type not found in this game build");

            object heart = Reflect.FindComponentInScene(t);
            if (heart == null) throw new ApiException(503, "no Heart component; the game is probably not on the tabletop");
            return heart;
        }

        /// <summary>
        /// Leave the tabletop and go back to the main menu, so a new game can be started.
        ///
        /// Without this an API-driven session could only ever be entered, never left: /action/newgame
        /// needs the menu scene, and the menu scene only exists once you are back in it. The
        /// game reaches the menu through GameGateway.LeaveGame() (its own "leave game") and
        /// StageHand.MenuScreen().
        /// </summary>
        public static Dictionary<string, object> GoToMenu()
        {
            var result = new Dictionary<string, object> { { "success", false }, { "action", "menu" } };
            var attempts = new List<string>();
            var errors = new List<string>();

            // Release a pause first: several of these paths are gated behind player input.
            try
            {
                object heart = Heart();
                InvokeOrNull(heart, "Unmetapause", 0);
            }
            catch { }

            // Preferred: the gateway's own leave-game flow, which saves and tears down properly.
            Type gatewayType = Reflect.FindType("SecretHistories.Infrastructure.GameGateway");
            object gateway = gatewayType == null ? null : Reflect.FindComponentInScene(gatewayType);
            if (gateway != null)
            {
                MethodInfo leave = Reflect.Method(gateway.GetType(), "LeaveGame", 0);
                if (leave != null)
                {
                    try { leave.Invoke(gateway, null); attempts.Add("GameGateway.LeaveGame"); }
                    catch (Exception e) { errors.Add("LeaveGame: " + Reflect.Unwrap(e).Message); }
                }
            }

            // Direct route: StageHand owns the scene transition.
            if (attempts.Count == 0)
            {
                try
                {
                    object stageHand = Reflect.StageHand();
                    MethodInfo menu = Reflect.Method(stageHand.GetType(), "MenuScreen", 0);
                    if (menu == null) throw new ApiException(500, "StageHand.MenuScreen not found");
                    menu.Invoke(stageHand, null);
                    attempts.Add("StageHand.MenuScreen");
                }
                catch (Exception e) { errors.Add("MenuScreen: " + Reflect.Unwrap(e).Message); }
            }

            result["attempted"] = attempts;
            if (errors.Count > 0) result["errors"] = errors;
            if (attempts.Count == 0)
                throw new ApiException(500, "could not leave the game: " + string.Join("; ", errors.ToArray()));

            result["success"] = true;
            result["note"] = "returning to the menu; poll GET /screen until menuSceneActive or tabletopHasCards flips, then POST /action/newgame";
            return result;
        }

        /// <summary>
        /// Read-only snapshot for /state. Never throws: a state read must not fail because the
        /// speed stack happened to be unavailable on this screen.
        /// </summary>
        public static Dictionary<string, object> Snapshot()
        {
            var d = new Dictionary<string, object>();
            try
            {
                Type heartType = Reflect.FindType("Heart");
                if (heartType == null) heartType = Reflect.FindType("SecretHistories.Infrastructure.Heart");
                object heart = heartType == null ? null : Reflect.FindComponentInScene(heartType);
                if (heart == null) return d;

                object speedState = Reflect.Get(heart, "gameSpeedState");
                object effective = speedState == null ? null : InvokeOrNull(speedState, "GetEffectiveGameSpeed", 0);

                int speedValue = -1;
                if (effective != null)
                {
                    try { speedValue = Convert.ToInt32(effective); } catch { }
                    d["gameSpeed"] = effective.ToString();
                }
                d["gameSpeedValue"] = speedValue;
                d["paused"] = speedValue == 0;
                d["metapaused"] = Reflect.GetBool(heart, "Metapaused");
                d["forcePaused"] = SafeBool(() => Convert.ToBoolean(InvokeOrNull(heart, "IsForcePaused", 0)));

                object nexus = Reflect.MenuControllerOrNull();
                if (nexus == null)
                    nexus = Reflect.FindComponentInScene(Reflect.FindType("SecretHistories.Infrastructure.UIController"));
                if (nexus != null) d["playerInputDisabled"] = Reflect.GetBool(nexus, "playerInputDisabled");

                if (speedValue == 0)
                    d["hint"] = "the game clock is not advancing because the effective speed is 0. "
                              + "POST /action/unpause to resume, or POST /action/speed with gameSpeed.";
            }
            catch { }
            return d;
        }

        /// <summary>Current effective game speed as its enum name, plus the raw numeric value.</summary>
        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>();
            object heart = Heart();

            object speedState = Reflect.Get(heart, "gameSpeedState");
            object effective = speedState == null ? null : InvokeOrNull(speedState, "GetEffectiveGameSpeed", 0);
            object priority = speedState == null ? null : InvokeOrNull(speedState, "GetCurrentPriority", 0);

            int speedValue = -1;
            if (effective != null)
            {
                try { speedValue = Convert.ToInt32(effective); } catch { }
                d["gameSpeed"] = effective.ToString();
            }
            d["gameSpeedValue"] = speedValue;
            // Paused is simply "effective speed zero"; there is no separate flag for it.
            d["paused"] = speedValue == 0;

            if (priority != null) d["speedPriority"] = priority.ToString();
            d["metapaused"] = Reflect.GetBool(heart, "Metapaused");
            d["forcePaused"] = SafeBool(() => Convert.ToBoolean(InvokeOrNull(heart, "IsForcePaused", 0)));
            d["gameSpeedBeforePause"] = SafeString(() => InvokeOrNull(heart, "GetGameSpeedBeforeMostRecentPause", 0));

            // Player input can be locked independently of the clock (a modal, a fade, a cutscene).
            object nexus = Reflect.MenuControllerOrNull();
            if (nexus == null) nexus = Reflect.FindComponentInScene(Reflect.FindType("SecretHistories.Infrastructure.UIController"));
            if (nexus != null) d["playerInputDisabled"] = Reflect.GetBool(nexus, "playerInputDisabled");

            d["success"] = true;
            return d;
        }

        /// <summary>
        /// Push a speed. Names come from the GameSpeed enum and are resolved by name, so this
        /// does not depend on the enum's numeric values: "Normal", "Fast", "Paused", etc.
        /// </summary>
        public static Dictionary<string, object> SetSpeed(string speedName, bool withSfx)
        {
            var result = new Dictionary<string, object> { { "success", false }, { "action", "speed" } };

            Type speedEnum = FindSpeedEnum();
            if (speedEnum == null) throw new ApiException(500, "GameSpeed enum not found in this game build");

            object speed;
            try { speed = Enum.Parse(speedEnum, speedName, true); }
            catch
            {
                throw new ApiException(400, "'" + speedName + "' is not a game speed; valid values are: "
                    + string.Join(", ", Enum.GetNames(speedEnum)));
            }

            RaiseSpeedCommand(speed, withSfx);
            result["requested"] = speed.ToString();
            result["success"] = true;

            // Report what actually took effect, not just what was asked for.
            object heart = Heart();
            object state = Reflect.Get(heart, "gameSpeedState");
            object effective = state == null ? null : InvokeOrNull(state, "GetEffectiveGameSpeed", 0);
            if (effective != null)
            {
                result["gameSpeed"] = effective.ToString();
                int v = -1;
                try { v = Convert.ToInt32(effective); } catch { }
                result["paused"] = v == 0;
            }
            return result;
        }

        /// <summary>Unpause, tolerating whichever pause mechanism is holding the game down.</summary>
        public static Dictionary<string, object> Unpause()
        {
            var result = new Dictionary<string, object> { { "success", false }, { "action", "unpause" } };
            var attempts = new List<string>();

            // 1. Release any forced pause and metapause.
            object heart = Heart();

            // 2. Clear a force pause through the nexus, which is what opened it.
            object nexus = Reflect.MenuControllerOrNull();
            if (nexus == null) nexus = Reflect.FindComponentInScene(Reflect.FindType("SecretHistories.Infrastructure.UIController"));
            if (nexus != null)
            {
                foreach (string m in new[] { "UnForcePauseGame", "UnPauseGame" })
                {
                    MethodInfo mi = Reflect.Method(nexus.GetType(), m, 1);
                    if (mi == null) continue;
                    try { mi.Invoke(nexus, new object[] { true }); attempts.Add(m); } catch { }
                }
                MethodInfo enable = Reflect.Method(nexus.GetType(), "EnablePlayerInputAndUnmetapause", 0);
                if (enable != null)
                {
                    try { enable.Invoke(nexus, null); attempts.Add("EnablePlayerInputAndUnmetapause"); } catch { }
                }
            }

            // 3. Metapause / unmetapause on Heart.
            InvokeOrNull(heart, "Unmetapause", 0);
            attempts.Add("Heart.Unmetapause");

            // 4. Finally push a normal speed, which is what the "continue" button really does.
            Type speedEnum = FindSpeedEnum();
            if (speedEnum != null)
            {
                try
                {
                    object normal = Enum.Parse(speedEnum, "Normal", true);
                    RaiseSpeedCommand(normal, true);
                    attempts.Add("speed=Normal");
                }
                catch { }
            }

            result["attempted"] = attempts;
            result["success"] = true;

            object state = Reflect.Get(heart, "gameSpeedState");
            object effective = state == null ? null : InvokeOrNull(state, "GetEffectiveGameSpeed", 0);
            if (effective != null)
            {
                result["gameSpeed"] = effective.ToString();
                int v = -1;
                try { v = Convert.ToInt32(effective); } catch { }
                result["paused"] = v == 0;
                if (v == 0)
                    result["warning"] = "the game still reports a paused speed; something may be holding a force-pause";
            }
            return result;
        }

        /// <summary>
        /// Raise LocalNexus.SpeedControlEvent the way the toolbar buttons do. Anything else in
        /// the game that reacts to a speed change therefore reacts here too.
        /// </summary>
        private static void RaiseSpeedCommand(object speed, bool withSfx)
        {
            Type argsType = Reflect.FindType("SecretHistories.Constants.SpeedControlEventArgs");
            if (argsType == null) throw new ApiException(500, "SpeedControlEventArgs not found");

            Type speedEnum = FindSpeedEnum();
            if (speedEnum == null) throw new ApiException(500, "GameSpeed enum not found");

            object args = Activator.CreateInstance(argsType, new[] { speed, (object)withSfx });

            // Prefer the game's own event so every subscriber sees it.
            object nexus = Reflect.MenuControllerOrNull();
            if (nexus == null) nexus = Reflect.FindComponentInScene(Reflect.FindType("SecretHistories.Infrastructure.UIController"));
            if (nexus != null)
            {
                object evt = Reflect.Get(nexus, "SpeedControlEvent");
                MethodInfo invoke = evt == null ? null : Reflect.Method(evt.GetType(), "Invoke", 1);
                if (invoke != null)
                {
                    try { invoke.Invoke(evt, new[] { args }); return; }
                    catch { }
                }
            }

            // Fall back to driving Heart directly.
            object heart = Heart();
            MethodInfo respond = Reflect.Method(heart.GetType(), "RespondToSpeedControlCommand", 1);
            if (respond == null) throw new ApiException(500, "no way to command the game speed");
            try { respond.Invoke(heart, new[] { args }); }
            catch (Exception e) { throw new ApiException(500, "speed command threw: " + Reflect.Unwrap(e).Message); }
        }

        private static Type FindSpeedEnum()
        {
            foreach (string n in new[]
            {
                // The real one, confirmed from the assembly listing: it lives in
                // SecretHistories.Enums, not SecretHistories.Constants alongside GameSpeedState.
                "SecretHistories.Enums.GameSpeed",
                "SecretHistories.Constants.GameSpeed",
                "GameSpeed",
                "SecretHistories.Constants.GameSpeedState+GameSpeed"
            })
            {
                Type t = Reflect.FindType(n);
                if (t != null && t.IsEnum) return t;
            }

            // Last resort: scan the game's assemblies for any enum named GameSpeed.
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (name != "SecretHistories.Enums" && name != "SecretHistories.Main"
                    && !name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) continue;
                try
                {
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t.IsEnum && t.Name == "GameSpeed") return t;
                    }
                }
                catch { }
            }
            return null;
        }

        private static object InvokeOrNull(object target, string method, int argCount, params object[] args)
        {
            if (target == null) return null;
            MethodInfo m = Reflect.Method(target.GetType(), method, argCount);
            if (m == null) return null;
            try { return m.Invoke(target, args); } catch { return null; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static string SafeString(Func<object> f)
        {
            try
            {
                object o = f();
                return o == null ? null : o.ToString();
            }
            catch { return null; }
        }
    }
}
