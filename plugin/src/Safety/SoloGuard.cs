using System;
using FishNet;

namespace HowToSteve.Safety
{
    /// <summary>
    /// The three solo-only conditions from game-map §7 / decisions D6. Phase 2 only logs them;
    /// from Phase 3 nothing is enabled unless <see cref="Snapshot.AllowsActivation"/> is true.
    /// Main thread only (reads Unity/FishNet state).
    /// </summary>
    internal static class SoloGuard
    {
        internal readonly struct Snapshot
        {
            public readonly bool? NotSteam;     // ConnectionManager.IsUsingSteam == false (public static field, verified)
            public readonly bool? IsHost;       // InstanceFinder.IsServerStarted && IsClientStarted
            public readonly int? PlayerCount;   // PlayerManager.Players.Count (static List<Player>)

            public Snapshot(bool? notSteam, bool? isHost, int? playerCount)
            {
                NotSteam = notSteam; IsHost = isHost; PlayerCount = playerCount;
            }

            public bool AllowsActivation => NotSteam == true && IsHost == true && PlayerCount == 1;

            public override string ToString() =>
                $"solo[offline={Fmt(NotSteam)} host={Fmt(IsHost)} players={(PlayerCount?.ToString() ?? "?")}] => {(AllowsActivation ? "OK" : "BLOCKED")}";

            private static string Fmt(bool? b) => b.HasValue ? (b.Value ? "yes" : "no") : "?";
        }

        public static Snapshot Evaluate()
        {
            bool? notSteam = Try(() => !ConnectionManager.IsUsingSteam);
            bool? isHost = Try(() => InstanceFinder.IsServerStarted && InstanceFinder.IsClientStarted);
            int? players = Try(() => PlayerManager.Players == null ? 0 : PlayerManager.Players.Count);
            return new Snapshot(notSteam, isHost, players);
        }

        // A game update could rename/remove members; report "?" instead of crashing.
        private static T? Try<T>(Func<T> read) where T : struct
        {
            try { return read(); }
            catch (Exception) { return null; }
        }
    }
}
