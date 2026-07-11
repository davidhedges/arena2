#nullable enable

using System.IO;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class SelectedTargetIndicatorTests
    {
        private const string IndicatorPath = "Assets/Arena/Runtime/Presentation/Targeting/SelectedTargetIndicator.cs";
        private const string AimIndicatorPath = "Assets/Arena/Runtime/Presentation/AimIndicator.cs";
        private const string SpellInputHandlerPath = "Assets/Arena/Runtime/Input/SpellInputHandler.cs";
        private const string TargetSelectorPath = "Assets/Arena/Runtime/Combat/TargetSelector.cs";
        private const string PlayerEntityPath = "Assets/Arena/Runtime/Entity/PlayerEntity.cs";

        [Test]
        public void SelectedTargetIndicator_UsesTerrainConformingPresentationGeometry()
        {
            string source = File.ReadAllText(IndicatorPath);

            Assert.That(source, Does.Contain("public const float RadiusMeters = 1.0f;"));
            Assert.That(source, Does.Contain("new GameObject(\"SelectedTargetIndicator\")"));
            Assert.That(source, Does.Contain("CreateSurface(\"SelectedTargetIndicator_Arc\""));
            Assert.That(source, Does.Contain("private const int SegmentCount = 144;"));
            Assert.That(source, Does.Contain("private const int RadialBandCount = 32;"));
            Assert.That(source, Does.Contain("private const float ArcAngleDegrees = 260f;"));
            Assert.That(source, Does.Contain("private const float InnerFadeRadius = RadiusMeters * 0.20f;"));
            Assert.That(source, Does.Contain("private const float EndFadeArcFraction = 0.18f;"));
            Assert.That(source, Does.Contain("private const float SurfaceOffsetMeters = 0.04f;"));
            Assert.That(source, Does.Contain("Shader.Find(\"Arena/Presentation/TargetIndicatorVertexColor\")"));
            Assert.That(source, Does.Contain("var colors = new Color[vertexCount];"));
            Assert.That(source, Does.Contain("EvaluateRadialAlpha(radialT)"));
            Assert.That(source, Does.Contain("EvaluateArcAlpha(arcT)"));
            Assert.That(source, Does.Contain("ResolveCameraFacingDirection(center)"));
            Assert.That(source, Does.Contain("Camera.main"));
            Assert.That(source, Does.Contain("Terrain.activeTerrains"));
            Assert.That(source, Does.Contain("terrain.SampleHeight(world)"));
            Assert.That(source, Does.Contain("ShadowCastingMode.Off"));
            Assert.That(source, Does.Contain("renderer.receiveShadows = false"));
            Assert.That(source, Does.Contain("RenderQueue.Transparent"));
            Assert.That(source, Does.Contain("RebuildPositionEpsilonSquared"));
            Assert.That(source, Does.Not.Contain("SelectedTargetIndicator_SoftArc"));
            Assert.That(source, Does.Not.Contain("SelectedTargetIndicator_GlowArc"));
            Assert.That(source, Does.Not.Contain("SelectedTargetIndicator_EdgeArc"));
            Assert.That(source, Does.Not.Contain("Resources.Load"));
            Assert.That(source, Does.Not.Contain("Physics.Raycast"));
            Assert.That(source, Does.Not.Contain("FindObjectsByType"));
        }

        [Test]
        public void PlayerEntity_DrivesSelectedTargetIndicatorFromSelectedStateOnly()
        {
            string source = File.ReadAllText(PlayerEntityPath);

            Assert.That(source, Does.Contain("using Arena.Presentation.Targeting;"));
            Assert.That(source, Does.Contain("SelectedTargetIndicator? _selectedTargetIndicator"));
            Assert.That(source, Does.Contain("RefreshSelectedTargetIndicator();"));
            Assert.That(source, Does.Contain("_isSelected && !IsLocalPlayer && IsAlive && !IsEliminated && !IsDestroyed"));
            Assert.That(source, Does.Not.Contain("SetFollowTransform"));
        }

        [Test]
        public void AreaSpellAimCursorPath_AvoidsDuplicateRefreshAndPerVertexSurfaceQueries()
        {
            string aimSource = File.ReadAllText(AimIndicatorPath);
            string spellInputSource = File.ReadAllText(SpellInputHandlerPath);
            string targetSelectorSource = File.ReadAllText(TargetSelectorPath);

            Assert.That(aimSource, Does.Not.Contain("private void Update()"));
            Assert.That(aimSource, Does.Contain("private const int SurfaceSampleRingCount = 4;"));
            Assert.That(aimSource, Does.Contain("EnsureMeshBuffers();"));
            Assert.That(aimSource, Does.Contain("SampleSurfaceRings("));
            Assert.That(aimSource, Does.Contain("_meshTopologyInitialized"));
            Assert.That(spellInputSource, Does.Contain("aim.RefreshFromCursor(input.MousePosition)"));
            Assert.That(targetSelectorSource, Does.Contain("if (!aimActive && input.LeftMousePressed)"));
            Assert.That(targetSelectorSource, Does.Contain("if (!aimActive && !input.CursorLocked)"));
        }
    }
}
