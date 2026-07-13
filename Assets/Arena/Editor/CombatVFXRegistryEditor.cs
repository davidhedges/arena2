#nullable enable
using System.Collections.Generic;
using Arena.Combat;
using Arena.Presentation.VFX;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    [CustomEditor(typeof(CombatVFXRegistry))]
    public sealed class CombatVFXRegistryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Runtime Diagnostics", EditorStyles.boldLabel);

            var registry = (CombatVFXRegistry)target;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Clear Runtime Cache"))
                {
                    registry.InvalidateIndex();
                    CombatVFXRegistry.ReloadShared();
                    Debug.Log("CombatVFXRegistry runtime cache cleared and shared registry reloaded.", registry);
                }

                if (GUILayout.Button("Validate Registry"))
                    ValidateRegistry(registry);
            }

            if (GUILayout.Button("Log Resolved VFX Bindings"))
                LogResolvedBindings(registry);
        }

        private static void ValidateRegistry(CombatVFXRegistry registry)
        {
            var errors = new List<string>();
            registry.CollectAuthoringErrors(errors);
            if (errors.Count == 0)
            {
                Debug.Log("CombatVFXRegistry validation passed.", registry);
                return;
            }

            foreach (string error in errors)
                Debug.LogError(error, registry);
        }

        private static void LogResolvedBindings(CombatVFXRegistry registry)
        {
            registry.InvalidateIndex();
            foreach (CombatVFXRegistry.Entry entry in registry.Entries)
            {
                string vfxId = WireIdentifier.Normalize(entry.vfxId);
                GameObject? prefab = registry.ResolvePrefab(vfxId);
                string prefabPath = prefab != null ? AssetDatabase.GetAssetPath(prefab) : "<unresolved>";
                string prefabName = prefab != null ? prefab.name : "<null>";
                Debug.Log(
                    $"{vfxId} -> {prefabName} ({prefabPath}) scale={entry.scale:0.###} "
                    + $"localPositionOffset={entry.localPositionOffset}",
                    registry);
            }
        }
    }
}
