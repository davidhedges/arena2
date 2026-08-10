#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Renders a world-space username label above a player capsule.
    /// Attached to a child GameObject of the player; billboards toward Camera.main.
    /// </summary>
    [RequireComponent(typeof(TextMesh))]
    public class NameTag : MonoBehaviour
    {
        private const float HeightOffset = 2.24f;
        private const bool NameTagsVisible = false;

        private TextMesh _textMesh = null!;

        private void Awake()
        {
            _textMesh = GetComponent<TextMesh>();
        }

        public void SetName(string username)
        {
            _textMesh.text = string.IsNullOrEmpty(username) ? "..." : username;
        }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Face the camera (billboard).
            transform.LookAt(cam.transform.position);
            transform.Rotate(0f, 180f, 0f);
        }

        /// <summary>
        /// Creates and attaches a NameTag to the given player GameObject.
        /// </summary>
        public static NameTag Create(Transform parent, bool isLocalPlayer)
        {
            var labelGo = new GameObject("NameTag");
            labelGo.transform.SetParent(parent, false);
            labelGo.transform.localPosition = new Vector3(0f, HeightOffset, 0f);

            var tm = labelGo.AddComponent<TextMesh>();
            tm.fontSize       = 24;
            tm.characterSize  = 0.06f;
            tm.alignment      = TextAlignment.Center;
            tm.anchor         = TextAnchor.LowerCenter;
            tm.color          = isLocalPlayer ? new Color(0.5f, 0.8f, 1f) : Color.white;
            tm.text           = "...";

            var comp = labelGo.AddComponent<NameTag>();
            // Awake is deferred while the parent hierarchy is inactive. NPC
            // visuals are configured in that state, so initialize the required
            // reference explicitly before callers can set the name.
            comp._textMesh = tm;

            // Keep name tags wired for future use, but hide world-space labels while the HUD
            // unit frames carry identity and health presentation.
            labelGo.SetActive(NameTagsVisible);

            return comp;
        }
    }
}
