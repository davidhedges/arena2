#nullable enable

using System.Collections.Generic;
using System.IO;
using Arena.Editor;
using Arena.Interaction;
using NUnit.Framework;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace Arena.EditModeTests
{
    public sealed class WorldTrapManifestTests
    {
        private const string ClientTrapProfileManifestPath =
            "Assets/Arena/Resources/SharedData/WorldInteractions/world_trap_profiles.shared.json";
        private const string ServerTrapProfileManifestPath =
            "server/src/world_data/world_trap_profiles.shared.json";
        private const string ProfileAssetRoot = "Assets/Arena/Content/Settings/Traps";

        [Test]
        public void ProfileManifest_IsStableAndSorted()
        {
            TrapProfile spikes = CreateSpikeProfile();
            TrapProfile saw = CreateSweepProfile();
            try
            {
                string first = WorldTrapManifestExporter.BuildProfileManifestJson(
                    new[] { spikes, saw });
                string second = WorldTrapManifestExporter.BuildProfileManifestJson(
                    new[] { saw, spikes });

                Assert.That(second, Is.EqualTo(first));
                var root = JObject.Parse(first);
                Assert.That(root["profiles"]![0]!["profile_id"]!.Value<string>(),
                    Is.EqualTo("TRAP_SAW_SWEEP"));
                Assert.That(root["profiles"]![1]!["profile_id"]!.Value<string>(),
                    Is.EqualTo("TRAP_SPIKES"));
            }
            finally
            {
                Object.DestroyImmediate(spikes);
                Object.DestroyImmediate(saw);
            }
        }

        [Test]
        public void ProfileManifest_CarriesTheHazardTrackAndEveryOnHitEntry()
        {
            TrapProfile saw = CreateSweepProfile();
            try
            {
                var root = JObject.Parse(
                    WorldTrapManifestExporter.BuildProfileManifestJson(new[] { saw }));
                JToken profile = root["profiles"]![0]!;

                Assert.That(profile["trigger_kind"]!.Value<string>(), Is.EqualTo("PROXIMITY"));
                Assert.That(profile["hazard_track"]!.Value<JArray>()!.Count, Is.EqualTo(2));
                Assert.That(profile["hazard_track"]![1]!["offset"]!["z"]!.Value<float>(),
                    Is.EqualTo(-2f));

                var onHit = profile["on_hit"]!.Value<JArray>()!;
                Assert.That(onHit.Count, Is.EqualTo(2));
                Assert.That(onHit[0]!["effect"]!.Value<string>(), Is.EqualTo("DAMAGE"));
                Assert.That(onHit[1]!["effect"]!.Value<string>(), Is.EqualTo("DOT"));
                Assert.That(onHit[1]!["stack_group"]!.Value<string>(), Is.EqualTo("TRAP_BLEED"));
                Assert.That(onHit[1]!["max_stacks"]!.Value<int>(), Is.EqualTo(10));
                Assert.That(onHit[1]!["stack_policy"]!.Value<string>(),
                    Is.EqualTo("ADD_STACK_ESCALATING_DECAY"));
                Assert.That(onHit[1]!["dispel_types"]!.Values<string>(),
                    Is.EqualTo(new[] { "BLEED" }));
            }
            finally
            {
                Object.DestroyImmediate(saw);
            }
        }

        [Test]
        public void TrapManifest_ExportsPlacementAndRefusesAnUnknownProfile()
        {
            var trapObject = new GameObject("trap_probe");
            TrapProfile spikes = CreateSpikeProfile();
            try
            {
                trapObject.transform.position = new Vector3(-36f, 9f, 26f);
                trapObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                TrapAuthoring authoring = trapObject.AddComponent<TrapAuthoring>();
                trapObject.AddComponent<TrapPresenter>();
                trapObject.AddComponent<Animator>();
                authoring.Configure(
                    "RANDOM_DUNGEON:TRAP:SPIKES:18:31:9",
                    "RANDOM_DUNGEON",
                    templateOnly: false,
                    productionEnabled: true,
                    definitionVersion: 1,
                    footprintCells: 1,
                    profile: spikes);

                var known = new HashSet<string> { "TRAP_SPIKES" };
                var root = JObject.Parse(WorldTrapManifestExporter.BuildTrapManifestJson(
                    "random_dungeon",
                    new[] { authoring },
                    known));
                JToken trap = root["traps"]![0]!;
                Assert.That(root["world_definition_key"]!.Value<string>(),
                    Is.EqualTo("RANDOM_DUNGEON"));
                Assert.That(trap["trap_definition_id"]!.Value<string>(),
                    Is.EqualTo("RANDOM_DUNGEON:TRAP:SPIKES:18:31:9"));
                Assert.That(trap["trap_profile_id"]!.Value<string>(), Is.EqualTo("TRAP_SPIKES"));
                Assert.That(trap["origin"]!["z"]!.Value<float>(), Is.EqualTo(26f));
                Assert.That(trap["yaw_degrees"]!.Value<float>(), Is.EqualTo(90f));
                Assert.That(
                    WorldTrapManifestExporter.ClientTrapManifestPath(
                        "random_dungeon_staging"),
                    Is.EqualTo(
                        "Assets/Arena/Resources/SharedData/Worlds/" +
                        "random_dungeon_staging.traps.shared.json"));
                Assert.That(
                    WorldTrapManifestExporter.ServerTrapManifestPath(
                        "random_dungeon_staging"),
                    Is.EqualTo(
                        "server/src/world_data/" +
                        "random_dungeon_staging.traps.shared.json"));

                Assert.Throws<System.InvalidOperationException>(() =>
                    WorldTrapManifestExporter.BuildTrapManifestJson(
                        "random_dungeon",
                        new[] { authoring },
                        new HashSet<string>()));
            }
            finally
            {
                Object.DestroyImmediate(trapObject);
                Object.DestroyImmediate(spikes);
            }
        }

        [Test]
        public void TrapManifest_RefusesATrapThatWouldContributeCollision()
        {
            var trapObject = new GameObject("trap_probe_collider");
            TrapProfile spikes = CreateSpikeProfile();
            try
            {
                TrapAuthoring authoring = trapObject.AddComponent<TrapAuthoring>();
                trapObject.AddComponent<TrapPresenter>();
                trapObject.AddComponent<Animator>();
                trapObject.AddComponent<BoxCollider>();
                authoring.Configure(
                    "RANDOM_DUNGEON:TRAP:SPIKES:1:1:0",
                    "RANDOM_DUNGEON",
                    templateOnly: false,
                    productionEnabled: true,
                    definitionVersion: 1,
                    footprintCells: 1,
                    profile: spikes);

                Assert.Throws<System.InvalidOperationException>(() =>
                    WorldTrapManifestExporter.BuildTrapManifestJson(
                        "random_dungeon",
                        new[] { authoring },
                        new HashSet<string> { "TRAP_SPIKES" }));
            }
            finally
            {
                Object.DestroyImmediate(trapObject);
                Object.DestroyImmediate(spikes);
            }
        }

        /// <summary>
        /// The checked-in pair is what the server compiles into the module, so a
        /// drift between the authored assets and the exported bytes is a real
        /// break, not a formatting nit.
        /// </summary>
        [Test]
        public void CheckedInProfileManifest_MatchesTheAuthoredAssetsAndItsServerCopy()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string client = File.ReadAllText(Path.Combine(projectRoot, ClientTrapProfileManifestPath));
            string server = File.ReadAllText(Path.Combine(projectRoot, ServerTrapProfileManifestPath));
            Assert.That(server, Is.EqualTo(client), "paired trap profile exports must be byte-identical");

            var profiles = new List<TrapProfile>();
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets(
                         "t:TrapProfile", new[] { ProfileAssetRoot }))
            {
                TrapProfile? asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TrapProfile>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                    profiles.Add(asset);
            }

            Assert.That(profiles.Count, Is.EqualTo(4), "four vendor trap kinds are authored");
            Assert.That(
                WorldTrapManifestExporter.BuildProfileManifestJson(profiles),
                Is.EqualTo(client));
        }

        /// <summary>
        /// Regression: the vendor clips are authored with `m_LoopTime: 1`, so a
        /// looping state samples normalized time 1.0 as 0.0 — the strike frame.
        /// Scrubbing the tail of a cycle to exactly 1 therefore replayed the
        /// spark burst just as the trap finished retracting.
        /// </summary>
        [Test]
        public void ClipScrub_NeverReachesTheLoopPointThatWrapsOntoTheStrikeFrame()
        {
            const float cycleSeconds = 4.667f;
            Assert.That(TrapPresenter.NormalizedClipTime(0f, cycleSeconds), Is.EqualTo(0f));
            Assert.That(TrapPresenter.NormalizedClipTime(cycleSeconds * 0.5f, cycleSeconds),
                Is.EqualTo(0.5f).Within(0.001f));

            foreach (float overrun in new[] { 0f, 0.001f, 0.5f, 30f })
            {
                float normalized = TrapPresenter.NormalizedClipTime(
                    cycleSeconds + overrun, cycleSeconds);
                Assert.That(normalized, Is.LessThan(1f),
                    $"scrubbing {overrun:0.###}s past the cycle wrapped onto the strike frame");
                Assert.That(normalized, Is.GreaterThan(0.99f));
            }

            // Degenerate inputs park at the start rather than divide by zero.
            Assert.That(TrapPresenter.NormalizedClipTime(1f, 0f), Is.EqualTo(0f));
            Assert.That(TrapPresenter.NormalizedClipTime(float.NaN, cycleSeconds), Is.EqualTo(0f));
        }

        private static TrapProfile CreateSpikeProfile()
        {
            TrapProfile profile = ScriptableObject.CreateInstance<TrapProfile>();
            profile.Configure(
                "TRAP_SPIKES",
                TrapTriggerKind.Proximity,
                triggerDelayMs: 350,
                cycleMs: 4667,
                hazardStartMs: 180,
                hazardEndMs: 230,
                rearmMs: 0,
                triggerVolume: new TrapVolume(new Vector3(0f, 1f, 0f), new Vector3(4f, 2f, 4f)),
                hazardVolume: new TrapVolume(new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 4f)),
                hazardTrack: System.Array.Empty<TrapHazardTrackKey>(),
                onHit: new[]
                {
                    new TrapOnHitEffect
                    {
                        effect = TrapOnHitEffectKind.Damage,
                        amount = 45,
                        damageType = "PHYSICAL",
                    },
                },
                oneHitPerActivation: true,
                animatorStateName: "TFD_Floor_Trap_01A");
            return profile;
        }

        private static TrapProfile CreateSweepProfile()
        {
            TrapProfile profile = ScriptableObject.CreateInstance<TrapProfile>();
            profile.Configure(
                "TRAP_SAW_SWEEP",
                TrapTriggerKind.Proximity,
                triggerDelayMs: 0,
                cycleMs: 3833,
                hazardStartMs: 220,
                hazardEndMs: 2250,
                rearmMs: 0,
                triggerVolume: new TrapVolume(new Vector3(0f, 1f, 0f), new Vector3(4f, 2f, 8f)),
                hazardVolume: new TrapVolume(new Vector3(0f, 0.7f, 0f), new Vector3(0.9f, 1.4f, 2f)),
                hazardTrack: new[]
                {
                    new TrapHazardTrackKey(250, new Vector3(0f, 0f, 2f)),
                    new TrapHazardTrackKey(2133, new Vector3(0f, 0f, -2f)),
                },
                onHit: new[]
                {
                    new TrapOnHitEffect
                    {
                        effect = TrapOnHitEffectKind.Damage,
                        amount = 22,
                        damageType = "PHYSICAL",
                    },
                    new TrapOnHitEffect
                    {
                        effect = TrapOnHitEffectKind.Dot,
                        damageType = "PHYSICAL",
                        tickAmount = 4,
                        tickIntervalMs = 1000,
                        durationMs = 6000,
                        stackGroup = "TRAP_BLEED",
                        maxStacks = 10,
                        stackPolicy = "ADD_STACK_ESCALATING_DECAY",
                        dispelTypes = new[] { "BLEED" },
                    },
                },
                oneHitPerActivation: true,
                animatorStateName: "TFD_Trap_01A");
            return profile;
        }
    }
}
