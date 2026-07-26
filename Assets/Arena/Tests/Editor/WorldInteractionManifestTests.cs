#nullable enable

using System.Collections.Generic;
using Arena.Editor;
using Arena.Interaction;
using NUnit.Framework;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace Arena.EditModeTests
{
    public sealed class WorldInteractionManifestTests
    {
        [Test]
        public void ProfileManifest_IsStableAndSorted()
        {
            WorldInteractionProfile instant = ScriptableObject.CreateInstance<WorldInteractionProfile>();
            WorldInteractionProfile timed = ScriptableObject.CreateInstance<WorldInteractionProfile>();
            try
            {
                instant.Configure(
                    "WORLD_DOOR_INSTANT",
                    "USE_DOOR",
                    0,
                    string.Empty,
                    requiresGrounded: false,
                    requiresStationary: false,
                    (WorldInteractionCancelCondition)255);
                timed.Configure(
                    "TIMED_HUMANOID_USE",
                    "USING",
                    1500,
                    "HUMANOID_USE",
                    requiresGrounded: true,
                    requiresStationary: true,
                    (WorldInteractionCancelCondition)255);

                string first = WorldInteractionManifestExporter.BuildProfileManifestJson(
                    new[] { instant, timed });
                string second = WorldInteractionManifestExporter.BuildProfileManifestJson(
                    new[] { timed, instant });

                Assert.That(second, Is.EqualTo(first));
                var root = JObject.Parse(first);
                Assert.That(root["profiles"]![0]!["profile_id"]!.Value<string>(),
                    Is.EqualTo("TIMED_HUMANOID_USE"));
                Assert.That(root["profiles"]![1]!["profile_id"]!.Value<string>(),
                    Is.EqualTo("WORLD_DOOR_INSTANT"));
            }
            finally
            {
                Object.DestroyImmediate(instant);
                Object.DestroyImmediate(timed);
            }
        }

        [Test]
        public void DoorManifest_ExportsAuthorityGeometryAndStableIdentity()
        {
            var rootObject = new GameObject("Door");
            var leafObject = new GameObject("Leaf");
            try
            {
                leafObject.transform.SetParent(rootObject.transform, false);
                DoorAuthoring authoring = rootObject.AddComponent<DoorAuthoring>();
                authoring.Configure(
                    "RANDOM_DUNGEON:GATEWAY:1:2:3",
                    "RANDOM_DUNGEON",
                    templateOnly: false,
                    productionEnabled: false,
                    defaultOpen: true,
                    definitionVersion: 1,
                    openInteractionProfileId: "WORLD_DOOR_INSTANT",
                    closeInteractionProfileId: "WORLD_DOOR_INSTANT",
                    interactionAnchorLocal: new Vector3(0f, 1.25f, 0f),
                    maxInteractionDistance: 3.25f,
                    closedBlockerCenterLocal: new Vector3(0f, 1.5f, 0f),
                    closedBlockerSize: new Vector3(3f, 3f, 0.35f),
                    closedBlockerLocalYaw: 90f,
                    new[]
                    {
                        new DoorAuthoring.LeafPose(
                            leafObject.transform,
                            Quaternion.identity,
                            Quaternion.Euler(0f, 95f, 0f)),
                    });

                string json = WorldInteractionManifestExporter.BuildDoorManifestJson(
                    "random_dungeon",
                    new[] { authoring },
                    new HashSet<string> { "WORLD_DOOR_INSTANT" });
                var document = JObject.Parse(json);
                JToken definition = document["doors"]![0]!;

                Assert.That(
                    definition["door_definition_id"]!.Value<string>(),
                    Is.EqualTo("RANDOM_DUNGEON:GATEWAY:1:2:3"));
                Assert.That(definition["default_open"]!.Value<bool>(), Is.True);
                Assert.That(
                    definition["closed_blocker"]!["size"]!["z"]!.Value<float>(),
                    Is.EqualTo(0.35f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void DoorManifest_RejectsDuplicateDefinitionIds()
        {
            GameObject first = CreateDoor("First");
            GameObject second = CreateDoor("Second");
            try
            {
                Assert.Throws<System.InvalidOperationException>(() =>
                    WorldInteractionManifestExporter.BuildDoorManifestJson(
                        "random_dungeon",
                        new[]
                        {
                            first.GetComponent<DoorAuthoring>(),
                            second.GetComponent<DoorAuthoring>(),
                        },
                        new HashSet<string> { "WORLD_DOOR_INSTANT" }));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private static GameObject CreateDoor(string name)
        {
            var root = new GameObject(name);
            var leaf = new GameObject("Leaf");
            leaf.transform.SetParent(root.transform, false);
            DoorAuthoring authoring = root.AddComponent<DoorAuthoring>();
            authoring.Configure(
                "RANDOM_DUNGEON:GATEWAY:DUPLICATE",
                "RANDOM_DUNGEON",
                templateOnly: false,
                productionEnabled: false,
                defaultOpen: true,
                definitionVersion: 1,
                openInteractionProfileId: "WORLD_DOOR_INSTANT",
                closeInteractionProfileId: "WORLD_DOOR_INSTANT",
                Vector3.zero,
                3f,
                Vector3.zero,
                Vector3.one,
                0f,
                new[]
                {
                    new DoorAuthoring.LeafPose(
                        leaf.transform,
                        Quaternion.identity,
                        Quaternion.identity),
                });
            return root;
        }
    }
}
