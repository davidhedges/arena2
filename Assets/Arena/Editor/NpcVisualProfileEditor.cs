#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Arena.Entity;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.Editor
{
    [CustomEditor(typeof(NpcVisualProfile))]
    public sealed class NpcVisualProfileEditor : UnityEditor.Editor
    {
        private const string OutputFolder = "Assets/Arena/Content/NPC/VisualProfiles";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Validate Profile"))
                ValidateAndLog((NpcVisualProfile)target);
        }

        [MenuItem("Arena/NPC/Create Visual Profile Draft From Selected Prefab")]
        private static void CreateDraftFromSelectedPrefab()
        {
            GameObject? prefab = Selection.activeObject as GameObject;
            if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                throw new InvalidOperationException("Select a prefab asset before creating an NPC visual profile draft.");

            EnsureFolder(OutputFolder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{prefab.name}_VisualProfile.asset");
            var profile = CreateInstance<NpcVisualProfile>();
            AssetDatabase.CreateAsset(profile, path);

            var serialized = new SerializedObject(profile);
            serialized.FindProperty("prefab").objectReferenceValue = prefab;

            Animator[] animators = prefab.GetComponentsInChildren<Animator>(includeInactive: true);
            if (animators.Length == 1)
            {
                serialized.FindProperty("primaryAnimatorPath").stringValue =
                    AnimationUtility.CalculateTransformPath(animators[0].transform, prefab.transform);
                PopulateDraftRoles(serialized.FindProperty("animations"), animators[0].runtimeAnimatorController);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);

            if (animators.Length != 1)
                Debug.LogWarning($"[NPC Profile] {prefab.name} contains {animators.Length} Animators; author primaryAnimatorPath explicitly.", profile);
            ValidateAndLog(profile);
        }

        internal static IReadOnlyList<string> Validate(NpcVisualProfile profile)
        {
            var errors = new List<string>();
            if (profile.Prefab is not GameObject prefab)
            {
                errors.Add("Prefab is required.");
                return errors;
            }

            Animator[] animators = prefab.GetComponentsInChildren<Animator>(includeInactive: true);
            if (animators.Length == 0)
                errors.Add("Prefab contains no Animator.");
            if (string.IsNullOrEmpty(profile.PrimaryAnimatorPath))
                errors.Add("Primary Animator path must be authored explicitly.");
            if (!profile.TryResolvePrimaryAnimator(prefab, out Animator animator))
                errors.Add($"Primary Animator path '{profile.PrimaryAnimatorPath}' does not resolve.");
            else
            {
                if (animator.runtimeAnimatorController == null)
                    errors.Add("Primary Animator has no controller.");
                if (animator.avatar == null || !animator.avatar.isValid)
                    errors.Add("Primary Animator has no valid avatar.");
            }

            RequireRole(errors, "idle", profile.Animations.idle);
            RequireRole(errors, "walk", profile.Animations.walk);
            RequireRole(errors, "basic attack", profile.Animations.basicAttack);
            RequireRole(errors, "death", profile.Animations.death);
            return errors;
        }

        private static void ValidateAndLog(NpcVisualProfile profile)
        {
            IReadOnlyList<string> errors = Validate(profile);
            if (errors.Count == 0)
                Debug.Log($"[NPC Profile] '{profile.name}' passed baseline validation.", profile);
            else
                Debug.LogError($"[NPC Profile] '{profile.name}' failed:\n- {string.Join("\n- ", errors)}", profile);
        }

        private static void RequireRole(List<string> errors, string label, List<string> states)
        {
            if (states.Count == 0 || states.TrueForAll(string.IsNullOrWhiteSpace))
                errors.Add($"At least one {label} state is required.");
        }

        private static void PopulateDraftRoles(SerializedProperty roles, RuntimeAnimatorController? controller)
        {
            if (controller == null)
                return;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AnimationClip clip in controller.animationClips)
                names.Add(clip.name);

            Populate(roles.FindPropertyRelative("idle"), names, "idle", "Idle01");
            Populate(roles.FindPropertyRelative("ready"), names, "ready");
            Populate(roles.FindPropertyRelative("walk"), names, "walk", "Walk_Forward");
            Populate(roles.FindPropertyRelative("run"), names, "run", "Run_Forward");
            Populate(roles.FindPropertyRelative("basicAttack"), names, "attack", "Attack01");
            Populate(roles.FindPropertyRelative("hit"), names, "hit");
            Populate(roles.FindPropertyRelative("death"), names, "death");
        }

        private static void Populate(SerializedProperty property, HashSet<string> available, params string[] candidates)
        {
            property.ClearArray();
            foreach (string candidate in candidates)
            {
                foreach (string value in available)
                {
                    if (!string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int index = property.arraySize;
                    property.InsertArrayElementAtIndex(index);
                    property.GetArrayElementAtIndex(index).stringValue = value;
                    break;
                }
            }
        }

        private static void EnsureFolder(string path)
        {
            string current = "Assets";
            foreach (string segment in path.Substring("Assets/".Length).Split('/'))
            {
                string next = $"{current}/{segment}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segment);
                current = next;
            }
        }
    }
}
