using System;
using HarmonyLib;

namespace HowToSteve.Patches
{
    /// <summary>
    /// Postfix on the private <c>Player.TickUpdate()</c> (game-map §2, hook a): it runs once per FishNet tick,
    /// on the main thread, right after the game sends its own pose. We only read state here.
    /// A Harmony "Postfix" is code Harmony splices in to run after the original method returns, like
    /// "after" middleware around a function we don't own; <c>__instance</c> is the object it ran on.
    /// </summary>
    internal static class PlayerTickPatch
    {
        public static Action<Player> OnLocalTick;
        public static bool Installed { get; private set; }
        private static bool _failedOnce;

        public static bool TryInstall(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var target = AccessTools.Method(typeof(Player), "TickUpdate");
            if (target == null)
            {
                log.LogError("Player.TickUpdate not found (game update?); falling back to per-frame polling");
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PlayerTickPatch), nameof(Postfix)));
            Installed = true;
            log.LogInfo("patched Player.TickUpdate (postfix)");
            return true;
        }

        private static void Postfix(Player __instance)
        {
            if (__instance == null || __instance != Player.LocalPlayer) return;
            try { OnLocalTick?.Invoke(__instance); }
            catch (Exception e)
            {
                // Never let our code break the game's tick.
                if (!_failedOnce) { _failedOnce = true; Plugin.Logger.LogError($"player tick hook failed: {e}"); }
            }
        }
    }
}
