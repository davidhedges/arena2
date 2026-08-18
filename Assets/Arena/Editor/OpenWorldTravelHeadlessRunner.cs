#nullable enable
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.EditorTools
{
    /// <summary>
    /// Headless client leg for disposable open-world travel: presses the Hub's
    /// real destination button and asserts the destination scene actually
    /// loads, with no human at the keyboard.
    ///
    ///   ARENA_OPENWORLD_TRAVEL_SCENE=Giant_Skeleton Unity -batchmode \
    ///       -projectPath . -quit=false \
    ///       -executeMethod Arena.EditorTools.OpenWorldTravelHeadlessRunner.Run \
    ///       -logFile /tmp/unity-openworld-travel.log
    ///
    /// Exits 0 only when the destination scene is active, so a shell can gate
    /// on it. The provisioner leg (a fresh database appears, and is deleted on
    /// exit) is proven separately by ops/open-world-travel-probe.py.
    /// </summary>
    public static class OpenWorldTravelHeadlessRunner
    {
        private const string HubScenePath = "Assets/Arena/Content/Scenes/Hub.unity";
        private const string DestinationButtonPath =
            "HubCanvas/HomeRoot/TravelMenu/DestinationButtons/Travel_{0}";
        private const double FirstClickDelaySeconds = 5.0;
        // The Hub control-plane connection can still be handshaking when the
        // menu opens; a press is then refused without creating a ticket, and
        // a press while one IS in flight is a no-op, so retrying is safe.
        private const double ClickRetrySeconds = 5.0;

        private static string _scene = "Giant_Skeleton";
        private static double _deadline;
        private static double _nextClickAt;
        private static int _clickAttempts;

        public static void Run()
        {
            string? sceneEnv = System.Environment.GetEnvironmentVariable("ARENA_OPENWORLD_TRAVEL_SCENE");
            if (!string.IsNullOrWhiteSpace(sceneEnv))
                _scene = sceneEnv.Trim();

            float seconds = 300f;
            string? secondsEnv =
                System.Environment.GetEnvironmentVariable("ARENA_OPENWORLD_TRAVEL_SECONDS");
            if (!string.IsNullOrWhiteSpace(secondsEnv)
                && float.TryParse(secondsEnv, out float parsed)
                && parsed > 0f)
            {
                seconds = parsed;
            }

            Debug.Log($"[OpenWorldTravelHeadlessRunner] destination={_scene} timeout={seconds:F0}s");

            // Domain reload would drop this update hook across the play
            // transition, taking the whole run with it.
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.OpenScene(HubScenePath);
            Application.runInBackground = true;

            _deadline = EditorApplication.timeSinceStartup + seconds;
            _nextClickAt = EditorApplication.timeSinceStartup + FirstClickDelaySeconds;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            if (!Application.isPlaying)
            {
                if (EditorApplication.timeSinceStartup >= _deadline)
                    Finish(false, "editor never entered play mode");
                return;
            }

            if (string.Equals(SceneManager.GetActiveScene().name, _scene, System.StringComparison.Ordinal))
            {
                Finish(true, $"{_scene} is the active scene");
                return;
            }

            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Finish(
                    false,
                    $"still in {SceneManager.GetActiveScene().name} after "
                    + $"{_clickAttempts} travel attempt(s)");
                return;
            }

            if (EditorApplication.timeSinceStartup < _nextClickAt)
                return;

            var hub = Object.FindAnyObjectByType<Arena.UI.HubController>();
            if (hub == null)
                return;

            Transform? button = hub.transform.Find(string.Format(DestinationButtonPath, _scene));
            if (button == null)
            {
                Debug.Log("[OpenWorldTravelHeadlessRunner] opening the practice destination menu");
                hub.OpenPracticeMenu();
                return;
            }

            var destination = button.GetComponent<Button>();
            if (destination == null)
                return;

            // The Hub disables its destination buttons while a travel ticket is
            // live, so this is also the "is a request already in flight" check
            // a human gets for free.
            if (!destination.interactable)
                return;

            _clickAttempts++;
            _nextClickAt = EditorApplication.timeSinceStartup + ClickRetrySeconds;
            Debug.Log(
                $"[OpenWorldTravelHeadlessRunner] pressing Travel_{_scene} (attempt {_clickAttempts})");
            destination.onClick.Invoke();
        }

        private static void Finish(bool passed, string detail)
        {
            EditorApplication.update -= Tick;
            Debug.Log($"[OpenWorldTravelHeadlessRunner] {(passed ? "PASS" : "FAIL")}: {detail}");
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }
}
