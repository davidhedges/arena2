#nullable enable
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Expanding ring burst on impact. Port of impact_burst_vfx.ts.
    /// Duration 0.35s, scales 1x to 5x, fades out.
    /// </summary>
    public class ImpactBurstVFX
    {
        private const float Duration = 0.35f;
        private const float StartScale = 1f;
        private const float EndScale = 5f;

        private readonly GameObject _go;
        private readonly Material _material;
        private float _age;

        public ImpactBurstVFX(Vector3 position, Color color)
        {
            _go = new GameObject("ImpactBurst");
            var mf = _go.AddComponent<MeshFilter>();
            var mr = _go.AddComponent<MeshRenderer>();

            mf.mesh = VFXUtils.CreateRingMesh(0.25f, 1.1f);

            _material = new Material(VFXUtils.GetAdditiveGlowShader());
            _material.SetColor("_Color", color);
            _material.SetFloat("_Intensity", 2f);
            mr.material = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _go.transform.position = position + Vector3.up * 0.05f;
            _go.transform.localScale = Vector3.one * StartScale;
        }

        /// <returns>false when the burst is done</returns>
        public bool Tick(float dt)
        {
            _age += dt;
            float t = Mathf.Clamp01(_age / Duration);
            float fade = 1f - t;

            float scale = Mathf.Lerp(StartScale, EndScale, t);
            _go.transform.localScale = Vector3.one * scale;

            var c = _material.GetColor("_Color");
            _material.SetFloat("_Intensity", 2f * fade);

            return t < 1f;
        }

        public void Dispose()
        {
            if (_go != null) Object.Destroy(_go);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
