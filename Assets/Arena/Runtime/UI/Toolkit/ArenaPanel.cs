#nullable enable

using UnityEngine;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// Shared bootstrap for runtime UI Toolkit panels: hosts a UIDocument whose
    /// PanelSettings mirror the uGUI overlay canvases (1920x1080 reference,
    /// match 0.5) and loads the Arena runtime theme. One document per screen,
    /// mirroring the one-canvas-per-surface uGUI idiom.
    /// </summary>
    internal static class ArenaPanel
    {
        /// <summary>
        /// Adds a UIDocument to <paramref name="host"/> showing the UXML at
        /// <paramref name="visualTreeResource"/> (a Resources path). The caller
        /// owns the returned document's PanelSettings instance and should
        /// Destroy it with the host.
        /// </summary>
        public static UIDocument CreateDocument(GameObject host, string visualTreeResource, float sortingOrder)
        {
            PanelSettings panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = $"{host.name}PanelSettings";
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/Toolkit/ArenaTheme");
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.match = 0.5f;
            panel.sortingOrder = sortingOrder;

            UIDocument document = host.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(visualTreeResource);
            return document;
        }
    }
}
