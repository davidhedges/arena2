#nullable enable
using UnityEngine;
using SpacetimeDB.Types;
using Arena.Entity;
using Arena.Presentation;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Beam rendering from caster to target.
    /// InstantBeam: procedural LineRenderer beam (port of instant_beam_vfx.ts).
    /// Electrocute: a camera-aligned LineRenderer ribbon running hand→target,
    /// rendered with the Arena/Presentation/ElectrocuteBeam shader over the Piloto
    /// lightning-bolt flipbook texture (real drawn bolts, animated in-shader), with
    /// the Ray.prefab flecks layer looping at both endpoints. A ribbon is used
    /// instead of the Ray.prefab column because that prefab's layers are
    /// camera-facing billboards that cannot be rotated onto an arbitrary
    /// hand→target axis. The procedural jagged beam remains only as a
    /// missing-asset fallback.
    /// </summary>
    public class BeamVFX : ISpellVFX
    {
        private const int BeamSegments = 16;

        // TS: resolveInstantBeamVisualParams() → durationSeconds: 0.5
        // Electrocute is charge-on-release, so it uses a large max and terminates on fizzle.
        private const float InstantBeamDuration = 0.5f;
        private const float ElectrocuteMaxDuration = 10f;
        // TS: fade starts at 82% of duration
        private const float FadeStartFraction = 0.82f;

        private const float ProceduralStartWidth = 0.2f;
        private const float ProceduralEndWidth = 0.15f;

        private const string ElectrocuteMaterialResourcePath = "CombatVFX/Beam/ElectrocuteBeam";
        private const string ElectrocuteGlowMaterialResourcePath = "CombatVFX/Beam/ElectrocuteBeamGlow";
        private const string RayPrefabResourcePath = "CombatVFX/Beam/Ray";
        private const string FlecksLayerName = "Flecks_Shiny_Additive";
        private const float ElectrocuteBeamWidth = 1.3f;
        // The soft wisp body sits under the bolts — mirrors the Ray.prefab
        // layering where Beam_Fire_Bg_Add backs the lightning sheets.
        private const float ElectrocuteGlowWidth = 1.0f;
        // One bolt-texture repeat per this many meters; matches the sheet's aspect
        // (2048px wide per 512px row) at the beam width above.
        private const float BoltRepeatMeters = 5.2f;
        // Re-tile only on meaningful length changes to avoid per-frame material churn.
        private const float RetileThresholdMeters = 0.25f;
        private const float FlecksEmitterRadius = 0.3f;

        private readonly GameObject _go;
        private LineRenderer? _line;
        private Material? _material;
        private LineRenderer? _glowLine;
        private Material? _glowMaterial;
        private bool _usesBoltMaterial;
        private float _lastTiledLength = -1f;
        private GameObject? _muzzleEmitter;
        private GameObject? _impactEmitter;
        private readonly SpacetimeDB.Identity _caster;
        private readonly bool _isJagged;
        private readonly float _jaggedAmplitude;
        private readonly float _jaggedFrequency;
        private readonly float _duration;
        private readonly float _maxLength;
        private readonly Transform? _originAnchor;
        private Vector3 _origin;
        private Vector3 _direction;
        private Vector3? _endPoint;
        private Vector3 _currentEnd;
        private float _elapsed;
        private bool _finished;
        private bool _disposed;

        private readonly Color _color;
        private readonly string _spellKind;

        internal BeamVFX(CombatVFXTemplateContext context, bool jagged, Color color)
        {
            _caster = context.Caster;
            _spellKind = context.ActionKind;
            _isJagged = jagged;
            _jaggedAmplitude = jagged ? 0.4f : 0f;
            _jaggedFrequency = jagged ? 8f : 0f;
            _color = color;
            _duration = jagged ? ElectrocuteMaxDuration : InstantBeamDuration;
            _maxLength = context.MaxDistance;
            _originAnchor = context.FollowAnchor;

            _origin = context.Origin;
            _direction = context.Direction;
            if (_direction.sqrMagnitude > 0.0001f)
                _direction.Normalize();
            else
                _direction = Vector3.forward;

            if (context.Point != Vector3.zero)
                _endPoint = context.Point;

            _go = new GameObject($"VFX_Beam_{context.ActionKind}_{context.ActionInstanceId[..Mathf.Min(6, context.ActionInstanceId.Length)]}");

            CreateBeamVisual();
            UpdateBeamPositions();
        }

        public BeamVFX(CombatEvent castEvent, bool jagged, Color color)
        {
            _caster = castEvent.Caster;
            _spellKind = castEvent.ActionKind;
            _isJagged = jagged;
            _jaggedAmplitude = jagged ? 0.4f : 0f;
            _jaggedFrequency = jagged ? 8f : 0f;
            _color = color;
            _duration = jagged ? ElectrocuteMaxDuration : InstantBeamDuration;
            _maxLength = castEvent.MaxDistance;
            _originAnchor = null;

            _origin = new Vector3(castEvent.OriginX, castEvent.OriginY, castEvent.OriginZ);
            _direction = new Vector3(castEvent.DirX, castEvent.DirY, castEvent.DirZ);
            if (_direction.sqrMagnitude > 0.0001f)
                _direction.Normalize();
            else
                _direction = Vector3.forward;

            // If we have a target point, use it
            if (castEvent.PointX != 0 || castEvent.PointY != 0 || castEvent.PointZ != 0)
                _endPoint = new Vector3(castEvent.PointX, castEvent.PointY, castEvent.PointZ);

            _go = new GameObject($"VFX_Beam_{castEvent.ActionKind}_{castEvent.ActionInstanceId[..Mathf.Min(6, castEvent.ActionInstanceId.Length)]}");

            CreateBeamVisual();
            UpdateBeamPositions();
        }

        private void CreateBeamVisual()
        {
            _line = _go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Material? boltMaterial = _isJagged
                ? Resources.Load<Material>(ElectrocuteMaterialResourcePath)
                : null;
            if (boltMaterial != null)
            {
                _usesBoltMaterial = true;
                // Instance the asset so per-beam tiling doesn't mutate it.
                _material = new Material(boltMaterial);
                _line.material = _material;
                _line.startWidth = ElectrocuteBeamWidth;
                _line.endWidth = ElectrocuteBeamWidth;
                _line.numCapVertices = 0;
                _line.positionCount = 2;
                _line.startColor = Color.white;
                _line.endColor = Color.white;
                CreateGlowUnderlay();
                CreateEndpointEmitters();
                return;
            }

            _material = new Material(VFXUtils.GetBeamShader());
            _material.SetColor("_Color", _color);
            _material.SetFloat("_Intensity", 2f);
            _material.SetFloat("_CoreWidth", 0.3f);
            _material.SetFloat("_GlowFalloff", 3f);
            _material.SetFloat("_ScrollSpeed", 4f);

            _line.material = _material;
            _line.startWidth = ProceduralStartWidth;
            _line.endWidth = ProceduralEndWidth;
            _line.positionCount = BeamSegments;
            _line.numCapVertices = 3;
        }

        // The soft energy-wisp body from Ray.prefab's Beam_Fire_Bg_Add layer,
        // as a wider ribbon beneath the bolts.
        private void CreateGlowUnderlay()
        {
            Material? glowMaterial = Resources.Load<Material>(ElectrocuteGlowMaterialResourcePath);
            if (glowMaterial == null)
                return;

            var glowGo = new GameObject("Glow");
            glowGo.transform.SetParent(_go.transform, false);
            _glowMaterial = new Material(glowMaterial);
            _glowLine = glowGo.AddComponent<LineRenderer>();
            _glowLine.material = _glowMaterial;
            _glowLine.startWidth = ElectrocuteGlowWidth;
            _glowLine.endWidth = ElectrocuteGlowWidth;
            _glowLine.numCapVertices = 0;
            _glowLine.positionCount = 2;
            _glowLine.useWorldSpace = true;
            _glowLine.startColor = Color.white;
            _glowLine.endColor = Color.white;
            _glowLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void CreateEndpointEmitters()
        {
            _muzzleEmitter = TryCreateFlecksEmitter("MuzzleFlecks");
            _impactEmitter = TryCreateFlecksEmitter("ImpactFlecks");

            if (_muzzleEmitter != null && _originAnchor != null)
                _muzzleEmitter.transform.SetParent(_originAnchor, false);
        }

        // Instantiates Ray.prefab reduced to its flecks layer: a small looping spark
        // cluster suitable for pinning at the hand muzzle or the impact point.
        private GameObject? TryCreateFlecksEmitter(string name)
        {
            GameObject prefab = Resources.Load<GameObject>(RayPrefabResourcePath);
            if (prefab == null)
                return null;

            GameObject instance = Object.Instantiate(prefab);
            instance.name = $"{_go.name}_{name}";

            // Silence the root one-shot flash layer; keep only the flecks child.
            var rootSystem = instance.GetComponent<ParticleSystem>();
            if (rootSystem != null)
            {
                rootSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var rootEmission = rootSystem.emission;
                rootEmission.enabled = false;
            }

            Transform? flecks = null;
            for (int i = instance.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = instance.transform.GetChild(i);
                if (string.Equals(child.name, FlecksLayerName, System.StringComparison.Ordinal))
                    flecks = child;
                else
                    Object.Destroy(child.gameObject);
            }

            if (flecks == null)
            {
                Object.Destroy(instance);
                return null;
            }

            // The flecks layer is authored partway up the vertical column with a
            // column-sized emission volume; recenter it and shrink the volume so it
            // reads as a point spark cluster. The authored system is one-shot; a
            // channel beam needs it looping for its whole lifetime.
            flecks.localPosition = Vector3.zero;
            var flecksSystem = flecks.GetComponent<ParticleSystem>();
            if (flecksSystem != null)
            {
                var main = flecksSystem.main;
                main.loop = true;
                var shape = flecksSystem.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = FlecksEmitterRadius;
                shape.scale = Vector3.one;
                flecksSystem.Clear(true);
                flecksSystem.Play(true);
            }

            return instance;
        }

        public bool Tick(float dt)
        {
            if (_disposed || _finished) return false;

            _elapsed += dt;

            // Auto-expire after duration (TS: CurvedTrackingBeam returns false after durationSeconds)
            if (_elapsed >= _duration)
            {
                _finished = true;
                return false;
            }

            // Fade out in the last portion of the duration
            float fadeStart = _duration * FadeStartFraction;
            if (_elapsed > fadeStart && _line != null && _material != null)
            {
                float fadeT = (_elapsed - fadeStart) / (_duration - fadeStart);
                float alpha = 1f - fadeT;
                if (_usesBoltMaterial)
                {
                    // The bolt shader multiplies by vertex color; alpha drives fade.
                    Color c = Color.white;
                    c.a = alpha;
                    _line.startColor = c;
                    _line.endColor = c;
                    if (_glowLine != null)
                    {
                        _glowLine.startColor = c;
                        _glowLine.endColor = c;
                    }
                }
                else
                {
                    Color c = _color;
                    c.a = alpha;
                    _material.SetColor("_Color", c);
                    _line.startWidth = ProceduralStartWidth * alpha;
                    _line.endWidth = ProceduralEndWidth * alpha;
                }
            }

            // Update origin from caster position
            UpdateOriginFromCaster();
            UpdateBeamPositions();

            return true;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed)
        {
            // Electrocute updates target point
            _endPoint = position;
        }

        public void OnImpact(Vector3 point)
        {
            _endPoint = point;
        }

        public void OnFizzle(Vector3 point)
        {
            _finished = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_muzzleEmitter != null) Object.Destroy(_muzzleEmitter);
            if (_impactEmitter != null) Object.Destroy(_impactEmitter);
            if (_go != null) Object.Destroy(_go);
            if (_material != null) Object.Destroy(_material);
            if (_glowMaterial != null) Object.Destroy(_glowMaterial);
        }

        private void UpdateOriginFromCaster()
        {
            if (_originAnchor != null)
            {
                _origin = _originAnchor.position;
                return;
            }

            var registry = EntityRegistry.Instance;
            if (registry == null) return;

            foreach (var entity in registry.AllPlayers)
            {
                if (entity.Identity.Equals(_caster))
                {
                    // Fallback: chest height offset
                    var pos = entity.SimState.GetRenderPosition();
                    _origin = pos + Vector3.up * 1.2f;
                    break;
                }
            }
        }

        private void UpdateBeamPositions()
        {
            Vector3 end;
            if (_endPoint.HasValue)
            {
                end = _endPoint.Value;
                // Clamp to max length
                if ((_endPoint.Value - _origin).magnitude > _maxLength)
                    end = _origin + (_endPoint.Value - _origin).normalized * _maxLength;
            }
            else
            {
                end = _origin + _direction * _maxLength;
            }

            _currentEnd = end;

            if (_line == null)
                return;

            if (_usesBoltMaterial)
            {
                _line.SetPosition(0, _origin);
                _line.SetPosition(1, end);
                if (_glowLine != null)
                {
                    _glowLine.SetPosition(0, _origin);
                    _glowLine.SetPosition(1, end);
                }
                UpdateBoltTiling(end);
                UpdateEndpointEmitters();
                return;
            }

            for (int i = 0; i < BeamSegments; i++)
            {
                float t = (float)i / (BeamSegments - 1);
                Vector3 point = Vector3.Lerp(_origin, end, t);

                if (_isJagged && i > 0 && i < BeamSegments - 1)
                {
                    // Jagged offset (TS: phase = elapsed * 10.0)
                    float phase = _elapsed * 10f;
                    float freq = _jaggedFrequency;
                    float amp = _jaggedAmplitude;

                    // Calculate perpendicular offsets
                    Vector3 beamDir = (end - _origin).normalized;
                    Vector3 right = Vector3.Cross(beamDir, Vector3.up).normalized;
                    Vector3 up = Vector3.Cross(right, beamDir);

                    float offsetX = Mathf.Sin(t * freq * Mathf.PI + phase) * amp;
                    float offsetY = Mathf.Cos(t * freq * Mathf.PI * 1.3f + phase * 0.7f) * amp * 0.6f;

                    point += right * offsetX + up * offsetY;
                }

                _line.SetPosition(i, point);
            }
        }

        // The ribbon stretches its UVs 0→1 over the whole beam; keep bolt density
        // near-constant in world space by re-tiling as the length changes.
        private void UpdateBoltTiling(Vector3 end)
        {
            if (_material == null)
                return;

            float length = (end - _origin).magnitude;
            if (Mathf.Abs(length - _lastTiledLength) < RetileThresholdMeters)
                return;

            _lastTiledLength = length;
            float repeats = Mathf.Max(1f, length / BoltRepeatMeters);
            _material.mainTextureScale = new Vector2(repeats, 1f);
            // The glow deliberately does NOT tile: one continuous stretch of the
            // wisp texture hand→target, so no repeating segments are visible.
        }

        private void UpdateEndpointEmitters()
        {
            // Anchored muzzles follow the hand bone via parenting.
            if (_muzzleEmitter != null && _originAnchor == null)
                _muzzleEmitter.transform.position = _origin;
            if (_impactEmitter != null)
                _impactEmitter.transform.position = _currentEnd;
        }
    }
}
