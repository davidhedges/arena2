#nullable enable

using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Moves the cavern envelope's far band with the camera at a fraction of
    /// its translation, so the band parallaxes slowly instead of being walked
    /// past.
    /// </summary>
    /// <remarks>
    /// This is the one piece of the envelope that cannot be an editor bake. A
    /// static far shell works for a hand-built level because an artist places
    /// it around one known footprint; a generated dungeon's footprint changes
    /// per seed, and a long enough run puts the player outside whatever shell
    /// was baked — the ceiling visibly ends and the enclosure is gone. Anchored
    /// motion makes the enclosure identical in a 40m dungeon and a 200m one.
    ///
    /// Not a skybox: a skybox has zero parallax and reads as a painted
    /// backdrop. The point is a small, non-zero relative shift.
    /// </remarks>
    public sealed class CavernFarBandFollower : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("World point the band is centred on when the camera sits on it.")]
        private Vector3 _anchor;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("Fraction of camera translation the band copies. Parallax is 1 minus this; 1 is a skybox.")]
        private float _follow = 0.85f;

        private Camera? _camera;

        /// <summary>Public because the envelope builder lives in the editor assembly.</summary>
        public void Configure(Vector3 anchor, float follow)
        {
            _anchor = anchor;
            _follow = Mathf.Clamp01(follow);
        }

        private void LateUpdate()
        {
            // Unity fake-null: an explicit comparison, never ?? or ?., so a
            // destroyed camera re-resolves instead of throwing.
            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera == null)
                    return;
            }

            Vector3 cameraPosition = _camera.transform.position;
            transform.position = _anchor + (cameraPosition - _anchor) * _follow;
        }
    }
}
