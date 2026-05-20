#nullable enable
using UnityEngine;
using SpacetimeDB.Types;
using Arena.Entity;
using Arena.Presentation;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Port of instant_beam_vfx.ts and electrocute_vfx.ts.
    /// Uses LineRenderer for beam rendering from caster to target.
    /// InstantBeam: smooth beam. Electrocute: jagged beam with animated offset.
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

        private readonly GameObject _go;
        private LineRenderer _line = null!;
        private Material _material = null!;
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

            CreateLineRenderer();
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

            CreateLineRenderer();
            UpdateBeamPositions();
        }

        private void CreateLineRenderer()
        {
            _line = _go.AddComponent<LineRenderer>();
            _material = new Material(VFXUtils.GetBeamShader());
            _material.SetColor("_Color", _color);
            _material.SetFloat("_Intensity", 2f);
            _material.SetFloat("_CoreWidth", 0.3f);
            _material.SetFloat("_GlowFalloff", 3f);
            _material.SetFloat("_ScrollSpeed", 4f);

            _line.material = _material;
            _line.startWidth = 0.2f;
            _line.endWidth = 0.15f;
            _line.positionCount = BeamSegments;
            _line.useWorldSpace = true;
            _line.numCapVertices = 3;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
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
            if (_elapsed > fadeStart)
            {
                float fadeT = (_elapsed - fadeStart) / (_duration - fadeStart);
                float alpha = 1f - fadeT;
                Color c = _color;
                c.a = alpha;
                _material.SetColor("_Color", c);
                _line.startWidth = 0.2f * alpha;
                _line.endWidth = 0.15f * alpha;
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
            if (_go != null) Object.Destroy(_go);
            if (_material != null) Object.Destroy(_material);
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
    }
}
