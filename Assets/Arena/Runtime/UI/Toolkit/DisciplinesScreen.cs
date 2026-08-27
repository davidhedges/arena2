#nullable enable

using System;
using Arena.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.UI
{
    /// <summary>
    /// Compatibility shell for the retired discipline editor. The old screen
    /// encoded an incompatible discipline hierarchy and cannot safely edit the atomic
    /// combat-build contract, so it stays disabled until the replacement UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DisciplinesScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string RuntimeObjectName = "DisciplinesScreenRuntime";

        public event Action? Closed;
        public event Action? EquipmentRequested;

        public int EscapeClosePriority => 115;
        public bool IsEscapeCloseable => false;

        public static DisciplinesScreen Ensure(Transform parent)
        {
            foreach (DisciplinesScreen candidate in
                     FindObjectsByType<DisciplinesScreen>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.gameObject.scene == parent.gameObject.scene)
                    return candidate;
            }

            GameObject host = new(RuntimeObjectName);
            SceneManager.MoveGameObjectToScene(host, parent.gameObject.scene);
            return host.AddComponent<DisciplinesScreen>();
        }

        public void Open()
        {
            Debug.LogWarning(
                "The retired Disciplines screen is disabled. Combat-build editing " +
                "will return with the canonical replacement UI.");
            Closed?.Invoke();
        }

        public void Close()
        {
            Closed?.Invoke();
        }

        public bool TryCloseForEscape() => false;

        internal static Sprite? ResolveDisciplineIcon(string disciplineId)
        {
            return ActionIconResolver.Resolve(
                ActionKinds.CombatDisciplineSwitch,
                WireIdentifier.Normalize(disciplineId));
        }

        internal static Color DisciplineColor(string disciplineId)
        {
            return WireIdentifier.Normalize(disciplineId) switch
            {
                "DAGGERS" => new Color32(159, 120, 194, 255),
                "TWO_HANDED_SWORD" => new Color32(213, 161, 72, 255),
                "SWORD_AND_SHIELD" => new Color32(216, 179, 90, 255),
                "ARCHER_BOW" => new Color32(111, 159, 105, 255),
                "STAFF" => new Color32(111, 131, 196, 255),
                _ => new Color32(217, 181, 106, 255),
            };
        }
    }
}
