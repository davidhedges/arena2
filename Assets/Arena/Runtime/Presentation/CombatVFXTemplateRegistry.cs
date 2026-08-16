#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Presentation.VFX;
using UnityEngine;

namespace Arena.Presentation
{
    public static class CombatVFXTemplateRegistry
    {
        private const string VfxNegate = "VFX_NEGATE_01";
        private const string VfxInstantBeam = "VFX_INSTANT_BEAM_01";
        private const string VfxElectrocuteBeam = "VFX_ELECTROCUTE_BEAM_01";
        private const string VfxMirrorImageActive = "VFX_MIRROR_IMAGE_ACTIVE_01";
        private const string VfxConfusionActive = "VFX_CONFUSION_ACTIVE_01";
        private const string VfxVerdantSpiritsActive = "VFX_VERDANT_SPIRITS_ACTIVE_01";
        private const string VfxPhotosynthesisActive = "VFX_PHOTOSYNTHESIS_ACTIVE_01";

        private static readonly string[] ScriptedTemplateIds =
        {
            VfxNegate,
            VfxInstantBeam,
            VfxElectrocuteBeam,
            VfxMirrorImageActive,
            VfxConfusionActive,
            VfxVerdantSpiritsActive,
            VfxPhotosynthesisActive,
        };

        public static IReadOnlyList<string> KnownScriptedTemplateIds => ScriptedTemplateIds;

        public static GameObject? ResolvePrefab(string vfxId)
        {
            return CombatVFXRegistry.ResolveSharedPrefab(vfxId);
        }

        public static CombatVFXRegistry.Template? ResolveTemplate(string vfxId)
        {
            return CombatVFXRegistry.ResolveSharedTemplate(vfxId);
        }

        public static bool IsScriptedTemplate(string vfxId)
        {
            string normalized = WireIdentifier.Normalize(vfxId);
            for (int index = 0; index < ScriptedTemplateIds.Length; index++)
            {
                if (string.Equals(normalized, ScriptedTemplateIds[index], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static bool CanResolveTemplate(string vfxId)
        {
            if (IsScriptedTemplate(vfxId))
                return true;

            return ResolvePrefab(vfxId) != null;
        }

        internal static bool TryCreateScripted(
            string vfxId,
            CombatVFXTemplateContext context,
            out ISpellVFX? visual)
        {
            visual = WireIdentifier.Normalize(vfxId) switch
            {
                VfxNegate => new NegateVFX(context),
                VfxInstantBeam => new BeamVFX(context, false, new Color(1f, 1f, 0.2f)),
                VfxElectrocuteBeam => new BeamVFX(context, true, new Color(0.9f, 0.95f, 0.2f)),
                VfxMirrorImageActive => new MirrorImageVFX(context),
                VfxConfusionActive => new ConfusionVFX(context),
                VfxVerdantSpiritsActive => new VerdantSpiritsVFX(context),
                VfxPhotosynthesisActive => new PhotosynthesisVFX(context),
                _ => null,
            };

            return visual != null;
        }
    }
}
