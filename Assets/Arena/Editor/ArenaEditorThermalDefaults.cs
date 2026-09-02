#nullable enable

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Keeps the project's fresh Editor experience comfortable on the current
    /// laptop without reducing the resolution or quality of standalone builds.
    /// </summary>
    [InitializeOnLoad]
    internal static class ArenaEditorThermalDefaults
    {
        internal const bool LowResolutionGameViewByDefault = true;

        private const int MaxGameViewDiscoveryAttempts = 8;
        private const string GameViewTypeName = "UnityEditor.GameView";
        private const string LowResolutionPropertyName = "lowResolutionForAspectRatios";

        private static int s_remainingDiscoveryAttempts = MaxGameViewDiscoveryAttempts;
        private static bool s_warned;

        static ArenaEditorThermalDefaults()
        {
            EditorApplication.delayCall += ApplyLowResolutionGameViewDefault;
        }

        private static void ApplyLowResolutionGameViewDefault()
        {
            Type? gameViewType = ResolveGameViewType();
            PropertyInfo? lowResolutionProperty = gameViewType?.GetProperty(
                LowResolutionPropertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (gameViewType == null ||
                lowResolutionProperty == null ||
                lowResolutionProperty.PropertyType != typeof(bool) ||
                !lowResolutionProperty.CanRead ||
                !lowResolutionProperty.CanWrite)
            {
                WarnOnce("Unity's Game view low-resolution setting could not be found.");
                return;
            }

            UnityEngine.Object[] gameViews = Resources.FindObjectsOfTypeAll(gameViewType);
            if (gameViews.Length == 0)
            {
                s_remainingDiscoveryAttempts--;
                if (s_remainingDiscoveryAttempts > 0)
                    EditorApplication.delayCall += ApplyLowResolutionGameViewDefault;
                return;
            }

            try
            {
                foreach (UnityEngine.Object gameView in gameViews)
                {
                    if (lowResolutionProperty.GetValue(gameView) is true)
                        continue;

                    lowResolutionProperty.SetValue(gameView, LowResolutionGameViewByDefault);
                    if (gameView is EditorWindow window)
                        window.Repaint();
                }
            }
            catch (Exception exception)
            {
                WarnOnce($"Unity's Game view low-resolution setting could not be applied: {exception.Message}");
            }
        }

        private static Type? ResolveGameViewType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type = assembly.GetType(GameViewTypeName, throwOnError: false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static void WarnOnce(string message)
        {
            if (s_warned)
                return;

            s_warned = true;
            Debug.LogWarning($"[ArenaEditorThermalDefaults] {message}");
        }
    }
}
