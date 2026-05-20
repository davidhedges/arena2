#nullable enable

using UnityEngine;
using System.Reflection;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Arena.Input
{
    /// <summary>
    /// Runtime Arena player/showcase objects may reuse locally imported Starter Assets
    /// prefabs for meshes, rigging, camera defaults, and mount points. Input and
    /// movement are owned by Arena components, so Starter Assets runtime behaviours
    /// must be stripped from those instantiated objects when the package is present.
    /// </summary>
    public static class StarterAssetsRuntimeStripper
    {
        private const string ThirdPersonControllerTypeName = "StarterAssets.ThirdPersonController";
        private const string StarterAssetsInputsTypeName = "StarterAssets.StarterAssetsInputs";

        public readonly struct ThirdPersonCameraConfig
        {
            public ThirdPersonCameraConfig(
                Transform? cameraTarget,
                float topClamp,
                float bottomClamp,
                float cameraAngleOverride,
                float cameraSensitivity)
            {
                CameraTarget = cameraTarget;
                TopClamp = topClamp;
                BottomClamp = bottomClamp;
                CameraAngleOverride = cameraAngleOverride;
                CameraSensitivity = cameraSensitivity;
            }

            public Transform? CameraTarget { get; }
            public float TopClamp { get; }
            public float BottomClamp { get; }
            public float CameraAngleOverride { get; }
            public float CameraSensitivity { get; }
        }

        public static bool TryReadThirdPersonCameraConfig(GameObject? root, out ThirdPersonCameraConfig config)
        {
            config = default;
            var controller = FindComponentByTypeName(root, ThirdPersonControllerTypeName);
            if (controller == null)
                return false;

            config = new ThirdPersonCameraConfig(
                ReadGameObjectField(controller, "CinemachineCameraTarget")?.transform,
                ReadFloatField(controller, "TopClamp", 70f),
                ReadFloatField(controller, "BottomClamp", -30f),
                ReadFloatField(controller, "CameraAngleOverride", 0f),
                ReadFloatField(controller, "CameraSensitivity", 3f));
            return true;
        }

        public static void StripFrom(GameObject? root)
        {
            if (root == null)
                return;

            // ThirdPersonController requires PlayerInput, and CharacterController must
            // survive until any caller has copied camera/controller authoring data.
            DestroyComponentsByTypeName(root, ThirdPersonControllerTypeName);

            DestroyComponentsByTypeName(root, StarterAssetsInputsTypeName);

#if ENABLE_INPUT_SYSTEM
            foreach (PlayerInput playerInput in root.GetComponentsInChildren<PlayerInput>(true))
                DestroyNow(playerInput);
#endif
        }

        public static bool IsThirdPersonController(Component? component)
            => component != null && component.GetType().FullName == ThirdPersonControllerTypeName;

        private static void DestroyComponentsByTypeName(GameObject root, string typeName)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null && component.GetType().FullName == typeName)
                    DestroyNow(component);
            }
        }

        private static Component? FindComponentByTypeName(GameObject? root, string typeName)
        {
            if (root == null)
                return null;

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null && component.GetType().FullName == typeName)
                    return component;
            }

            return null;
        }

        private static GameObject? ReadGameObjectField(Component component, string fieldName)
        {
            var field = GetField(component, fieldName);
            return field?.GetValue(component) as GameObject;
        }

        private static float ReadFloatField(Component component, string fieldName, float fallback)
        {
            var field = GetField(component, fieldName);
            if (field?.GetValue(component) is float value)
                return value;

            return fallback;
        }

        private static FieldInfo? GetField(Component component, string fieldName)
            => component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);

        private static void DestroyNow(Object obj)
        {
            if (obj == null)
                return;

            // This is a dependency-order cleanup of prefab-authoring components.
            // Delayed Destroy can leave [RequireComponent] dependencies visible until
            // end-of-frame, which is exactly what blocks PlayerInput removal.
            Object.DestroyImmediate(obj);
        }
    }
}
