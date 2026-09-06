#nullable enable

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Generates the painted cavern backdrop that sits behind every silhouette
    /// band, as a seeded panoramic skybox tinted by the descent curve.
    /// </summary>
    /// <remarks>
    /// The FARTHEST layer is the one place geometry buys nothing. At the far
    /// band's parallax it barely shifts regardless, and procedural cones are at
    /// their worst there — hard faceted silhouettes with no detail. A painted
    /// layer carries soft haze and value depth instead, for less cost.
    ///
    /// It does not replace the bands in front of it. A backdrop is at infinity,
    /// so everything on it sits at ONE depth: no parallax, and no silhouette can
    /// occlude another. Those two properties are what actually encode vastness,
    /// which is why the near and mid bands stay real geometry and this only
    /// fills in behind them.
    ///
    /// Generated per seed rather than authored once, so the same rock formation
    /// does not appear in every dungeon of every run.
    /// </remarks>
    internal static class CavernBackdrop
    {
        private const string GeneratedFolder = "Assets/Arena/Content/Art/Generated";

        /// <summary>Where the generated dungeon's own backdrop lands.</summary>
        /// <remarks>
        /// Callers that are not the dungeon MUST pass their own path. The
        /// texture is a shared asset referenced by GUID from whichever scene
        /// generated it, so two scenes writing this one path means rebuilding
        /// either silently repaints the other.
        /// </remarks>
        internal const string DungeonBackdropAssetPath = GeneratedFolder + "/CavernBackdrop.png";
        private const string PanoramicShaderName = "Skybox/Panoramic";

        private const int Width = 2048;
        private const int Height = 1024;

        /// <summary>
        /// Fraction of image height over which the underglow reaches, measured
        /// from straight down.
        /// </summary>
        /// <remarks>
        /// MEASURED WRONG AT 0.44: that assumption was that the dungeon occludes
        /// everything below the horizon. It does not — the dungeon is a platform
        /// standing in open space, so from any edge the player looks straight
        /// out into the panorama's lower half. Lighting that whole half put a
        /// cream-coloured field across most of the frame and silhouetted the
        /// spires, the architecture and the player against it. The distance must
        /// be the DARKEST thing in a cavern; the lava is an accent, not a floor
        /// of light.
        /// </remarks>
        private const float GlowBandTop = 0.18f;

        /// <summary>Blur radius in pixels. This is what keeps it painterly rather than faceted.</summary>
        // 5 left ridge edges hard; 14 smeared every silhouette into a blob. The
        // hard edge that prompted raising this was really a CONTRAST problem —
        // a bright field behind a dark mass — and darkening the palette fixed
        // it, so this only needs to take the aliasing off.
        private const int BlurRadius = 7;

        /// <summary>
        /// Horizon layers, farthest first. Each is a noise profile at its own
        /// height, hazed toward the void by distance.
        /// </summary>
        private readonly struct Ridge
        {
            internal Ridge(float baseHeight, float amplitude, float frequency, float haze, float roughness)
            {
                BaseHeight = baseHeight;
                Amplitude = amplitude;
                Frequency = frequency;
                Haze = haze;
                Roughness = roughness;
            }

            /// <summary>Where the ridge sits, as a fraction of image height.</summary>
            internal float BaseHeight { get; }

            internal float Amplitude { get; }
            internal float Frequency { get; }

            /// <summary>How far this ridge is washed toward the background value.</summary>
            internal float Haze { get; }

            /// <summary>Weight of the higher octaves — nearer ridges are more jagged.</summary>
            internal float Roughness { get; }
        }

        /// <summary>
        /// Rock rising from below, farthest first.
        /// </summary>
        /// <remarks>
        /// The two nearest layers exist to break up the LIT zone. With ridges
        /// only around the horizon, everything below the nearest one resolved to
        /// a single smooth vertical gradient — bright, featureless, and reading
        /// as glowing fog rather than stone. Structure inside the bright mass
        /// has to come from more silhouette edges crossing it.
        /// </remarks>
        private static readonly Ridge[] FloorRidges =
        {
            new(baseHeight: 0.54f, amplitude: 0.13f, frequency: 3f, haze: 0.70f, roughness: 0.25f),
            new(baseHeight: 0.45f, amplitude: 0.16f, frequency: 5f, haze: 0.52f, roughness: 0.45f),
            new(baseHeight: 0.35f, amplitude: 0.18f, frequency: 8f, haze: 0.34f, roughness: 0.70f),
            new(baseHeight: 0.25f, amplitude: 0.15f, frequency: 12f, haze: 0.18f, roughness: 0.85f),
            new(baseHeight: 0.15f, amplitude: 0.13f, frequency: 17f, haze: 0.06f, roughness: 1.0f),
        };

        /// <summary>
        /// Rock descending from above, farthest first. Heights are the level
        /// the mass hangs DOWN to.
        /// </summary>
        /// <remarks>
        /// This is what makes the image a cavern rather than a landscape.
        /// Without a ceiling mass the composition is rock along the bottom and
        /// open gradient above — a mountain range at dusk, with a clean horizon
        /// line and a glow that reads as sky. A cave encloses: rock above, rock
        /// below, and the deep dark pinched into a band between them.
        /// </remarks>
        /// <remarks>
        /// MEASURED: the horizon is v = 0.5, and standing in the dungeon the
        /// player can only see ABOVE it — everything below is occluded by the
        /// dungeon's own floor and walls. With the ceiling starting at 0.78 the
        /// visible half of the panorama measured mean luminance 8/255 against a
        /// max of 10, i.e. flat black, while all the contrast (mean 47, peaks
        /// over 128) sat in the half nobody can see. The masses now hang into
        /// the band the camera actually looks at.
        /// </remarks>
        private static readonly Ridge[] CeilingRidges =
        {
            // Farthest first. For a CEILING that means highest first: a nearer
            // mass hangs lower into view, so the low layer is the near one and
            // takes the least haze. Reversed, the depth cue inverted — the
            // low-hanging mass washed out while the distant one stayed crisp.
            new(baseHeight: 0.70f, amplitude: 0.13f, frequency: 7f, haze: 0.62f, roughness: 0.30f),
            new(baseHeight: 0.58f, amplitude: 0.10f, frequency: 4f, haze: 0.34f, roughness: 0.65f),
        };

        internal static void Apply(int seed, CavernDepthProfile depth, string? backdropAssetPath = null)
        {
            string assetPath = backdropAssetPath ?? DungeonBackdropAssetPath;
            if (!assetPath.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".png", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Backdrop path must be a .png under '{GeneratedFolder}'; got '{assetPath}'.",
                    nameof(backdropAssetPath));
            }

            Color[] pixels = Paint(seed, depth);
            Blur(pixels);
            ToGammaSpace(pixels);

            Texture2D texture = new(Width, Height, TextureFormat.RGB24, mipChain: false, linear: false);
            texture.SetPixels(pixels);
            texture.Apply();

            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            WriteBackdropAsset(png, assetPath);

            ApplyAuthored(assetPath);
        }

        /// <summary>Use a scene's painted panorama without regenerating its artwork.</summary>
        internal static void ApplyAuthored(string assetPath)
        {
            Texture2D? imported = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (imported == null)
                throw new InvalidOperationException($"Generated backdrop '{assetPath}' did not import.");

            Shader? shader = Shader.Find(PanoramicShaderName);
            if (shader == null)
                throw new InvalidOperationException($"Required skybox shader '{PanoramicShaderName}' is missing.");

            Material skybox = new(shader) { name = "CavernBackdrop (Generated)" };
            skybox.SetTexture("_MainTex", imported);
            skybox.SetFloat("_Mapping", 1f);   // latitude/longitude layout
            skybox.SetFloat("_ImageType", 0f); // full 360
            skybox.SetFloat("_Exposure", 1f);
            skybox.SetFloat("_Rotation", 0f);
            skybox.SetColor("_Tint", Color.white);

            // The camera already clears with CameraClearFlags.Skybox; until now
            // RenderSettings.skybox was null, which is why it fell through to the
            // flat clear colour.
            RenderSettings.skybox = skybox;
        }

        private static Color[] Paint(int seed, CavernDepthProfile depth)
        {
            var pixels = new Color[Width * Height];

            float[][] floorProfiles = new float[FloorRidges.Length][];
            for (int ridge = 0; ridge < FloorRidges.Length; ridge++)
                floorProfiles[ridge] = BuildRidgeProfile(seed, ridge, FloorRidges[ridge]);

            float[][] ceilingProfiles = new float[CeilingRidges.Length][];
            for (int ridge = 0; ridge < CeilingRidges.Length; ridge++)
                ceilingProfiles[ridge] = BuildRidgeProfile(seed, 64 + ridge, CeilingRidges[ridge]);

            Color background = depth.Background;
            Color glow = depth.GlowColor;
            System.Random dither = new(seed ^ StairForge.StableHash("cavern-backdrop:dither"));

            for (int y = 0; y < Height; y++)
            {
                // y = 0 is straight down in a latlong panorama, which is also
                // where the lava is. y = Height-1 is straight up.
                float verticalFraction = y / (float)(Height - 1);

                for (int x = 0; x < Width; x++)
                {
                    Color colour = VoidColour(background, glow, verticalFraction, depth);

                    // Nearest ridge that still covers this pixel wins, so ridges
                    // occlude each other in the painted layer too.
                    for (int ridge = FloorRidges.Length - 1; ridge >= 0; ridge--)
                    {
                        if (verticalFraction > floorProfiles[ridge][x])
                            continue;

                        colour = RockColour(background, glow, FloorLit(verticalFraction, depth), FloorRidges[ridge]);
                        break;
                    }

                    for (int ridge = CeilingRidges.Length - 1; ridge >= 0; ridge--)
                    {
                        if (verticalFraction < ceilingProfiles[ridge][x])
                            continue;

                        colour = RockColour(background, glow, CeilingLit(verticalFraction, depth), CeilingRidges[ridge]);
                        break;
                    }

                    // A smooth near-black gradient quantises to visible contour
                    // bands at 8 bits. Sub-LSB noise costs nothing and removes
                    // them.
                    float noise = ((float)dither.NextDouble() - 0.5f) / 255f;
                    pixels[y * Width + x] = new Color(
                        colour.r + noise,
                        colour.g + noise,
                        colour.b + noise,
                        1f);
                }
            }

            return pixels;
        }

        private static Color VoidColour(Color background, Color glow, float verticalFraction, CavernDepthProfile depth)
        {
            // The open deep between the floor and ceiling masses stays DARK.
            // Only a hint of haze survives near the lava.
            //
            // Blooming the void as hard as the rock was the mistake in the first
            // pass: down near the lava, empty space and stone resolved to the
            // same colour, so the silhouette vanished exactly where it should be
            // strongest — and a bright gradient with rock only above it reads as
            // sky no matter how dark you make it. Light belongs on the stone.
            float bloom = Mathf.Pow(Mathf.Clamp01(1f - verticalFraction / GlowBandTop), 3.2f) * depth.Glow * 0.10f;
            return Color.Lerp(background, glow, bloom);
        }

        /// <summary>Lava light on rock rising from below.</summary>
        private static float FloorLit(float verticalFraction, CavernDepthProfile depth) =>
            Mathf.Pow(Mathf.Clamp01(1f - verticalFraction / GlowBandTop), 1.4f) * depth.Glow * MaxLit;

        /// <summary>
        /// Lava light on the ceiling mass, which catches it on its UNDERSIDES.
        /// </summary>
        /// <remarks>
        /// Keyed off height above the horizon rather than the floor's falloff.
        /// Sharing the floor term left every ceiling pixel at exactly zero light
        /// — a flat black slab in the only part of the panorama the player can
        /// actually see. Light from below striking a ceiling is also the single
        /// most cave-like cue available here.
        /// </remarks>
        private static float CeilingLit(float verticalFraction, CavernDepthProfile depth) =>
            Mathf.Pow(Mathf.Clamp01(1f - (verticalFraction - 0.5f) / 0.5f), 1.8f) * depth.Glow * 0.22f;

        /// <summary>
        /// Ceiling on how far any painted rock may be pushed toward the raw lava
        /// colour. Distant rock catching lava light is never AS bright as the
        /// lava; letting it reach the full colour is what flattened the backdrop
        /// into a uniform glow with no stone left in it.
        /// </summary>
        private const float MaxLit = 0.55f;

        private static Color RockColour(
            Color background,
            Color glow,
            float lit,
            Ridge ridge)
        {
            // Unlit rock sits DARKER than the void it stands against, so the
            // upper masses read as solid mass against the deep. Lit rock goes
            // brighter than anything else in the image. All the contrast in the
            // backdrop comes from that one spread.
            Color rock = new(background.r * 0.25f, background.g * 0.25f, background.b * 0.25f, 1f);
            Color surface = Color.Lerp(rock, glow, lit);

            // Distance haze washes the far ridges toward the void so their
            // silhouettes stay soft and unmeasurable.
            return Color.Lerp(surface, background, ridge.Haze);
        }

        /// <summary>
        /// One ridge silhouette as a height per column, wrapping seamlessly
        /// around the panorama.
        /// </summary>
        private static float[] BuildRidgeProfile(int seed, int ridgeIndex, Ridge ridge)
        {
            var profile = new float[Width];
            System.Random rng = new(seed ^ StairForge.StableHash($"cavern-backdrop:ridge:{ridgeIndex}"));

            // Three octaves of wrapping value noise. Period must divide the
            // panorama width or the seam is visible looking due south.
            int[] periods =
            {
                Mathf.Max(2, Mathf.RoundToInt(ridge.Frequency)),
                Mathf.Max(2, Mathf.RoundToInt(ridge.Frequency * 2.7f)),
                Mathf.Max(2, Mathf.RoundToInt(ridge.Frequency * 6.1f)),
            };
            float[] weights = { 1f, ridge.Roughness, ridge.Roughness * ridge.Roughness * 0.75f };

            var octaves = new float[periods.Length][];
            for (int octave = 0; octave < periods.Length; octave++)
            {
                var control = new float[periods[octave]];
                for (int index = 0; index < control.Length; index++)
                    control[index] = (float)rng.NextDouble();
                octaves[octave] = control;
            }

            float weightSum = 0f;
            foreach (float weight in weights)
                weightSum += weight;

            for (int x = 0; x < Width; x++)
            {
                float azimuth = x / (float)Width;
                float value = 0f;
                for (int octave = 0; octave < octaves.Length; octave++)
                    value += SampleWrapped(octaves[octave], azimuth) * weights[octave];

                value = value / weightSum * 2f - 1f;
                profile[x] = Mathf.Clamp01(ridge.BaseHeight + value * ridge.Amplitude);
            }

            return profile;
        }

        private static float SampleWrapped(float[] control, float position01)
        {
            float scaled = position01 * control.Length;
            int index = Mathf.FloorToInt(scaled);
            float fraction = scaled - index;
            float a = control[((index % control.Length) + control.Length) % control.Length];
            float b = control[((index + 1) % control.Length + control.Length) % control.Length];

            // Smoothstep rather than linear: linear interpolation leaves visible
            // creases at every control point.
            return Mathf.Lerp(a, b, fraction * fraction * (3f - 2f * fraction));
        }

        /// <summary>
        /// Encodes the painted linear values into gamma space for storage.
        /// </summary>
        /// <remarks>
        /// The project renders in LINEAR colour space, so every Color composed
        /// above is linear — including `depth.Background`, which the camera also
        /// uses as its clear colour. Writing those values straight into an sRGB
        /// texture stores 0.070 as byte 18, which the shader reads back as
        /// (18/255)^2.2 = 0.0028 linear: roughly twenty-five times darker than
        /// the clear colour it is supposed to match. The backdrop rendered as
        /// effectively black, which is indistinguishable from having no backdrop
        /// at all.
        ///
        /// Applied after the blur so the blur still averages in linear, which is
        /// the only space in which averaging light is meaningful.
        /// </remarks>
        internal static void ToGammaSpace(Color[] pixels)
        {
            for (int index = 0; index < pixels.Length; index++)
            {
                Color linear = pixels[index];
                pixels[index] = new Color(
                    LinearToGamma(linear.r),
                    LinearToGamma(linear.g),
                    LinearToGamma(linear.b),
                    1f);
            }
        }

        // Unity's exact sRGB transfer curve, written out rather than using
        // Color.gamma, which is a native ECall and therefore unavailable to the
        // headless preview harness that renders this texture without Unity.
        private static float LinearToGamma(float value)
        {
            if (value <= 0f)
                return 0f;
            if (value <= 0.0031308f)
                return value * 12.92f;
            if (value < 1f)
                return 1.055f * Mathf.Pow(value, 0.4166667f) - 0.055f;
            return Mathf.Pow(value, 0.45454545f);
        }

        /// <summary>
        /// Separable box blur, wrapping horizontally. This is the step that turns
        /// hard procedural edges into something that reads as painted.
        /// </summary>
        private static void Blur(Color[] pixels)
        {
            var scratch = new Color[pixels.Length];
            float inverse = 1f / (BlurRadius * 2 + 1);

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    float r = 0f, g = 0f, b = 0f;
                    for (int offset = -BlurRadius; offset <= BlurRadius; offset++)
                    {
                        Color sample = pixels[row + ((x + offset) % Width + Width) % Width];
                        r += sample.r;
                        g += sample.g;
                        b += sample.b;
                    }

                    scratch[row + x] = new Color(r * inverse, g * inverse, b * inverse, 1f);
                }
            }

            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    float r = 0f, g = 0f, b = 0f;
                    for (int offset = -BlurRadius; offset <= BlurRadius; offset++)
                    {
                        int row = Mathf.Clamp(y + offset, 0, Height - 1);
                        Color sample = scratch[row * Width + x];
                        r += sample.r;
                        g += sample.g;
                        b += sample.b;
                    }

                    pixels[y * Width + x] = new Color(r * inverse, g * inverse, b * inverse, 1f);
                }
            }
        }

        private static void WriteBackdropAsset(byte[] png, string assetPath)
        {
            if (!AssetDatabase.IsValidFolder(GeneratedFolder))
                AssetDatabase.CreateFolder("Assets/Arena/Content/Art", "Generated");

            File.WriteAllBytes(assetPath, png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // Written as an imported asset rather than embedded in the scene: a
            // 2048x1024 texture serialised into scene YAML bloats the scene file
            // and its load time for no benefit.
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer)
                throw new InvalidOperationException($"Generated backdrop '{assetPath}' has no texture importer.");

            importer.textureType = TextureImporterType.Default;
            importer.wrapModeU = TextureWrapMode.Repeat;  // seamless around the horizon
            importer.wrapModeV = TextureWrapMode.Clamp;   // poles must not wrap
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.maxTextureSize = Width;
            importer.SaveAndReimport();
        }
    }
}
