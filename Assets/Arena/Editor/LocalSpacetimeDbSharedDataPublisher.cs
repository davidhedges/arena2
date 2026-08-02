#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Arena.Editor
{
    /// <summary>
    /// Keeps the local SpacetimeDB module in step with Unity-authored shared
    /// data. Exporters write both client and server copies, then AssetDatabase
    /// imports the client copy; that import is the durable publish trigger.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class LocalSpacetimeDbSharedDataPublisher : AssetPostprocessor
    {
        private const string SharedDataPrefix = "Assets/Arena/Resources/SharedData/";
        private const string DisableEnvironmentVariable = "ARENA_AUTO_PUBLISH_SHARED_DATA";
        private const double DebounceSeconds = 2d;

        private static PublishRun? activeRun;
        private static bool publishRequested;
        private static bool enterPlayWhenReady;
        private static double lastRequestTime;

        static LocalSpacetimeDbSharedDataPublisher()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (Application.isBatchMode || AutoPublishDisabled())
                return;

            if (!ContainsSharedData(importedAssets) &&
                !ContainsSharedData(deletedAssets) &&
                !ContainsSharedData(movedAssets) &&
                !ContainsSharedData(movedFromAssetPaths))
            {
                return;
            }

            publishRequested = true;
            lastRequestTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Debug.Log(
                "[SpacetimeDB Auto Publish] Shared data changed; queued one " +
                "data-preserving local republish.");
        }

        private static bool ContainsSharedData(IEnumerable<string> paths)
        {
            return paths.Any(path =>
                path.StartsWith(SharedDataPrefix, StringComparison.Ordinal) &&
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }

        private static bool AutoPublishDisabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable(DisableEnvironmentVariable),
                "0",
                StringComparison.Ordinal);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode ||
                (activeRun == null && !publishRequested))
            {
                return;
            }

            enterPlayWhenReady = true;
            EditorApplication.isPlaying = false;
            Debug.Log(
                "[SpacetimeDB Auto Publish] Holding Play until the queued shared-data " +
                "publish passes its live contract gate; Play will resume automatically.");
        }

        private static void Tick()
        {
            if (activeRun != null)
            {
                if (!activeRun.Process.HasExited)
                    return;

                bool succeeded = CompleteRun(activeRun);
                activeRun = null;
                if (!succeeded)
                    enterPlayWhenReady = false;
            }

            if (!publishRequested)
            {
                EditorApplication.update -= Tick;
                ResumePlayIfRequested();
                return;
            }

            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.timeSinceStartup - lastRequestTime < DebounceSeconds)
            {
                return;
            }

            publishRequested = false;
            StartPublish();
        }

        private static void StartPublish()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Unable to resolve the Unity project root.");
            string scriptPath = Path.Combine(projectRoot, "ops", "republish-local-clear.sh");
            if (!File.Exists(scriptPath))
            {
                enterPlayWhenReady = false;
                Debug.LogError($"[SpacetimeDB Auto Publish] Missing '{scriptPath}'.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = scriptPath,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["ARENA_SERVER"] = "local";
            startInfo.EnvironmentVariables["ARENA_DELETE_DATA"] = "never";
            startInfo.EnvironmentVariables["ARENA_GENERATE_BINDINGS"] = "0";
            startInfo.EnvironmentVariables["ARENA_VERIFY_DOTNET"] = "0";
            startInfo.EnvironmentVariables["ARENA_AUTO_START"] = "1";
            AddToolDirectoriesToPath(startInfo);

            var process = new Process { StartInfo = startInfo };
            var run = new PublishRun(process);
            process.OutputDataReceived += (_, args) => run.AppendOutput(args.Data);
            process.ErrorDataReceived += (_, args) => run.AppendError(args.Data);
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Process.Start returned false.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                activeRun = run;
                Debug.Log(
                    "[SpacetimeDB Auto Publish] Rebuilding and publishing local 'arena' " +
                    "with data preservation.");
            }
            catch (Exception error)
            {
                process.Dispose();
                enterPlayWhenReady = false;
                Debug.LogError($"[SpacetimeDB Auto Publish] Failed to start: {error.Message}");
            }
        }

        private static void AddToolDirectoriesToPath(ProcessStartInfo startInfo)
        {
            string userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] required =
            {
                Path.Combine(userRoot, ".local", "bin"),
                Path.Combine(userRoot, ".cargo", "bin"),
                "/opt/homebrew/bin",
                "/usr/local/bin",
                "/usr/local/share/dotnet"
            };
            string current = startInfo.EnvironmentVariables["PATH"] ?? string.Empty;
            startInfo.EnvironmentVariables["PATH"] =
                string.Join(Path.PathSeparator.ToString(), required) +
                Path.PathSeparator + current;
        }

        private static bool CompleteRun(PublishRun run)
        {
            run.Process.WaitForExit();
            int exitCode = run.Process.ExitCode;
            string output = Tail(run.Output(), 80);
            string error = Tail(run.Error(), 40);
            run.Process.Dispose();

            string detail = string.Join(
                "\n",
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (exitCode == 0)
            {
                Debug.Log(
                    "[SpacetimeDB Auto Publish] PASS: local 'arena' is live and " +
                    $"shared-data contracts verified.\n{detail}");
                return true;
            }

            Debug.LogError(
                $"[SpacetimeDB Auto Publish] FAILED (exit {exitCode}).\n{detail}");
            return false;
        }

        private static void ResumePlayIfRequested()
        {
            if (!enterPlayWhenReady)
                return;

            enterPlayWhenReady = false;
            EditorApplication.delayCall += () =>
            {
                if (activeRun == null && !publishRequested && !EditorApplication.isPlaying)
                {
                    Debug.Log("[SpacetimeDB Auto Publish] Contracts are live; entering Play.");
                    EditorApplication.isPlaying = true;
                }
            };
        }

        private static string Tail(string text, int lineCount)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - lineCount)));
        }

        private sealed class PublishRun
        {
            private readonly object outputLock = new();
            private readonly StringBuilder output = new();
            private readonly StringBuilder error = new();

            internal PublishRun(Process process)
            {
                Process = process;
            }

            internal Process Process { get; }

            internal void AppendOutput(string? line)
            {
                if (line == null)
                    return;
                lock (outputLock)
                    output.AppendLine(line);
            }

            internal void AppendError(string? line)
            {
                if (line == null)
                    return;
                lock (outputLock)
                    error.AppendLine(line);
            }

            internal string Output()
            {
                lock (outputLock)
                    return output.ToString();
            }

            internal string Error()
            {
                lock (outputLock)
                    return error.ToString();
            }
        }
    }
}
