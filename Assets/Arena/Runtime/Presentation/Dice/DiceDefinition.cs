#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation.Dice
{
    [Serializable]
    public sealed class DiceFace
    {
        [SerializeField, Min(1)] private int value;
        [SerializeField] private Vector3 outwardNormal = Vector3.forward;
        [SerializeField] private Vector3 upright = Vector3.up;

        public int Value => value;
        public Vector3 OutwardNormal => outwardNormal;
        public Vector3 Upright => upright;

        public DiceFace(int value, Vector3 outwardNormal, Vector3 upright)
        {
            this.value = value;
            this.outwardNormal = outwardNormal.normalized;
            this.upright = Vector3.ProjectOnPlane(upright, this.outwardNormal).normalized;
        }
    }

    [CreateAssetMenu(menuName = "Arena/Dice/Dice Definition")]
    public sealed class DiceDefinition : ScriptableObject
    {
        [SerializeField] private string dieId = string.Empty;
        [SerializeField, Min(2)] private int sides = 2;
        [SerializeField] private GameObject? visualPrefab;
        [SerializeField, Min(0.01f)] private float presentationScale = 1f;
        [SerializeField] private List<DiceFace> faces = new();

        public string DieId => dieId;
        public int Sides => sides;
        public GameObject? VisualPrefab => visualPrefab;
        public float PresentationScale => presentationScale;
        public IReadOnlyList<DiceFace> Faces => faces;

        public bool TryGetFace(int value, out DiceFace face)
        {
            for (int i = 0; i < faces.Count; i++)
            {
                DiceFace candidate = faces[i];
                if (candidate != null && candidate.Value == value)
                {
                    face = candidate;
                    return true;
                }
            }

            face = null!;
            return false;
        }

        public void SetAuthoringData(
            string stableDieId,
            int sideCount,
            GameObject prefab,
            float scale,
            IEnumerable<DiceFace> authoredFaces)
        {
            dieId = stableDieId ?? string.Empty;
            sides = sideCount;
            visualPrefab = prefab;
            presentationScale = scale;
            faces = authoredFaces != null ? new List<DiceFace>(authoredFaces) : new List<DiceFace>();
        }
    }
}
