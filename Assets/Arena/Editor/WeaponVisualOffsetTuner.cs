#nullable enable

using System;
using System.Collections.Generic;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    public static class WeaponVisualOffsetTuner
    {
        private const string MenuPath = "Arena/Animation Sets/Save Selected Weapon Presentation Offset";
        private const string WindowMenuPath = "Arena/Animation Sets/Open Weapon Presentation Offset Tuner";
        private static readonly string[] PreferredAnimationSetFolders = { "Assets/Arena/Resources/CombatAnimationSets" };

        [MenuItem(WindowMenuPath)]
        public static void OpenWindow()
        {
            WeaponVisualOffsetTunerWindow.ShowWindow();
        }

        [MenuItem(MenuPath)]
        public static void SaveSelectedVisualOffset()
        {
            Transform? selected = Selection.activeTransform;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("No Weapon Visual Selected", "Select a spawned weapon visual root in the Hierarchy first.", "OK");
                return;
            }

            WeaponAttachmentSpawnedVisual? marker = FindSpawnedVisual(selected);
            if (marker == null)
            {
                EditorUtility.DisplayDialog(
                    "Not A Spawned Weapon Visual",
                    "Select the spawned weapon visual root, such as sword, shield, or greatsword.",
                    "OK");
                return;
            }

            Transform visual = marker.transform;
            if (selected != visual)
            {
                EditorUtility.DisplayDialog(
                    "Select The Visual Root",
                    $"Select the spawned visual root named '{visual.name}', then move or rotate that root before saving.",
                    "OK");
                Selection.activeTransform = visual;
                return;
            }

            if (visual.parent == null)
            {
                EditorUtility.DisplayDialog("Missing Mount Parent", "The selected weapon visual is not parented to a weapon mount.", "OK");
                return;
            }

            AvatarWeaponMounts? mounts = visual.GetComponentInParent<AvatarWeaponMounts>();
            if (mounts == null)
            {
                EditorUtility.DisplayDialog("Missing Avatar Mounts", "Could not find AvatarWeaponMounts above the selected visual.", "OK");
                return;
            }

            List<string> currentMountIds = FindMountIdsForParent(mounts, visual.parent);
            if (currentMountIds.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Unknown Mount",
                    $"The selected visual is parented to '{visual.parent.name}', but that Transform is not registered in AvatarWeaponMounts.",
                    "OK");
                return;
            }

            int saved = SaveToMatchingAnimationSets(
                visual.name,
                currentMountIds,
                mounts.GetComponent<WeaponAttachmentController>(),
                visual.localPosition,
                Normalize(visual.localRotation));

            if (saved == 0)
            {
                EditorUtility.DisplayDialog(
                    "No Matching Animation Set",
                    $"No animation set weapon-presentation binding matched item '{visual.name}' on mount '{string.Join(", ", currentMountIds)}'.",
                    "OK");
                return;
            }

            EditorUtility.DisplayDialog(
                "Weapon Presentation Offset Saved",
                $"Saved local offset for '{visual.name}' to {saved} weapon-presentation binding(s).",
                "OK");
        }

        [MenuItem(MenuPath, true)]
        private static bool SaveSelectedVisualOffsetValidate()
        {
            Transform? selected = Selection.activeTransform;
            return selected != null && FindSpawnedVisual(selected) != null;
        }

        private static int SaveToMatchingAnimationSets(
            string itemId,
            IReadOnlyCollection<string> currentMountIds,
            WeaponAttachmentController? controller,
            Vector3 localPosition,
            Quaternion localRotation)
        {
            int saved = 0;
            string[] guids = FindAnimationSetGuids();

            for (int assetIndex = 0; assetIndex < guids.Length; assetIndex++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[assetIndex]);
                CombatAnimationSet set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(assetPath);
                if (set == null)
                    continue;

                set.EnsureWeaponPresentationProfileInitialized();
                WeaponVisualBinding[] visuals = set.VisualBindings;
                if (visuals.Length == 0)
                    continue;

                SerializedObject serializedSet = new(set);
                SerializedProperty? presentationProperty = serializedSet.FindProperty("weaponPresentation");
                SerializedProperty? visualsProperty = presentationProperty?.FindPropertyRelative("visuals");
                if (visualsProperty == null || !visualsProperty.isArray)
                    continue;

                bool setChanged = false;
                for (int visualIndex = 0; visualIndex < visuals.Length; visualIndex++)
                {
                    WeaponVisualBinding binding = visuals[visualIndex];
                    if (!MatchesItem(binding, itemId))
                        continue;

                    bool isCombatMount = ContainsMountId(currentMountIds, binding.drawnMountId);
                    bool isSheathMount = ContainsMountId(currentMountIds, binding.stowedMountId);
                    if (!isCombatMount && !isSheathMount)
                        continue;

                    bool saveCombat = ResolveSaveCombat(isCombatMount, isSheathMount, controller);

                    if (!setChanged)
                    {
                        Undo.RecordObject(set, "Save Weapon Presentation Offset");
                        CombatAnimationSetProtection.MarkTrustedMutation(set, "weapon-presentation-offset-tune");
                        setChanged = true;
                    }

                    SerializedProperty bindingProperty = visualsProperty.GetArrayElementAtIndex(visualIndex);
                    WriteVisualOffset(bindingProperty, saveCombat, localPosition, localRotation);
                    saved++;

                    Debug.Log(
                        $"[{nameof(WeaponVisualOffsetTuner)}] Saved {(saveCombat ? "combat" : "stowed")} offset for " +
                        $"'{itemId}' in {assetPath}: pos={localPosition}, rot={localRotation.eulerAngles}.");
                }

                if (setChanged)
                {
                    serializedSet.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(set);
                    CombatAnimationSetProtection.RecordTrustedState(set, "weapon-presentation-offset-tune");
                    AssetDatabase.SaveAssetIfDirty(set);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }

            AssetDatabase.SaveAssets();
            return saved;
        }

        private static string[] FindAnimationSetGuids()
        {
            string[] guids = AssetDatabase.FindAssets("t:CombatAnimationSet", PreferredAnimationSetFolders);
            return guids.Length > 0
                ? guids
                : AssetDatabase.FindAssets("t:CombatAnimationSet");
        }

        private static bool ResolveSaveCombat(
            bool isCombatMount,
            bool isSheathMount,
            WeaponAttachmentController? controller)
        {
            if (isCombatMount && isSheathMount)
                return controller == null || controller.IsInCombatVisual;

            return isCombatMount;
        }

        private static bool MatchesItem(WeaponVisualBinding binding, string itemId)
        {
            if (string.Equals(binding.itemId, itemId, StringComparison.Ordinal))
                return true;

            return binding.prefab != null &&
                string.Equals(binding.prefab.name, itemId, StringComparison.Ordinal);
        }

        private static bool ContainsMountId(IReadOnlyCollection<string> mountIds, string mountId)
        {
            if (string.IsNullOrWhiteSpace(mountId))
                return false;

            foreach (string candidate in mountIds)
            {
                if (string.Equals(candidate, mountId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void WriteVisualOffset(
            SerializedProperty bindingProperty,
            bool combat,
            Vector3 localPosition,
            Quaternion localRotation)
        {
            SerializedProperty positionProperty = bindingProperty.FindPropertyRelative(
                combat ? "drawnLocalPosition" : "stowedLocalPosition");
            SerializedProperty rotationProperty = bindingProperty.FindPropertyRelative(
                combat ? "drawnLocalRotation" : "stowedLocalRotation");

            if (positionProperty != null)
                positionProperty.vector3Value = localPosition;
            if (rotationProperty != null)
                rotationProperty.quaternionValue = localRotation;
        }

        private static List<string> FindMountIdsForParent(AvatarWeaponMounts mounts, Transform parent)
        {
            List<string> mountIds = new();
            IReadOnlyList<AvatarWeaponMountDefinition> definitions = mounts.MountDefinitions;
            for (int i = 0; i < definitions.Count; i++)
            {
                AvatarWeaponMountDefinition definition = definitions[i];
                if (definition == null || definition.mount == null)
                    continue;

                if (definition.mount == parent)
                    mountIds.Add(definition.mountId);
            }

            return mountIds;
        }

        private static WeaponAttachmentSpawnedVisual? FindSpawnedVisual(Transform start)
        {
            Transform? current = start;
            while (current != null)
            {
                WeaponAttachmentSpawnedVisual marker = current.GetComponent<WeaponAttachmentSpawnedVisual>();
                if (marker != null)
                    return marker;

                current = current.parent;
            }

            return null;
        }

        private static Quaternion Normalize(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w);

            return magnitude > 0.0001f
                ? new Quaternion(rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude)
                : Quaternion.identity;
        }

        private sealed class WeaponVisualOffsetTunerWindow : EditorWindow
        {
            private Vector3 _positionStep = new(0.01f, 0.01f, 0.01f);
            private Vector3 _rotationStep = new(5f, 5f, 5f);

            public static void ShowWindow()
            {
                var window = GetWindow<WeaponVisualOffsetTunerWindow>("Weapon Presentation Offset");
                window.minSize = new Vector2(360f, 320f);
                window.Show();
            }

            private void OnGUI()
            {
                WeaponAttachmentSpawnedVisual? marker = Selection.activeTransform != null
                    ? FindSpawnedVisual(Selection.activeTransform)
                    : null;

                if (marker == null)
                {
                    EditorGUILayout.HelpBox(
                        "Select a spawned runtime weapon visual root in the Hierarchy: sword, shield, or greatsword. " +
                        "This window edits that object live, so you can watch the Game tab.",
                        MessageType.Info);
                    return;
                }

                Transform visual = marker.transform;
                if (Selection.activeTransform != visual)
                {
                    EditorGUILayout.HelpBox(
                        $"Selected child belongs to '{visual.name}'. Use the button below to select the spawned visual root.",
                        MessageType.Warning);
                    if (GUILayout.Button($"Select {visual.name} Root"))
                        Selection.activeTransform = visual;
                    return;
                }

                EditorGUILayout.LabelField("Selected Visual", visual.name, EditorStyles.boldLabel);
                EditorGUILayout.Space(6f);

                EditorGUI.BeginChangeCheck();
                Vector3 localPosition = EditorGUILayout.Vector3Field("Local Position", visual.localPosition);
                Vector3 localEuler = EditorGUILayout.Vector3Field("Local Rotation", visual.localEulerAngles);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(visual, "Tune Weapon Presentation Offset");
                    visual.localPosition = localPosition;
                    visual.localEulerAngles = localEuler;
                    RepaintGameAndSceneViews();
                }

                EditorGUILayout.Space(10f);
                _positionStep = EditorGUILayout.Vector3Field("Position Step", _positionStep);
                DrawNudgeRow("X", () => NudgePosition(visual, new Vector3(-_positionStep.x, 0f, 0f)), () => NudgePosition(visual, new Vector3(_positionStep.x, 0f, 0f)));
                DrawNudgeRow("Y", () => NudgePosition(visual, new Vector3(0f, -_positionStep.y, 0f)), () => NudgePosition(visual, new Vector3(0f, _positionStep.y, 0f)));
                DrawNudgeRow("Z", () => NudgePosition(visual, new Vector3(0f, 0f, -_positionStep.z)), () => NudgePosition(visual, new Vector3(0f, 0f, _positionStep.z)));

                EditorGUILayout.Space(10f);
                _rotationStep = EditorGUILayout.Vector3Field("Rotation Step", _rotationStep);
                DrawNudgeRow("Local X", () => NudgeLocalRotation(visual, Vector3.right, -_rotationStep.x), () => NudgeLocalRotation(visual, Vector3.right, _rotationStep.x));
                DrawNudgeRow("Local Y", () => NudgeLocalRotation(visual, Vector3.up, -_rotationStep.y), () => NudgeLocalRotation(visual, Vector3.up, _rotationStep.y));
                DrawNudgeRow("Local Z", () => NudgeLocalRotation(visual, Vector3.forward, -_rotationStep.z), () => NudgeLocalRotation(visual, Vector3.forward, _rotationStep.z));
                DrawNudgeRow("Mesh Axis", () => RollDetectedMeshAxis(visual, -_rotationStep.z), () => RollDetectedMeshAxis(visual, _rotationStep.z));

                EditorGUILayout.Space(12f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset Local Offset"))
                    {
                        Undo.RecordObject(visual, "Reset Weapon Presentation Offset");
                        visual.localPosition = Vector3.zero;
                        visual.localRotation = Quaternion.identity;
                        RepaintGameAndSceneViews();
                    }

                    if (GUILayout.Button("Save To Animation Set"))
                        SaveSelectedVisualOffset();
                }
            }

            private static void DrawNudgeRow(string label, Action negative, Action positive)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, GUILayout.Width(70f));
                    if (GUILayout.Button("-", GUILayout.Width(70f)))
                        negative();
                    if (GUILayout.Button("+", GUILayout.Width(70f)))
                        positive();
                }
            }

            private static void NudgePosition(Transform visual, Vector3 delta)
            {
                Undo.RecordObject(visual, "Nudge Weapon Presentation Position");
                visual.localPosition += delta;
                RepaintGameAndSceneViews();
            }

            private static void NudgeLocalRotation(Transform visual, Vector3 localAxis, float degrees)
            {
                Undo.RecordObject(visual, "Nudge Weapon Presentation Rotation");
                visual.localRotation = Normalize(visual.localRotation * Quaternion.AngleAxis(degrees, localAxis));
                RepaintGameAndSceneViews();
            }

            private static void RollDetectedMeshAxis(Transform visual, float degrees)
            {
                if (visual.parent == null)
                    return;

                if (!TryDetectPrimaryMeshAxisInParentSpace(visual, out Vector3 center, out Vector3 axis))
                {
                    EditorUtility.DisplayDialog(
                        "No Mesh Axis Found",
                        $"Could not find a readable MeshFilter under '{visual.name}'.",
                        "OK");
                    return;
                }

                Quaternion rotation = Quaternion.AngleAxis(degrees, axis);
                Undo.RecordObject(visual, "Roll Weapon Presentation Around Mesh Axis");
                visual.localPosition = center + rotation * (visual.localPosition - center);
                visual.localRotation = Normalize(rotation * visual.localRotation);
                RepaintGameAndSceneViews();
            }

            private static bool TryDetectPrimaryMeshAxisInParentSpace(
                Transform visual,
                out Vector3 center,
                out Vector3 axis)
            {
                center = Vector3.zero;
                axis = Vector3.forward;

                if (visual.parent == null)
                    return false;

                MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>(true);
                int pointCount = 0;
                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter filter = filters[i];
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null)
                        continue;

                    Vector3[] vertices;
                    try
                    {
                        vertices = mesh.vertices;
                    }
                    catch (UnityException)
                    {
                        continue;
                    }

                    if (vertices == null || vertices.Length == 0)
                        continue;

                    Matrix4x4 toParent = visual.parent.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    int stride = Mathf.Max(1, vertices.Length / 2000);
                    for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex += stride)
                    {
                        center += toParent.MultiplyPoint3x4(vertices[vertexIndex]);
                        pointCount++;
                    }
                }

                if (pointCount == 0)
                    return TryDetectPrimaryBoundsAxisInParentSpace(visual, out center, out axis);

                center /= pointCount;

                float xx = 0f;
                float xy = 0f;
                float xz = 0f;
                float yy = 0f;
                float yz = 0f;
                float zz = 0f;

                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter filter = filters[i];
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null)
                        continue;

                    Vector3[] vertices;
                    try
                    {
                        vertices = mesh.vertices;
                    }
                    catch (UnityException)
                    {
                        continue;
                    }

                    if (vertices == null || vertices.Length == 0)
                        continue;

                    Matrix4x4 toParent = visual.parent.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    int stride = Mathf.Max(1, vertices.Length / 2000);
                    for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex += stride)
                    {
                        Vector3 delta = toParent.MultiplyPoint3x4(vertices[vertexIndex]) - center;
                        xx += delta.x * delta.x;
                        xy += delta.x * delta.y;
                        xz += delta.x * delta.z;
                        yy += delta.y * delta.y;
                        yz += delta.y * delta.z;
                        zz += delta.z * delta.z;
                    }
                }

                axis = new Vector3(1f, 0.37f, 0.19f).normalized;
                for (int i = 0; i < 16; i++)
                {
                    Vector3 next = new(
                        xx * axis.x + xy * axis.y + xz * axis.z,
                        xy * axis.x + yy * axis.y + yz * axis.z,
                        xz * axis.x + yz * axis.y + zz * axis.z);

                    if (next.sqrMagnitude <= 0.000001f)
                        return false;

                    axis = next.normalized;
                }

                return axis.sqrMagnitude > 0.9f;
            }

            private static bool TryDetectPrimaryBoundsAxisInParentSpace(
                Transform visual,
                out Vector3 center,
                out Vector3 axis)
            {
                center = Vector3.zero;
                axis = Vector3.forward;

                if (visual.parent == null)
                    return false;

                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                bool initialized = false;
                Bounds parentBounds = default;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    Bounds worldBounds = renderer.bounds;
                    Vector3 parentCenter = visual.parent.InverseTransformPoint(worldBounds.center);
                    Vector3 parentSize = visual.parent.InverseTransformVector(worldBounds.size);
                    Bounds current = new(parentCenter, new Vector3(
                        Mathf.Abs(parentSize.x),
                        Mathf.Abs(parentSize.y),
                        Mathf.Abs(parentSize.z)));

                    if (!initialized)
                    {
                        parentBounds = current;
                        initialized = true;
                    }
                    else
                    {
                        parentBounds.Encapsulate(current);
                    }
                }

                if (!initialized)
                    return false;

                center = parentBounds.center;
                Vector3 size = parentBounds.size;
                if (size.x >= size.y && size.x >= size.z)
                    axis = Vector3.right;
                else if (size.y >= size.z)
                    axis = Vector3.up;
                else
                    axis = Vector3.forward;

                return true;
            }

            private static void RepaintGameAndSceneViews()
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }
    }
}
