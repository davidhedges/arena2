#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// World-space health bar rendered above a player capsule.
    /// Uses quads (background + fill) that billboard toward the camera.
    /// </summary>
    public class WorldHealthBar : MonoBehaviour
    {
        private const float BarWidth = 0.8f;
        private const float BarHeight = 0.08f;
        private const float HeightOffset = 2.1f;
        private const bool WorldHealthBarsVisible = false;

        private Transform _fill = null!;
        private float _fraction = 1f;

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            transform.LookAt(cam.transform.position);
            transform.Rotate(0f, 180f, 0f);
        }

        public void SetHealth(int hp, int maxHp)
        {
            _fraction = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
            UpdateBar();
        }

        private void UpdateBar()
        {
            // Fill anchored to left edge: scale X and offset to keep left edge pinned
            _fill.localScale = new Vector3(BarWidth * _fraction, BarHeight, 1f);
            _fill.localPosition = new Vector3(-(1f - _fraction) * BarWidth * 0.5f, 0f, -0.001f);
        }

        public static WorldHealthBar Create(Transform parent, bool isLocalPlayer)
        {
            var root = new GameObject("WorldHealthBar");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, HeightOffset, 0f);

            // Background (dark)
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "HpBg";
            bg.transform.SetParent(root.transform, false);
            bg.transform.localScale = new Vector3(BarWidth, BarHeight, 1f);
            Object.Destroy(bg.GetComponent<Collider>());
            var bgRend = bg.GetComponent<Renderer>();
            bgRend.material = MakeUnlitMaterial(bgRend, new Color(0.15f, 0.15f, 0.15f));
            bgRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRend.receiveShadows = false;

            // HP fill
            var fill = GameObject.CreatePrimitive(PrimitiveType.Quad);
            fill.name = "HpFill";
            fill.transform.SetParent(root.transform, false);
            fill.transform.localScale = new Vector3(BarWidth, BarHeight, 1f);
            fill.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            Object.Destroy(fill.GetComponent<Collider>());
            var fillRend = fill.GetComponent<Renderer>();
            fillRend.material = MakeUnlitMaterial(fillRend, isLocalPlayer
                ? new Color(0.2f, 0.75f, 0.3f)
                : new Color(0.85f, 0.15f, 0.15f));
            fillRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fillRend.receiveShadows = false;

            var comp = root.AddComponent<WorldHealthBar>();
            comp._fill = fill.transform;

            // Keep the world-space health bar implementation wired for future use, but hide
            // it for now while health is represented by the HUD unit frames.
            root.SetActive(WorldHealthBarsVisible);

            return comp;
        }

        private static Material MakeUnlitMaterial(Renderer sourceRenderer, Color color)
        {
            Shader? shader =
                Shader.Find("Unlit/Color") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");

            var template = sourceRenderer.sharedMaterial;
            var mat = shader != null
                ? new Material(shader)
                : new Material(template);
            mat.color = color;
            return mat;
        }
    }
}
