using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Centralized Harmony patch registration.
    /// Patches are applied MANUALLY (not via attributes) because IL2CPP game types
    /// are not available at assembly load time — typeof(GameClass) in [HarmonyPatch]
    /// attributes would crash. Patches are applied once the game is ready.
    ///
    /// NOTE: ViewController.OnFocusChanged is NOT patched — IL2CPP virtual method
    /// marshalling crashes Harmony. Instead, GameStateTracker polls the current
    /// top ViewController each frame from the update loop.
    /// </summary>
    public static class HarmonyPatches
    {
        private static bool _applied = false;

        /// <summary>
        /// Applies all Harmony patches. Call once when the game is ready.
        /// Safe to call multiple times — only applies once.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;

            try
            {
                PatchViewControllerManagerPush(harmony);
                PatchViewControllerManagerPop(harmony);
                PatchDuelClientRunEffect(harmony);
                PatchUserTutorialDialog(harmony);
                PatchTutorialManagerFetch(harmony);
                PatchTutorialManagerNotificator(harmony);

                _applied = true;
                MelonLogger.Msg("Harmony patches applied successfully");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to apply Harmony patches: {ex.Message}");
                MelonLogger.Error(ex.StackTrace);
            }
        }

        #region Patch Registration

        private static void PatchViewControllerManagerPush(HarmonyLib.Harmony harmony)
        {
            var targetType = typeof(Il2CppYgomSystem.UI.ViewControllerManager);
            var targetMethod = AccessTools.Method(targetType, "PushChildViewController",
                new Type[] { typeof(string) });

            if (targetMethod == null)
            {
                MelonLogger.Warning("Could not find ViewControllerManager.PushChildViewController(string)");
                return;
            }

            var postfix = typeof(HarmonyPatches).GetMethod(
                nameof(PushChildViewController_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
            DebugLogger.Log(LogCategory.State, "Harmony", "Patched ViewControllerManager.PushChildViewController");
        }

        private static void PatchViewControllerManagerPop(HarmonyLib.Harmony harmony)
        {
            var targetType = typeof(Il2CppYgomSystem.UI.ViewControllerManager);
            var targetMethod = AccessTools.Method(targetType, "PopChildViewController",
                new Type[] { });

            if (targetMethod == null)
            {
                MelonLogger.Warning("Could not find ViewControllerManager.PopChildViewController()");
                return;
            }

            var postfix = typeof(HarmonyPatches).GetMethod(
                nameof(PopChildViewController_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
            DebugLogger.Log(LogCategory.State, "Harmony", "Patched ViewControllerManager.PopChildViewController");
        }

        private static void PatchDuelClientRunEffect(HarmonyLib.Harmony harmony)
        {
            var targetType = typeof(Il2CppYgomGame.Duel.DuelClient);
            var targetMethod = AccessTools.Method(targetType, "RunEffect",
                new Type[] { typeof(int), typeof(int), typeof(int), typeof(int) });

            if (targetMethod == null)
            {
                MelonLogger.Warning("Could not find DuelClient.RunEffect(int,int,int,int)");
                return;
            }

            var postfix = typeof(HarmonyPatches).GetMethod(
                nameof(RunEffect_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
            DebugLogger.Log(LogCategory.State, "Harmony", "Patched DuelClient.RunEffect");
        }

        // Diagnostic patches for the tutorial gate. We don't yet know what
        // actually flips boot tutorial inProgress False — these patches let
        // the next test session capture: every API.User_tutorial_dialog call
        // (the explicit server ack), every TutorialManager.fetch (server
        // refresh), and every UrlQueueTutorialAutoNotificator (the server
        // callback). Stack traces show whose code initiated each call so we
        // can compare a real Cardshop click vs our F11 path.

        private static void PatchUserTutorialDialog(HarmonyLib.Harmony harmony)
        {
            try
            {
                var targetType = typeof(Il2CppYgomSystem.Network.API);
                var targetMethod = AccessTools.Method(targetType, "User_tutorial_dialog",
                    new Type[] { typeof(int) });

                if (targetMethod == null)
                {
                    MelonLogger.Warning("Could not find API.User_tutorial_dialog(int)");
                    return;
                }

                var prefix = typeof(HarmonyPatches).GetMethod(
                    nameof(UserTutorialDialog_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                DebugLogger.Log(LogCategory.State, "Harmony", "Patched API.User_tutorial_dialog");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Failed to patch User_tutorial_dialog: {ex.Message}");
            }
        }

        private static void PatchTutorialManagerFetch(HarmonyLib.Harmony harmony)
        {
            try
            {
                var targetType = typeof(Il2CppYgomSystem.Utility.TutorialManager);
                var targetMethod = AccessTools.Method(targetType, "fetch", new Type[] { });

                if (targetMethod == null)
                {
                    MelonLogger.Warning("Could not find TutorialManager.fetch()");
                    return;
                }

                var prefix = typeof(HarmonyPatches).GetMethod(
                    nameof(TutorialManagerFetch_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                DebugLogger.Log(LogCategory.State, "Harmony", "Patched TutorialManager.fetch");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Failed to patch TutorialManager.fetch: {ex.Message}");
            }
        }

        private static void PatchTutorialManagerNotificator(HarmonyLib.Harmony harmony)
        {
            try
            {
                var targetType = typeof(Il2CppYgomSystem.Utility.TutorialManager);
                // UrlQueueTutorialAutoNotificator(Il2CppSystem.Object obj) — server callback
                var targetMethod = AccessTools.Method(targetType,
                    "UrlQueueTutorialAutoNotificator",
                    new Type[] { typeof(Il2CppSystem.Object) });

                if (targetMethod == null)
                {
                    MelonLogger.Warning("Could not find TutorialManager.UrlQueueTutorialAutoNotificator(Object)");
                    return;
                }

                var prefix = typeof(HarmonyPatches).GetMethod(
                    nameof(TutorialManagerNotificator_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                DebugLogger.Log(LogCategory.State, "Harmony",
                    "Patched TutorialManager.UrlQueueTutorialAutoNotificator");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"Failed to patch TutorialManager.UrlQueueTutorialAutoNotificator: {ex.Message}");
            }
        }

        #endregion

        #region Postfix Methods

        private static void PushChildViewController_Postfix(string prefabpath)
        {
            try
            {
                GameStateTracker.OnViewPushed(prefabpath);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.State, "Harmony",
                    $"Push postfix error: {ex.Message}");
            }
        }

        private static void PopChildViewController_Postfix()
        {
            try
            {
                GameStateTracker.OnViewPopped();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.State, "Harmony",
                    $"Pop postfix error: {ex.Message}");
            }
        }

        private static void RunEffect_Postfix(int id, int param1, int param2, int param3)
        {
            try
            {
                DuelEventAnnouncer.OnRunEffect(id, param1, param2, param3);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DuelPatch",
                    $"RunEffect postfix error: {ex.Message}");
            }
        }

        private static void UserTutorialDialog_Prefix(int _id_)
        {
            try
            {
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"API.User_tutorial_dialog({_id_}) called. Pre-call boot " +
                    $"inProgress={GetBootInProgressSafe()}, " +
                    $"waitTarget=\"{GetWaitTargetSafe()}\"");
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"  caller stack: {ShortStackTrace(8)}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"UserTutorialDialog_Prefix error: {ex.Message}");
            }
        }

        private static void TutorialManagerFetch_Prefix()
        {
            try
            {
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"TutorialManager.fetch() called. Pre-fetch boot " +
                    $"inProgress={GetBootInProgressSafe()}, " +
                    $"waitTarget=\"{GetWaitTargetSafe()}\"");
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"  caller stack: {ShortStackTrace(8)}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"TutorialManagerFetch_Prefix error: {ex.Message}");
            }
        }

        private static void TutorialManagerNotificator_Prefix(Il2CppSystem.Object obj)
        {
            try
            {
                string objStr = "(null)";
                try { if (obj != null) objStr = obj.ToString(); }
                catch { objStr = "(ToString threw)"; }

                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"TutorialManager.UrlQueueTutorialAutoNotificator(obj=\"{objStr}\"). " +
                    $"Pre-call boot inProgress={GetBootInProgressSafe()}, " +
                    $"waitTarget=\"{GetWaitTargetSafe()}\"");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Tutorial",
                    $"TutorialManagerNotificator_Prefix error: {ex.Message}");
            }
        }

        // Helpers used by the tutorial-instrumentation patches to log state
        // around each call. Wrapped in try/catch so a query failure (game
        // not ready, IL2CPP marshalling hiccup) never breaks the patch.

        private static string GetBootInProgressSafe()
        {
            try
            {
                bool ip = Il2CppYgomGame.Utility.TutorialUtil.IsTutorialProgress(
                    Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                return ip ? "True" : "False";
            }
            catch { return "?"; }
        }

        private static string GetWaitTargetSafe()
        {
            try { return Il2CppYgomSystem.Utility.TutorialManager.waitTarget ?? ""; }
            catch { return "?"; }
        }

        private static string ShortStackTrace(int maxFrames)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                int frames = System.Math.Min(st.FrameCount, maxFrames);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < frames; i++)
                {
                    var frame = st.GetFrame(i);
                    var method = frame?.GetMethod();
                    if (method == null) continue;
                    if (i > 0) sb.Append(" <- ");
                    sb.Append(method.DeclaringType?.Name ?? "?");
                    sb.Append(".");
                    sb.Append(method.Name);
                }
                return sb.ToString();
            }
            catch (Exception ex) { return $"(stack-trace error: {ex.Message})"; }
        }

        #endregion
    }
}
