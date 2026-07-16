#nullable enable

// Editor-side runner for ops/ui-preview.py. This file is copied into the
// throwaway preview project's Assets/Editor/ folder; it never ships with the
// game. It renders a UXML asset through a runtime panel into a RenderTexture
// and writes a PNG, then exits the editor process.
//
// Contract (environment variables, set by ops/ui-preview.py):
//   ARENA_UIPREVIEW_UXML     project-relative UXML path (required)
//   ARENA_UIPREVIEW_OUT      absolute output PNG path (required)
//   ARENA_UIPREVIEW_THEME    project-relative .tss path (optional)
//   ARENA_UIPREVIEW_CLASSES  comma list of Name:class to add before capture
//   ARENA_UIPREVIEW_WIDTH / ARENA_UIPREVIEW_HEIGHT   panel size (default 1920x1080)

using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class UiPreviewRunner
{
    public static void Run()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_UXML")))
        {
            Debug.LogError("UiPreviewRunner: ARENA_UIPREVIEW_UXML not set");
            EditorApplication.Exit(2);
            return;
        }

        EditorApplication.EnterPlaymode();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void OnEnterPlayMode()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_UXML")))
            return;

        GameObject host = new("UiPreviewHost");
        host.AddComponent<UiPreviewBehaviour>();
    }
}

public sealed class UiPreviewBehaviour : MonoBehaviour
{
    private IEnumerator Start()
    {
        string? outPath = Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_OUT");
        try
        {
            string uxmlPath = Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_UXML")!;
            string themePath = Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_THEME")
                               ?? "Assets/Arena/Resources/UI/Toolkit/ArenaTheme.tss";
            int width = ParseEnv("ARENA_UIPREVIEW_WIDTH", 1920);
            int height = ParseEnv("ARENA_UIPREVIEW_HEIGHT", 1080);

            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (tree == null)
                throw new InvalidOperationException($"UXML not found or failed import: {uxmlPath}");

            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.match = 0.5f;

            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            panel.targetTexture = target;

            var document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = tree;

            ApplyClasses(document.rootVisualElement);

            // Let layout, fonts, and open-transitions settle.
            for (int i = 0; i < 30; i++)
                yield return null;

            RenderTexture.active = target;
            var capture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
            capture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            capture.Apply();
            RenderTexture.active = null;

            File.WriteAllBytes(outPath!, capture.EncodeToPNG());
            Debug.Log($"UiPreviewRunner: wrote {outPath}");
        }
        finally
        {
            EditorApplication.Exit(File.Exists(outPath ?? string.Empty) ? 0 : 1);
        }
    }

    private static void ApplyClasses(VisualElement root)
    {
        string? specs = Environment.GetEnvironmentVariable("ARENA_UIPREVIEW_CLASSES");
        if (string.IsNullOrEmpty(specs))
            return;

        foreach (string spec in specs.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = spec.Split(':');
            if (parts.Length != 2)
                continue;

            VisualElement? element = root.Q<VisualElement>(parts[0].Trim());
            if (element == null)
                Debug.LogWarning($"UiPreviewRunner: element '{parts[0]}' not found for class toggle");
            else
                element.AddToClassList(parts[1].Trim());
        }
    }

    private static int ParseEnv(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
}
