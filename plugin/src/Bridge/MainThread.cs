using System;
using System.Collections.Concurrent;
using BepInEx.Logging;
using UnityEngine;

namespace SteveFish.Bridge
{
    /// <summary>
    /// Unity APIs (GameObjects, Transforms, most statics) may only be touched from the main thread.
    /// The WebSocket runs on thread-pool threads, so it posts work here; Update() drains it once per frame.
    /// </summary>
    internal sealed class MainThread : MonoBehaviour
    {
        private const int MaxActionsPerFrame = 256;

        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        internal static ManualLogSource Log;

        public static void Post(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        public static MainThread Create(ManualLogSource log)
        {
            Log = log;
            var go = new GameObject("SteveFish.MainThread");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            return go.AddComponent<MainThread>();
        }

        /// <summary>Raised once per frame on the main thread, after posted actions ran.</summary>
        public static event Action PerFrame;

        private void Update()
        {
            DrainQueue();
            try { PerFrame?.Invoke(); }
            catch (Exception e) { Log?.LogError($"per-frame handler failed: {e}"); }
        }

        private void DrainQueue()
        {
            for (var i = 0; i < MaxActionsPerFrame && Queue.TryDequeue(out var action); i++)
            {
                try { action(); }
                catch (Exception e) { Log?.LogError($"main-thread action failed: {e}"); }
            }
        }
    }
}
