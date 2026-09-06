#nullable enable

using System.Collections.Generic;
using Arena.Editor;
using Arena.Input;
using Arena.Interaction;
using NUnit.Framework;
using SpacetimeDB.Types;
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
                    productionEnabled: true,
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
                    document["world_definition_key"]!.Value<string>(),
                    Is.EqualTo("RANDOM_DUNGEON"));
                Assert.That(
                    definition["door_definition_id"]!.Value<string>(),
                    Is.EqualTo("RANDOM_DUNGEON:GATEWAY:1:2:3"));
                Assert.That(definition["default_open"]!.Value<bool>(), Is.True);
                Assert.That(
                    definition["closed_blocker"]!["size"]!["z"]!.Value<float>(),
                    Is.EqualTo(0.35f).Within(0.0001f));
                Assert.That(
                    WorldInteractionManifestExporter.ClientDoorManifestPath(
                        "random_dungeon_staging"),
                    Is.EqualTo(
                        "Assets/Arena/Resources/SharedData/Worlds/" +
                        "random_dungeon_staging.doors.shared.json"));
                Assert.That(
                    WorldInteractionManifestExporter.ServerDoorManifestPath(
                        "random_dungeon_staging"),
                    Is.EqualTo(
                        "server/src/world_data/" +
                        "random_dungeon_staging.doors.shared.json"));
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

        [Test]
        public void DoorManifest_RejectsProductionDoorThatIsNotEnabled()
        {
            GameObject door = CreateDoor("Disabled");
            try
            {
                door.GetComponent<DoorAuthoring>().SetProductionEnabled(false);

                System.InvalidOperationException exception =
                    Assert.Throws<System.InvalidOperationException>(() =>
                        WorldInteractionManifestExporter.BuildDoorManifestJson(
                            "random_dungeon",
                            new[] { door.GetComponent<DoorAuthoring>() },
                            new HashSet<string> { "WORLD_DOOR_INSTANT" }))!;

                Assert.That(
                    exception.Message,
                    Does.Contain("is not interaction-enabled"));
            }
            finally
            {
                Object.DestroyImmediate(door);
            }
        }

        [Test]
        public void ReplicatedClosedDoor_BlocksPredictionAndLineQueries()
        {
            JObject manifest = JObject.Parse(Resources.Load<TextAsset>(
                "SharedData/Worlds/random_dungeon.doors.shared").text);
            var doors = (JArray)manifest["doors"]!;
            Assert.That(doors.Count, Is.GreaterThan(0));
            WorldDoorCollisionRuntime.Clear();
            WorldDoorCollisionRuntime.SetScope("OPEN", "RandomDungeon");
            try
            {
                foreach (JToken door in doors)
                {
                    string doorId = door["door_definition_id"]!.Value<string>()!;
                    JToken blocker = door["closed_blocker"]!;
                    Vector3 center = Vector(blocker["center"]!);
                    Vector3 size = Vector(blocker["size"]!);
                    Vector3 normal = Quaternion.Euler(0f, blocker["yaw_degrees"]!.Value<float>(), 0f) * Vector3.forward;
                    Vector3 start = center - normal * 2f;
                    Vector3 end = center + normal * 2f;
                    float footY = center.y - size.y * 0.5f + 0.1f;
                    Assert.That(
                        WorldDoorCollisionRuntime.TryGetEffectiveState(
                            doorId,
                            out bool defaultOpen,
                            out ulong defaultRevision),
                        Is.True);
                    Assert.That(defaultOpen, Is.True);
                    Assert.That(defaultRevision, Is.Zero);

                    var closed = new WorldDoorState(
                        $"OPEN:RandomDungeon:{doorId}",
                        doorId,
                        "OPEN",
                        null,
                        0UL,
                        "RandomDungeon",
                        false,
                        1,
                        new SpacetimeDB.Timestamp(0));
                    WorldDoorCollisionRuntime.Upsert(closed);

                    Vector2 blocked = WorldDoorCollisionRuntime.ResolveHorizontalCollision(
                        start.x,
                        start.z,
                        end.x,
                        end.z,
                        0.25f,
                        1.8f,
                        footY);
                    Assert.That(Vector2.Distance(blocked, new Vector2(start.x, start.z)), Is.LessThan(2f), doorId);
                    Assert.That(
                        WorldDoorCollisionRuntime.TryFindFirstLineHitDistance(
                            start,
                            end,
                            0.05f,
                            out float hitDistance),
                        Is.True);
                    Assert.That(hitDistance, Is.LessThan(2f));

                    closed.IsOpen = true;
                    closed.Revision = 2;
                    WorldDoorCollisionRuntime.Upsert(closed);
                    Vector2 open = WorldDoorCollisionRuntime.ResolveHorizontalCollision(
                        start.x,
                        start.z,
                        end.x,
                        end.z,
                        0.25f,
                        1.8f,
                        footY);
                    Assert.That(open, Is.EqualTo(new Vector2(end.x, end.z)), doorId);
                    Assert.That(WorldDoorCollisionRuntime.TryFindFirstLineHitDistance(
                        start, end, 0.05f, out _), Is.False, doorId);
                }
            }
            finally
            {
                WorldDoorCollisionRuntime.Clear();
            }

            static Vector3 Vector(JToken value) => new(
                value["x"]!.Value<float>(), value["y"]!.Value<float>(), value["z"]!.Value<float>());
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
                productionEnabled: true,
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
