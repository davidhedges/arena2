#nullable enable
using TMPro;
using UnityEngine;

namespace Arena.Presentation.Dice
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TextMeshPro))]
    public sealed class DiceFaceLabel : MonoBehaviour
    {
        [SerializeField, Min(1)] private int value;
        [SerializeField] private Vector3 outwardNormal = Vector3.forward;
        [SerializeField] private Vector3 upright = Vector3.up;

        public int Value => value;
        public Vector3 OutwardNormal => outwardNormal;
        public Vector3 Upright => upright;
        public TextMeshPro Text => GetComponent<TextMeshPro>();

        public void SetAuthoringData(int faceValue, Vector3 faceNormal, Vector3 faceUpright)
        {
            value = faceValue;
            outwardNormal = faceNormal.normalized;
            upright = Vector3.ProjectOnPlane(faceUpright, outwardNormal).normalized;
        }
    }
}
