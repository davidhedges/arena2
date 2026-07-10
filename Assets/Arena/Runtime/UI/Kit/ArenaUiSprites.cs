#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Arena.UI
{
    /// <summary>
    /// Procedurally generated sprite assets for the UI kit: rounded 9-slice
    /// panels, border rings, soft drop shadows, and a vertical sheen gradient.
    /// uGUI renders untextured quads by default; these give the kit the
    /// rounded/shadowed/layered rendering a web stack gets for free, with no
    /// art dependencies and full theme-color tintability (all sprites are white).
    /// </summary>
    internal static class ArenaUiSprites
    {
        public const float PanelRadius = 14f;
        public const float SmallRadius = 8f;
        private const float Stroke = 2f;
        private const float ShadowBlur = 26f;

        private static Sprite? s_panel;
        private static Sprite? s_panelSmall;
        private static Sprite? s_ring;
        private static Sprite? s_ringSmall;
        private static Sprite? s_shadow;
        private static Sprite? s_sheen;

        /// <summary>Rounded rect fill, radius 14. Use with Image.Type.Sliced.</summary>
        public static Sprite Panel
        {
            get
            {
                if (s_panel == null)
                    s_panel = BuildRounded(64, PanelRadius, ringStroke: 0f);
                return s_panel;
            }
        }

        /// <summary>Rounded rect fill, radius 8, for buttons/cells/rows.</summary>
        public static Sprite PanelSmall
        {
            get
            {
                if (s_panelSmall == null)
                    s_panelSmall = BuildRounded(40, SmallRadius, ringStroke: 0f);
                return s_panelSmall;
            }
        }

        /// <summary>2px rounded border ring, radius 14.</summary>
        public static Sprite Ring
        {
            get
            {
                if (s_ring == null)
                    s_ring = BuildRounded(64, PanelRadius, ringStroke: Stroke);
                return s_ring;
            }
        }

        /// <summary>2px rounded border ring, radius 8.</summary>
        public static Sprite RingSmall
        {
            get
            {
                if (s_ringSmall == null)
                    s_ringSmall = BuildRounded(40, SmallRadius, ringStroke: Stroke);
                return s_ringSmall;
            }
        }

        /// <summary>Soft drop shadow. Stretch ~26 units beyond the casting rect.</summary>
        public static Sprite Shadow
        {
            get
            {
                if (s_shadow == null)
                    s_shadow = BuildShadow(128, PanelRadius, ShadowBlur);
                return s_shadow;
            }
        }

        /// <summary>Vertical gradient, opaque at top fading to clear. Tint for sheens.</summary>
        public static Sprite Sheen
        {
            get
            {
                if (s_sheen == null)
                    s_sheen = BuildSheen(4, 64);
                return s_sheen;
            }
        }

        // ------------------------------------------------------------------
        // Authored-art override layer. Drop a PNG into Assets/Arena/Resources/
        // UI/Kit/ with one of the well-known names below (import as
        // "Sprite (2D and UI)") and the kit swaps it in everywhere, replacing
        // the procedural sprite. 9-slice borders are defined here in code so
        // no importer fiddling is needed — author ornament inside the listed
        // corner margins.
        // ------------------------------------------------------------------

        public readonly struct SurfaceSprite
        {
            public readonly Sprite? Sprite;
            public readonly bool Authored;

            public SurfaceSprite(Sprite? sprite, bool authored)
            {
                Sprite = sprite;
                Authored = authored;
            }
        }

        private const string AuthoredRoot = "UI/Kit/";
        private static readonly Dictionary<string, Sprite?> s_authoredCache = new();

        // name -> 9-slice border in source pixels (0 = stretched whole).
        private static readonly Dictionary<string, float> AuthoredBorders = new()
        {
            ["window_fill"] = 48f,
            ["window_frame"] = 64f,
            ["header_plate"] = 24f,
            ["button"] = 16f,
            ["button_glow"] = 16f,
            ["slot_frame"] = 0f,
            ["divider"] = 0f,
        };

        /// <summary>Window backdrop fill (dark texture). Fallback: rounded panel.</summary>
        public static SurfaceSprite WindowFill => Resolve("window_fill", Panel);

        /// <summary>Ornate window border, transparent center. Fallback: hairline ring.</summary>
        public static SurfaceSprite WindowFrame => Resolve("window_frame", Ring);

        /// <summary>Header/footer band plate. Fallback: small rounded panel.</summary>
        public static SurfaceSprite HeaderPlate => Resolve("header_plate", PanelSmall);

        /// <summary>Button plate (author neutral/desaturated; the theme tints it per style).</summary>
        public static SurfaceSprite ButtonFill => Resolve("button", PanelSmall);

        /// <summary>Button hover glow ring. Fallback: small ring.</summary>
        public static SurfaceSprite ButtonGlow => Resolve("button_glow", RingSmall);

        /// <summary>Horizontal ornament/divider. Fallback: none (plain hairline quad).</summary>
        public static SurfaceSprite Divider => Resolve("divider", null);

        /// <summary>
        /// Item/ability slot frame. Falls back to the action bar's authored
        /// slot art so inventory cells match the action bar out of the box.
        /// </summary>
        private static Sprite? s_actionBarSlot;
        private static bool s_actionBarSlotResolved;

        public static SurfaceSprite SlotFrame
        {
            get
            {
                SurfaceSprite authored = Resolve("slot_frame", null);
                if (authored.Sprite != null)
                    return authored;

                if (!s_actionBarSlotResolved)
                {
                    s_actionBarSlot = Resources.Load<Sprite>("UI/ActionBar/slot");
                    s_actionBarSlotResolved = true;
                }

                return new SurfaceSprite(s_actionBarSlot, s_actionBarSlot != null);
            }
        }

        private static SurfaceSprite Resolve(string name, Sprite? fallback)
        {
            if (!s_authoredCache.TryGetValue(name, out Sprite? sprite))
            {
                sprite = LoadAuthored(name);
                s_authoredCache[name] = sprite;
            }

            return sprite != null
                ? new SurfaceSprite(sprite, true)
                : new SurfaceSprite(fallback, false);
        }

        private static Sprite? LoadAuthored(string name)
        {
            Texture2D? texture = Resources.Load<Texture2D>(AuthoredRoot + name);
            if (texture == null)
                return null;

            AuthoredBorders.TryGetValue(name, out float border);
            border = Mathf.Min(border, Mathf.Min(texture.width, texture.height) * 0.5f - 1f);
            border = Mathf.Max(border, 0f);
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            sprite.name = $"ArenaUiAuthored_{name}";
            return sprite;
        }

        /// <summary>Signed distance to a rounded rect centered in a size-S texture.</summary>
        private static float RoundedDistance(float x, float y, float size, float margin, float radius)
        {
            float half = size * 0.5f;
            float px = Mathf.Abs(x + 0.5f - half);
            float py = Mathf.Abs(y + 0.5f - half);
            float hx = half - margin - radius;
            float hy = half - margin - radius;
            float qx = Mathf.Max(px - hx, 0f);
            float qy = Mathf.Max(py - hy, 0f);
            return Mathf.Sqrt(qx * qx + qy * qy) - radius;
        }

        private static Sprite BuildRounded(int size, float radius, float ringStroke)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            const float margin = 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = RoundedDistance(x, y, size, margin, radius);
                    float alpha = Mathf.Clamp01(0.5f - d);
                    if (ringStroke > 0f)
                        alpha -= Mathf.Clamp01(0.5f - (d + ringStroke));
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = margin + radius + 2f;
            return MakeSprite(texture, size, border);
        }

        private static Sprite BuildShadow(int size, float radius, float blur)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            float margin = blur + 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = RoundedDistance(x, y, size, margin, radius);
                    float alpha = d <= 0f
                        ? 1f
                        : Mathf.Pow(1f - Mathf.Clamp01(d / blur), 2.4f);
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = margin + radius + 2f;
            return MakeSprite(texture, size, border);
        }

        private static Sprite BuildSheen(int width, int height)
        {
            Texture2D texture = NewTexture(width, height);
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)(height - 1);
                byte a = (byte)Mathf.RoundToInt(Mathf.Pow(t, 1.6f) * 255f);
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color32(255, 255, 255, a);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
        }

        private static Texture2D NewTexture(int width, int height)
        {
            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                name = "ArenaUiSprite",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return texture;
        }

        private static Sprite MakeSprite(Texture2D texture, int size, float border)
        {
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            sprite.name = "ArenaUiSprite";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
