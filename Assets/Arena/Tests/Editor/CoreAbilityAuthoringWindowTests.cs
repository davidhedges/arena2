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
  ""classes"": [
    {
      ""class_id"": ""WARRIOR"",
      ""display_name"": ""Warrior"",
      ""sort_order"": 10
    }
  ],
  ""abilities"": [
    {
      ""ability_id"": ""WARRIOR_HEW"",
      ""class_id"": ""WARRIOR"",
      ""action_id"": ""COMBO_ATTACK_1_1_HIGH_TO_LOW"",
      ""display_name"": ""Hew"",
      ""ability_tags"": [
        ""LOADOUT_ACTION""
      ],
      ""sort_order"": 10,
      ""gameplay"": {
        ""kind"": ""MELEE"",
        ""base_damage"": 30
      }
    },
    {
      ""ability_id"": ""WARRIOR_SKYFALL_1"",
      ""class_id"": ""WARRIOR"",
      ""action_id"": ""SKYFALL_1"",
      ""display_name"": ""Skyfall I"",
      ""ability_tags"": [
        ""LOADOUT_ACTION"",
        ""CORE_ABILITY""
      ],
      ""sort_order"": 20,
      ""gameplay"": {
        ""kind"": ""MELEE"",
        ""base_damage"": 35
      }
    }
  ],
  ""default_loadout_assignments"": [
    {
      ""class_id"": ""WARRIOR"",
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
                    ["WARRIOR_SKYFALL_1"] = true,
                });

            Assert.That(updated, Does.Match(@"""ability_id""\s*:\s*""WARRIOR_HEW""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""LOADOUT_ACTION""[^\]]*""CORE_ABILITY"""));
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
                    ["WARRIOR_SKYFALL_1"] = false,
                });

            Assert.That(updated, Does.Match(@"""ability_id""\s*:\s*""WARRIOR_SKYFALL_1""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""LOADOUT_ACTION"""));
            Assert.That(updated, Does.Not.Match(@"""ability_id""\s*:\s*""WARRIOR_SKYFALL_1""[\s\S]*?""ability_tags""\s*:\s*\[[^\]]*""CORE_ABILITY"""));
        }

        [Test]
        public void ReadCatalog_ReportsCoreAndDefaultAssignmentState()
        {
            object catalog = ReadCatalog(SampleCatalogJson);
            object abilities = catalog.GetType().GetField("Abilities")!.GetValue(catalog)!;
            object defaultAssigned = catalog.GetType().GetField("DefaultAssignedAbilityIds")!.GetValue(catalog)!;

            IEnumerable<object> abilityRows = ((System.Collections.IEnumerable)abilities).Cast<object>();
            object hew = abilityRows.First(row =>
                (string)row.GetType().GetField("AbilityId")!.GetValue(row)! == "WARRIOR_HEW");
            object skyfall = abilityRows.First(row =>
                (string)row.GetType().GetField("AbilityId")!.GetValue(row)! == "WARRIOR_SKYFALL_1");

            Assert.That((bool)hew.GetType().GetProperty("IsCore")!.GetValue(hew)!, Is.False);
            Assert.That((bool)skyfall.GetType().GetProperty("IsCore")!.GetValue(skyfall)!, Is.True);
            Assert.That((bool)defaultAssigned.GetType().GetMethod("Contains")!.Invoke(defaultAssigned, new object[] { "WARRIOR_HEW" })!, Is.True);
            Assert.That((bool)defaultAssigned.GetType().GetMethod("Contains")!.Invoke(defaultAssigned, new object[] { "WARRIOR_SKYFALL_1" })!, Is.False);
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
