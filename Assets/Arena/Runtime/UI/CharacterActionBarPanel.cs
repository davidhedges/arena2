#nullable enable

using UnityEngine;

namespace Arena.UI
{
    /// <summary>
    /// Disabled compatibility component. Action-bar editing now belongs to the
    /// future atomic combat-build editor; gameplay bars are read-only views of
    /// the frozen match build.
    /// </summary>
    [DefaultExecutionOrder(66)]
    public sealed class CharacterActionBarPanel : MonoBehaviour, IEscapeCloseable
    {
        public int EscapeClosePriority => 89;
        public bool IsEscapeCloseable => false;

        private void Awake()
        {
            enabled = false;
        }

        public bool TryCloseForEscape() => false;
    }
}
