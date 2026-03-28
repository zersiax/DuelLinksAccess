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

        #endregion
    }
}
