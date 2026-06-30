#nullable enable

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Arena.Network;
using UnityEngine;

namespace Arena.Debugging
{
    /// <summary>
    /// Development-only network endpoint selector. Toggle with F8.
    /// </summary>
    internal sealed class NetworkEnvironmentOverlay : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.F8;
        private bool _visible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("NetworkEnvironmentOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<NetworkEnvironmentOverlay>();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _visible = false;
                return;
            }

            if (UnityEngine.Input.GetKeyDown(ToggleKey))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            NetworkEnvironmentEndpoint selected = NetworkEnvironmentConfig.CurrentEndpoint;
            NetworkManager? manager = NetworkManager.Instance;
            NetworkEnvironmentEndpoint active = manager?.ActiveEndpoint ?? selected;

            GUILayout.BeginArea(new Rect(10f, 10f, 440f, 210f), "Network Environment (F8)", GUI.skin.window);
            GUILayout.Label($"Selected: {selected.DisplayName}");
            GUILayout.Label($"Selected endpoint: {selected.ServerUri} / {selected.ModuleName}");
            GUILayout.Space(6f);
            GUILayout.Label(manager == null
                ? "NetworkManager: not initialized"
                : $"Active: {active.DisplayName}  Connected: {manager.IsConnected}");

            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            DrawEnvironmentButton("Use Local", NetworkEnvironmentKind.Local);
            DrawEnvironmentButton("Use Remote", NetworkEnvironmentKind.Remote);
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            if (GUILayout.Button("Reconnect"))
                manager?.ReconnectToSelectedEnvironment();

            if (GUILayout.Button("Reset Identity For Selected Environment"))
            {
                NetworkEnvironmentConfig.ClearAuthToken(selected);
                manager?.ReconnectToSelectedEnvironment();
            }

            GUILayout.Space(6f);
            GUILayout.Label("Local:  ws://localhost:3000");
            GUILayout.Label("Remote: wss://arena.meandmyson.org");
            GUILayout.EndArea();
        }

        private static void DrawEnvironmentButton(string label, NetworkEnvironmentKind environment)
        {
            bool selected = NetworkEnvironmentConfig.CurrentEnvironment == environment;
            GUI.enabled = !selected;
            if (GUILayout.Button(selected ? $"{label} (selected)" : label))
            {
                NetworkEnvironmentConfig.SetCurrentEnvironment(environment);
                NetworkManager.Instance?.ReconnectToSelectedEnvironment();
            }
            GUI.enabled = true;
        }
    }
}
#endif
