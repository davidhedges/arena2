#nullable enable
using System;
using System.Collections.Generic;
using Arena.Network;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Debugging
{
    public static class ActionBarTrace
    {
        private const int MaxLines = 16;
        private static readonly Queue<string> Lines = new();
        private static string[] _snapshot = Array.Empty<string>();
        private static DbConnection? _subscribedConnection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("ActionBarTrace");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ActionBarTraceMonitor>();
        }

        public static IReadOnlyList<string> Snapshot() => _snapshot;

        public static void EnsureSubscribed(DbConnection? conn)
        {
            if (conn == null || ReferenceEquals(_subscribedConnection, conn))
                return;

            Unsubscribe();
            _subscribedConnection = conn;
            _subscribedConnection.OnUnhandledReducerError += OnUnhandledReducerError;
            Trace("connected reducer-error trace");
        }

        public static void Trace(string message)
        {
            Record(message);
        }

        /// <summary>
        /// Records an event in the in-game trace and mirrors it to Editor.log.
        /// Use only for input-driven diagnostics and rejection/result boundaries,
        /// never for per-frame state.
        /// </summary>
        public static void Diagnostic(string message)
        {
            string line = Record(message);
            Debug.LogWarning($"[ArenaActionDiagnostic] {line}");
        }

        private static string Record(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            if (Lines.Count >= MaxLines)
                Lines.Dequeue();
            Lines.Enqueue(line);
            _snapshot = Lines.ToArray();
            return line;
        }

        private static void Unsubscribe()
        {
            if (_subscribedConnection == null)
                return;

            _subscribedConnection.OnUnhandledReducerError -= OnUnhandledReducerError;
            _subscribedConnection = null;
        }

        private static void OnUnhandledReducerError(ReducerEventContext ctx, Exception error)
        {
            string reducerName = ctx.Event.Reducer.GetType().Name;
            Diagnostic($"reducer {reducerName} failed: {error.Message}");
        }

        private sealed class ActionBarTraceMonitor : MonoBehaviour
        {
            private void Update()
            {
                if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                    return;

                EnsureSubscribed(NetworkManager.Instance?.Conn);
            }
        }
    }
}
