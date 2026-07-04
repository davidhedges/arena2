#nullable enable
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>
    /// S7 headless A/B evidence runner: drives an observing client session
    /// without a human at the keyboard, so the F4 adaptive-delay legs can run
    /// end-to-end from the command line:
    ///
    ///   ARENA_S7_AB_AUTORUN=1 Unity -batchmode -projectPath . \
    ///       -executeMethod Arena.EditorTools.S7HeadlessAbRunner.Run \
    ///       -logFile /tmp/unity-s7.log
    ///
    /// Opens the target open-world scene, enters play mode with domain
    /// reload disabled (keeps this update hook alive across the play
    /// transition), travels the connected player to that scene server-side
    /// (the Hub button's exact call sequence, no UI needed), and exits the
    /// editor after ARENA_S7_HEADLESS_SECONDS (default 560). The scripted
    /// leg driver itself lives in NetcodeDebugOverlay and self-starts via
    /// ARENA_S7_AB_AUTORUN once a precise clock sample and an NPC (the
    /// ops/s7-lap-probe.py kobold) have been present for ~10 s. Evidence
    /// lands in Logs/remote-presentation-ab.csv as usual.
    /// </summary>
    public static class S7HeadlessAbRunner
    {
        private const string ScenePathFormat = "Assets/Arena/Content/Scenes/OpenWorld/{0}.unity";

        private static string _scene = "Desert_Day";
        private static double _deadline;
        private static bool _travelRequested;

        public static void Run()
        {
            string? sceneEnv = System.Environment.GetEnvironmentVariable("ARENA_S7_SCENE");
            if (!string.IsNullOrWhiteSpace(sceneEnv))
                _scene = sceneEnv.Trim();

            float seconds = 560f;
            string? secondsEnv = System.Environment.GetEnvironmentVariable("ARENA_S7_HEADLESS_SECONDS");
            if (!string.IsNullOrWhiteSpace(secondsEnv) && float.TryParse(secondsEnv, out float parsed) && parsed > 0f)
                seconds = parsed;

            Debug.Log($"[S7HeadlessAbRunner] scene={_scene} seconds={seconds:F0}");

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.OpenScene(string.Format(ScenePathFormat, _scene));
            Application.runInBackground = true;

            _deadline = EditorApplication.timeSinceStartup + seconds;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Debug.Log("[S7HeadlessAbRunner] deadline reached — exiting editor");
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
                return;
            }

            if (_travelRequested || !Application.isPlaying)
                return;

            var networkManager = Arena.Network.NetworkManager.Instance;
            if (networkManager == null || !networkManager.IsConnected || networkManager.Conn == null)
                return;

            // Same call sequence as the Hub travel button, minus the UI.
            _travelRequested = true;
            Debug.Log($"[S7HeadlessAbRunner] connected — traveling to {_scene}");
            Arena.World.OpenWorldTravelCatalog.SetCurrentScene(_scene);
            networkManager.Conn.Reducers.SetOpenWorldScene(_scene);
        }
    }
}
