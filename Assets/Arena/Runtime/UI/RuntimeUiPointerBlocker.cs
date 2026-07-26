#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;

namespace Arena.UI
{
    public static class RuntimeUiPointerBlocker
    {
        private static readonly List<RaycastResult> UiRaycastResults = new(8);

        public static bool IsPointerOverUi(Vector2 screenPosition)
        {
            EventSystem? eventSystem = EventSystem.current;
            if (eventSystem != null)
            {
                UiRaycastResults.Clear();
                var pointer = new PointerEventData(eventSystem) { position = screenPosition };
                eventSystem.RaycastAll(pointer, UiRaycastResults);
                for (int i = 0; i < UiRaycastResults.Count; i++)
                {
                    string moduleType = UiRaycastResults[i].module.GetType().Name;
                    if (moduleType == "GraphicRaycaster" || moduleType == "PanelRaycaster")
                        return true;
                }
            }

            UIDocument[] documents = Object.FindObjectsByType<UIDocument>(
                FindObjectsInactive.Exclude);
            for (int i = 0; i < documents.Length; i++)
            {
                VisualElement? root = documents[i].rootVisualElement;
                if (root?.panel == null)
                    continue;

                Vector2 panelPoint = RuntimePanelUtils.ScreenToPanel(
                    root.panel,
                    new Vector2(screenPosition.x, Screen.height - screenPosition.y));
                VisualElement? picked = root.panel.Pick(panelPoint);
                if (picked != null && picked != root)
                    return true;
            }

            return false;
        }
    }
}
