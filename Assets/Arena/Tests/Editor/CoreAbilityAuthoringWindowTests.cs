#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CoreAbilityAuthoringWindowTests
    {
        private const string SampleCatalogJson = @"{
  ""combat_profiles"": [
    {
      ""combat_profile_id"": ""TWO_HANDED_SWORD"",
      ""display_name"": ""Greatsword"",
      ""sort_order"": 10
    }
  ],
  ""abilities"": [
    {
      ""ability_id"": ""WARRIOR_HEW"",
      ""combat_profile_id"": ""TWO_HANDED_SWORD"",
      ""action_id"": ""COMBO_ATTACK_1_1_HIGH_TO_LOW"",
      ""display_name"": ""Hew"",
      ""ability_tags"": [
        ""ACTION_BAR_ACTION""
      ],
      ""sort_order"": 10,
      ""gameplay"": {
        ""kind"": ""MELEE"",
        ""base_damage"": 30
      }
    },
    {
      ""ability_id"": ""WARRIOR_CRUSHING_BLOW"",
      ""combat_profile_id"": ""TWO_HANDED_SWORD"",
      ""action_id"": ""CRUSHING_BLOW"",
      ""display_name"": ""Crushing Blow"",
      ""ability_tags"": [
        ""ACTION_BAR_ACTION"",
        ""CORE_ABILITY""
      ],
      ""sort_order"": 20,
      ""gameplay"": {
        ""kind"": ""MELEE"",
        ""base_damage"": 35
      }
    }
  ],
  ""combat_profile_action_bar_defaults"": [
    {
      ""combat_profile_id"": ""TWO_HANDED_SWORD"",
      ""slot_id"": ""slot_0_0"",
      ""ability_id"": ""WARRIOR_HEW"",
      ""sort_order"": 10
    }
  ]
}";

        [Test]
        public void ApplyCoreAbilityTags_AddsCoreTagWithoutDroppingExistingTags()
        {
            string updated = ApplyCoreTags(
                SampleCatalogJson,
                new Dictionary<string, bool>
                {
                    ["WARRIOR_HEW"] = true,
                    ["WARRIOR_CRUSHING_BLOW"] = true,
                });

            Assert.That(updated, Does.Match(@"""ability_id""\s*:\s*""WARRIOR_HEW""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""ACTION_BAR_ACTION""[^\]]*""CORE_ABILITY"""));
            Assert.That(updated, Does.Contain(@"""base_damage"": 30"));
        }

        [Test]
        public void ApplyCoreAbilityTags_RemovesOnlyCoreTag()
        {
            string updated = ApplyCoreTags(
                SampleCatalogJson,
                new Dictionary<string, bool>
                {
                    ["WARRIOR_HEW"] = false,
                    ["WARRIOR_CRUSHING_BLOW"] = false,
                });

            Assert.That(updated, Does.Match(@"""ability_id""\s*:\s*""WARRIOR_CRUSHING_BLOW""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""ACTION_BAR_ACTION"""));
            Assert.That(updated, Does.Not.Match(@"""ability_id""\s*:\s*""WARRIOR_CRUSHING_BLOW""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""CORE_ABILITY"""));
        }

        [Test]
        public void ReadCatalog_ReportsCoreAndActionBarDefaultState()
        {
            object catalog = ReadCatalog(SampleCatalogJson);
            object abilities = catalog.GetType().GetField("Abilities")!.GetValue(catalog)!;
            object actionBarDefaultAssigned = catalog.GetType().GetField("ActionBarDefaultAbilityIds")!.GetValue(catalog)!;

            IEnumerable<object> abilityRows = ((System.Collections.IEnumerable)abilities).Cast<object>();
            object hew = abilityRows.First(row =>
                (string)row.GetType().GetField("AbilityId")!.GetValue(row)! == "WARRIOR_HEW");
            object crushingBlow = abilityRows.First(row =>
                (string)row.GetType().GetField("AbilityId")!.GetValue(row)! == "WARRIOR_CRUSHING_BLOW");

            Assert.That((bool)hew.GetType().GetProperty("IsCore")!.GetValue(hew)!, Is.False);
            Assert.That((bool)crushingBlow.GetType().GetProperty("IsCore")!.GetValue(crushingBlow)!, Is.True);
            Assert.That((bool)actionBarDefaultAssigned.GetType().GetMethod("Contains")!.Invoke(actionBarDefaultAssigned, new object[] { "WARRIOR_HEW" })!, Is.True);
            Assert.That((bool)actionBarDefaultAssigned.GetType().GetMethod("Contains")!.Invoke(actionBarDefaultAssigned, new object[] { "WARRIOR_CRUSHING_BLOW" })!, Is.False);
        }

        private static string ApplyCoreTags(string json, Dictionary<string, bool> coreByAbilityId)
        {
            Type type = EditorType();
            return (string)type.GetMethod("ApplyCoreAbilityTags", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { json, coreByAbilityId })!;
        }

        private static object ReadCatalog(string json)
        {
            Type type = EditorType();
            return type.GetMethod("ReadCatalog", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { json })!;
        }

        private static Type EditorType()
        {
            return AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.CoreAbilityCatalogJson", throwOnError: true)!;
        }
    }
}
