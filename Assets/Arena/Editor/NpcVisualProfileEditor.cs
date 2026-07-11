#nullable enable
using System;
using System.Collections.Generic;
using Arena.Entity;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.Editor
{
    [CustomEditor(typeof(NpcVisualCatalog))]
    public sealed class NpcVisualCatalogEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (!GUILayout.Button("Validate Catalog"))
                return;

            var catalog = (NpcVisualCatalog)target;
            IReadOnlyList<string> errors = catalog.ValidateEntries();
            if (errors.Count == 0)
                Debug.Log($"[NPC Catalog] '{catalog.name}' passed validation.", catalog);
            else
                Debug.LogError($"[NPC Catalog] '{catalog.name}' failed:\n- {string.Join("\n- ", errors)}", catalog);
        }
    }

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
                    animators[0].transform == prefab.transform
                        ? "."
                        : AnimationUtility.CalculateTransformPath(animators[0].transform, prefab.transform);
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

                HashSet<string> states = CollectStateNames(animator.runtimeAnimatorController);
                ValidateRoleStates(errors, "idle", profile.Animations.idle, states);
                ValidateRoleStates(errors, "ready", profile.Animations.ready, states, required: false);
                ValidateRoleStates(errors, "walk", profile.Animations.walk, states);
                ValidateRoleStates(errors, "run", profile.Animations.run, states, required: false);
                ValidateRoleStates(errors, "basic attack", profile.Animations.basicAttack, states);
                ValidateRoleStates(errors, "spell cast start", profile.Animations.spellCastStart, states, required: false);
                ValidateRoleStates(errors, "spell release", profile.Animations.spellRelease, states, required: false);
                ValidateRoleStates(errors, "spell cancel", profile.Animations.spellCancel, states, required: false);
                ValidateRoleStates(errors, "hit", profile.Animations.hit, states, required: false);
                ValidateRoleStates(errors, "death", profile.Animations.death, states);
            }

            ValidateVfxSockets(errors, profile, prefab);
            return errors;
        }

        private static void ValidateVfxSockets(
            List<string> errors,
            NpcVisualProfile profile,
            GameObject prefab)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < profile.VfxSockets.Count; i++)
            {
                NpcVisualSocketEntry? entry = profile.VfxSockets[i];
                if (entry == null)
                {
                    errors.Add($"VFX socket entry {i} is null.");
                    continue;
                }

                string anchor = entry.AnchorOrEmpty;
                if (string.IsNullOrEmpty(anchor))
                {
                    errors.Add($"VFX socket entry {i} has no anchor.");
                    continue;
                }
                if (!seen.Add(anchor))
                    errors.Add($"VFX socket anchor '{anchor}' is duplicated.");
                if (!NpcVisualProfile.SupportsVfxAnchor(anchor))
                    errors.Add($"VFX socket anchor '{anchor}' is not supported by the shared resolver.");

                string path = entry.TransformPathOrEmpty;
                if (string.IsNullOrEmpty(path))
                {
                    errors.Add($"VFX socket anchor '{anchor}' has no transform path.");
                    continue;
                }

                Transform? transform = string.Equals(path, ".", StringComparison.Ordinal)
                    ? prefab.transform
                    : prefab.transform.Find(path);
                if (transform == null)
                    errors.Add($"VFX socket anchor '{anchor}' path '{path}' does not resolve.");
            }
        }

        private static void ValidateAndLog(NpcVisualProfile profile)
        {
            IReadOnlyList<string> errors = Validate(profile);
            if (errors.Count == 0)
                Debug.Log($"[NPC Profile] '{profile.name}' passed baseline validation.", profile);
            else
                Debug.LogError($"[NPC Profile] '{profile.name}' failed:\n- {string.Join("\n- ", errors)}", profile);
        }

        private static void ValidateRoleStates(
            List<string> errors,
            string label,
            List<string> authored,
            HashSet<string> controllerStates,
            bool required = true)
        {
            if (authored.Count == 0 || authored.TrueForAll(string.IsNullOrWhiteSpace))
            {
                if (required)
                    errors.Add($"At least one {label} state is required.");
                return;
            }

            if (authored.TrueForAll(state => !controllerStates.Contains(state.Trim())))
                errors.Add($"No authored {label} state exists in the primary Animator controller.");
        }

        private static HashSet<string> CollectStateNames(RuntimeAnimatorController? controller)
        {
            if (controller is AnimatorOverrideController overrideController)
                controller = overrideController.runtimeAnimatorController;

            var names = new HashSet<string>(StringComparer.Ordinal);
            if (controller is not AnimatorController animatorController)
                return names;

            foreach (AnimatorControllerLayer layer in animatorController.layers)
                CollectStateNames(layer.stateMachine, names);
            return names;
        }

        private static void CollectStateNames(AnimatorStateMachine stateMachine, HashSet<string> names)
        {
            foreach (ChildAnimatorState child in stateMachine.states)
                names.Add(child.state.name);
            foreach (ChildAnimatorStateMachine child in stateMachine.stateMachines)
                CollectStateNames(child.stateMachine, names);
        }

        private static void PopulateDraftRoles(SerializedProperty roles, RuntimeAnimatorController? controller)
        {
            if (controller == null)
                return;

            HashSet<string> names = CollectStateNames(controller);

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
