#nullable enable

using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Entity
{
    /// <summary>
    /// Coalesces authoritative scene requests and starts them after the frame
    /// that delivered the database callbacks has completely finished.
    /// </summary>
    /// <remarks>
    /// Survival death updates PlayerWorld and deletes PlayerPhysics in one
    /// SpacetimeDB FrameTick. Starting a scene preload from the first callback
    /// while the second callback schedules Object.Destroy can deadlock Unity's
    /// preload and persistent-object locks. The one-frame gate guarantees that
    /// delayed destruction is committed before loading the destination.
    /// </remarks>
    [DefaultExecutionOrder(10_000)]
    internal sealed class RuntimeSceneTransitionQueue : MonoBehaviour
    {
        private static RuntimeSceneTransitionQueue? s_instance;
        private static bool s_explicitHubReturnPending;

        private readonly DeferredSceneTransitionState _state = new();
        private AsyncOperation? _loadOperation;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_instance = null;
            s_explicitHubReturnPending = false;
        }

        internal static bool IsExplicitHubReturnPending => s_explicitHubReturnPending;

        internal static void BeginExplicitHubReturn()
            => s_explicitHubReturnPending = true;

        internal static void CancelExplicitHubReturn()
            => s_explicitHubReturnPending = false;

        internal static void RequestExplicitHubReturn()
        {
            s_explicitHubReturnPending = true;
            Request("Hub");
        }

        internal static void Request(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                return;

            EnsureInstance()._state.Request(sceneName, Time.frameCount);
        }

        private static RuntimeSceneTransitionQueue EnsureInstance()
        {
            if (s_instance != null)
                return s_instance;

            s_instance = FindAnyObjectByType<RuntimeSceneTransitionQueue>();
            if (s_instance != null)
                return s_instance;

            var host = new GameObject(nameof(RuntimeSceneTransitionQueue));
            s_instance = host.AddComponent<RuntimeSceneTransitionQueue>();
            return s_instance;
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        private void Update()
        {
            if (!_state.TryDequeue(Time.frameCount, _loadOperation != null, out string sceneName))
                return;

            if (string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal))
                return;

            Debug.Log($"[SceneTransition] Loading {sceneName} asynchronously after deferred destruction.");
            _loadOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (_loadOperation == null)
            {
                Debug.LogError($"[SceneTransition] Unity did not create an async load for '{sceneName}'.");
                if (string.Equals(sceneName, "Hub", StringComparison.Ordinal))
                    s_explicitHubReturnPending = false;
                return;
            }

            _loadOperation.completed += OnLoadCompleted;
        }

        private void OnLoadCompleted(AsyncOperation operation)
        {
            operation.completed -= OnLoadCompleted;
            if (_loadOperation == operation)
                _loadOperation = null;
            if (string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
                s_explicitHubReturnPending = false;
        }
    }

    /// <summary>Unity-independent state machine for the transition queue.</summary>
    internal sealed class DeferredSceneTransitionState
    {
        private string? _pendingSceneName;
        private int _requestedFrame = -1;

        internal void Request(string sceneName, int frame)
        {
            _pendingSceneName = sceneName;
            _requestedFrame = frame;
        }

        internal bool TryDequeue(int currentFrame, bool loadInFlight, out string sceneName)
        {
            if (loadInFlight ||
                _pendingSceneName == null ||
                currentFrame <= _requestedFrame)
            {
                sceneName = string.Empty;
                return false;
            }

            sceneName = _pendingSceneName;
            _pendingSceneName = null;
            _requestedFrame = -1;
            return true;
        }
    }
}
