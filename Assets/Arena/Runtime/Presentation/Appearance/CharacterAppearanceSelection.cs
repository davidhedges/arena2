#nullable enable
using System;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [Serializable]
    public struct CharacterAppearanceSelection
    {
        public string raceId;
        public string sexId;
        public string bodyId;
        public string headId;
        public string faceId;
        public string hairId;
        public string eyesId;
        public string outfitId;

        public static CharacterAppearanceSelection DefaultHumanMale(string? outfitId = null)
        {
            return new CharacterAppearanceSelection
            {
                raceId = CharacterAppearanceIds.DefaultRaceId,
                sexId = CharacterAppearanceIds.DefaultSexId,
                bodyId = CharacterAppearanceIds.DefaultBodyId,
                headId = CharacterAppearanceIds.DefaultHeadId,
                faceId = CharacterAppearanceIds.DefaultFaceId,
                hairId = CharacterAppearanceIds.DefaultHairId,
                eyesId = CharacterAppearanceIds.DefaultEyesId,
                outfitId = string.IsNullOrWhiteSpace(outfitId)
                    ? CharacterAppearanceIds.DefaultOutfitId
                    : CharacterAppearanceIds.Normalize(outfitId),
            };
        }

        public void NormalizeInPlace()
        {
            raceId = CharacterAppearanceIds.Normalize(raceId);
            sexId = CharacterAppearanceIds.Normalize(sexId);
            bodyId = CharacterAppearanceIds.Normalize(bodyId);
            headId = CharacterAppearanceIds.Normalize(headId);
            faceId = CharacterAppearanceIds.Normalize(faceId);
            hairId = CharacterAppearanceIds.Normalize(hairId);
            eyesId = CharacterAppearanceIds.Normalize(eyesId);
            outfitId = CharacterAppearanceIds.Normalize(outfitId);
        }
    }
}
