#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation.Dice
{
    [CreateAssetMenu(menuName = "Arena/Dice/Dice Set Catalog")]
    public sealed class DiceSetCatalog : ScriptableObject
    {
        [SerializeField] private string setId = string.Empty;
        [SerializeField] private List<DiceDefinition> definitions = new();
        [SerializeField] private List<DiceMotionProfile> motionProfiles = new();
        [SerializeField] private Material? resinMaterial;
        [SerializeField] private Material? numeralMaterial;

        public string SetId => setId;
        public IReadOnlyList<DiceDefinition> Definitions => definitions;
        public IReadOnlyList<DiceMotionProfile> MotionProfiles => motionProfiles;
        public Material? ResinMaterial => resinMaterial;
        public Material? NumeralMaterial => numeralMaterial;

        public bool TryGetDefinition(string? dieId, out DiceDefinition definition)
        {
            for (int i = 0; i < definitions.Count; i++)
            {
                DiceDefinition candidate = definitions[i];
                if (candidate != null &&
                    string.Equals(candidate.DieId, dieId, System.StringComparison.OrdinalIgnoreCase))
                {
                    definition = candidate;
                    return true;
                }
            }

            definition = null!;
            return false;
        }

        public void SetAuthoringData(
            string stableSetId,
            IEnumerable<DiceDefinition> authoredDefinitions,
            IEnumerable<DiceMotionProfile> authoredMotionProfiles,
            Material bodyMaterial,
            Material faceMaterial)
        {
            setId = stableSetId ?? string.Empty;
            definitions = authoredDefinitions != null
                ? new List<DiceDefinition>(authoredDefinitions)
                : new List<DiceDefinition>();
            motionProfiles = authoredMotionProfiles != null
                ? new List<DiceMotionProfile>(authoredMotionProfiles)
                : new List<DiceMotionProfile>();
            resinMaterial = bodyMaterial;
            numeralMaterial = faceMaterial;
        }
    }
}
