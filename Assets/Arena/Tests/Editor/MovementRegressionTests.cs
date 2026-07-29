#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class MovementRegressionTests
    {
        private const float PositionTolerance = 0.0001f;
        private const float PlayerRadius = 0.28f;
        private const float PlayerHeight = 1.8f;
        private static readonly string ProjectRootPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void BundledSharedMovementAssets_ArePresent()
        {
            TextAsset arenaLayout = Resources.Load<TextAsset>("SharedData/arena_layout.shared");
            TextAsset gameplayCollision = Resources.Load<TextAsset>("SharedData/gameplay_collision.shared");

            Assert.That(arenaLayout, Is.Not.Null);
            Assert.That(gameplayCollision, Is.Not.Null);
            Assert.That(arenaLayout!.text, Does.Contain("\"arena_radius\""));
            Assert.That(gameplayCollision!.text, Does.Contain("\"boxes\""));
        }

        [Test]
        public void SharedOpenWorldGameplayCollision_DoesNotContainBackgroundBoxes()
        {
            string[] roots =
            {
                ProjectPath("server/src/world_data"),
                ProjectPath("Assets/Arena/Resources/SharedData/Worlds"),
            };

            foreach (string root in roots)
            {
                foreach (string filePath in Directory.GetFiles(root, "*.collision.shared.json"))
                {
                    string json = File.ReadAllText(filePath);
                    Assert.That(
                        json,
                        Does.Not.Match("\"name\"\\s*:\\s*\"Background/"),
                        Path.GetRelativePath(ProjectRootPath, filePath));
                }
            }
        }

        [Test]
        public void ArenaEnvironment_SamplesBundledPlatformHeight()
        {
            object environment = InvokeStaticMethod(
                "Arena.Input.ArenaMovementEnvironment",
                "Shared",
                12345UL);

            float ground = (float)InvokeInstanceMethod(
                environment,
                "SampleGroundHeight",
                -26.0f,
                18.0f,
                10.0f);

            Assert.That(ground, Is.EqualTo(4.2f).Within(PositionTolerance));
        }

        [Test]
        public void OpenWorldEnvironment_ResolvesBundledGameplayCollisionSample()
        {
            object environment = GetStaticProperty(
                "Arena.Input.OpenWorldMovementEnvironment",
                "Shared");

            Vector2 resolved = (Vector2)InvokeInstanceMethod(
                environment,
                "ResolveHorizontalCollision",
                -10.2310009f,
                10.9860001f,
                PlayerRadius,
                PlayerHeight,
                0.0f);

            Assert.That(resolved.x, Is.EqualTo(-10.2310009f).Within(PositionTolerance));
            Assert.That(resolved.y, Is.EqualTo(10.9860001f).Within(PositionTolerance));
        }

        [Test]
        public void OpenWorldEnvironment_SamplesBundledHeightfieldGround()
        {
            object environment = GetStaticProperty(
                "Arena.Input.OpenWorldMovementEnvironment",
                "Shared");

            float ground = (float)InvokeInstanceMethod(
                environment,
                "SampleGroundHeight",
                0.0f,
                0.0f,
                100.0f);

            Assert.That(ground, Is.EqualTo(16.493927f).Within(PositionTolerance));
        }

        [Test]
        public void RandomDungeonEnvironment_UsesBakedLowerFloorWithoutFlatGroundPlane()
        {
            object environment = InvokeStaticMethod(
                "Arena.Input.OpenWorldMovementEnvironment",
                "SharedForScene",
                "RandomDungeon");
            MethodInfo trySample = RequireMethod(
                environment.GetType(),
                "TrySampleGroundHeight",
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(float).MakeByRefType());

            object[] lowerFloorArgs = { 7.3333335f, -3.3333333f, -8.0f, 0.0f };
            bool foundLowerFloor = (bool)trySample.Invoke(environment, lowerFloorArgs)!;
            Assert.That(foundLowerFloor, Is.True);
            Assert.That((float)lowerFloorArgs[3], Is.EqualTo(-8.0f).Within(0.01f));

            object[] emptySpaceArgs = { 10_000.0f, 10_000.0f, 0.0f, 0.0f };
            bool foundSyntheticPlane = (bool)trySample.Invoke(environment, emptySpaceArgs)!;
            Assert.That(foundSyntheticPlane, Is.False);
        }

        private static string ProjectPath(string relativePath)
            => Path.Combine(ProjectRootPath, relativePath);

        [Test]
        public void LocalMovementWorldContext_DefaultsToOpenWorldEnvironment()
        {
            object context = CreateInstance("Arena.Input.LocalMovementWorldContext");
            Type interfaceType = RequireType("Arena.Input.IMovementEnvironment");
            object[] args = { null! };

            bool found = (bool)RequireMethod(context.GetType(), "TryGetPredictionEnvironment", interfaceType.MakeByRefType())
                .Invoke(context, args)!;

            Assert.That(found, Is.True);
            Assert.That(args[0], Is.Not.Null);
            Assert.That(args[0]!.GetType().FullName, Is.EqualTo("Arena.Input.OpenWorldMovementEnvironment"));
        }

        [Test]
        public void LocalMovementWorldContext_UsesArenaEnvironmentWhenInstanceSeedKnown()
        {
            object context = CreateInstance("Arena.Input.LocalMovementWorldContext");
            Type interfaceType = RequireType("Arena.Input.IMovementEnvironment");
            object[] args = { null! };

            InvokeInstanceMethod(context, "SetWorld", "INSTANCE", 42UL, 987654321UL);
            bool found = (bool)RequireMethod(context.GetType(), "TryGetPredictionEnvironment", interfaceType.MakeByRefType())
                .Invoke(context, args)!;

            Assert.That(found, Is.True);
            Assert.That(args[0], Is.Not.Null);
            Assert.That(args[0]!.GetType().FullName, Is.EqualTo("Arena.Input.ArenaMovementEnvironment"));
        }

        [Test]
        public void CombatAnimationSet_ExportsProfiledMeleeManifestWithoutTopLevelStrikes()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            UnityEngine.Object instance = Resources.Load("CombatAnimationSets/SwordAndShield", combatAnimationSetType);
            Assert.That(instance, Is.Not.Null);

            object export = InvokeInstanceMethod(instance, "BuildMeleeExport");
            string json = JsonUtility.ToJson(export, true);

            Assert.That(export.GetType().GetField("strikes"), Is.Null);
            Assert.That(json, Does.Contain("\"profiles\""));
            Assert.That(json, Does.Contain("\"combat_profile\": \"SWORD_AND_SHIELD\""));
            Assert.That(json, Does.Contain("\"slot_id\": \"light_combo_1\""));
        }

        [Test]
        public void DaggerCombatAnimationSet_AuthorsTwoStrikeAutoAttackSequence()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            UnityEngine.Object set = Resources.Load("CombatAnimationSets/Daggers", combatAnimationSetType);
            Assert.That(set, Is.Not.Null);

            string[] sequence = (string[])RequireProperty(
                    combatAnimationSetType,
                    "AutoAttackVisualSequenceActionIds")
                .GetValue(set)!;
            Assert.That(
                sequence,
                Is.EqualTo(new[]
                {
                    "DAGGER_COMBO_ATTACK_01_01",
                    "DAGGER_COMBO_ATTACK_01_02",
                }));
            Assert.That(
                combatAnimationSetType.GetField("autoAttackSequenceIntervalMs")!.GetValue(set),
                Is.EqualTo(500));

            Assert.That(
                (bool)RequireMethod(
                        combatAnimationSetType,
                        "IsAutoAttackVisualSequenceTransition",
                        typeof(int),
                        typeof(string))
                    .Invoke(set, new object[] { 1, "DAGGER_COMBO_ATTACK_01_02" })!,
                Is.True);
            Assert.That(
                (bool)RequireMethod(
                        combatAnimationSetType,
                        "IsAutoAttackVisualSequenceRestart",
                        typeof(int),
                        typeof(string))
                    .Invoke(set, new object[] { 2, "AUTO_ATTACK_1" })!,
                Is.True);

            string json = JsonUtility.ToJson(InvokeInstanceMethod(set, "BuildMeleeExport"), true);
            Assert.That(json, Does.Contain("\"auto_attack_sequence\""));
            Assert.That(json, Does.Contain("\"auto_attack_sequence_interval_ms\": 500"));
            Assert.That(json, Does.Contain("DAGGER_COMBO_ATTACK_01_02"));

            Type schedulerType = RequireType("Arena.Presentation.AutoAttackSwingScheduler");
            Assert.That(
                (bool)RequireMethod(
                        schedulerType,
                        "SupportsLocalPrediction",
                        combatAnimationSetType)
                    .Invoke(null, new[] { set })!,
                Is.False);

            UnityEngine.Object swordAndShield = Resources.Load(
                "CombatAnimationSets/SwordAndShield",
                combatAnimationSetType);
            Assert.That(swordAndShield, Is.Not.Null);
            Assert.That(
                (bool)RequireMethod(
                        schedulerType,
                        "SupportsLocalPrediction",
                        combatAnimationSetType)
                    .Invoke(null, new[] { swordAndShield })!,
                Is.True);
        }

        [Test]
        public void CombatAnimationSet_MeleeExportUsesStrikeHitEventBeforeAuthoredHitWindows()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip clip = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    clip,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.73f,
                        },
                    });

                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "clip", clip);

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");

                Assert.That(GetFirstImpactDelayMs(strike), Is.EqualTo(730));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_StartupTrimShiftsPlaybackAndExportOntoSameTimeline()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip clip = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    clip,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.73f,
                        },
                    });

                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "clip", clip);
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "startupTrimSeconds", 0.53f);

                Assert.That(
                    (float)RequireMethod(combatAnimationSetType, "GetStrikeStartupTrimSeconds", typeof(int))
                        .Invoke(set, new object[] { 1 })!,
                    Is.EqualTo(0.53f).Within(PositionTolerance));
                Assert.That(
                    (float)RequireMethod(combatAnimationSetType, "GetStrikeStartupTrimNormalized", typeof(int))
                        .Invoke(set, new object[] { 1 })!,
                    Is.EqualTo(0.53f).Within(PositionTolerance));

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");
                Assert.That(GetFirstImpactDelayMs(strike), Is.EqualTo(200));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_StartupTrimAtContactProducesValidZeroDelayHit()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip clip = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    clip,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.4f,
                        },
                    });

                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "clip", clip);
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "startupTrimSeconds", 0.4f);

                MethodInfo firstHitResolver = RequireMethod(
                    combatAnimationSetType,
                    "TryGetStrikeFirstHitWindowSeconds",
                    typeof(int),
                    typeof(float).MakeByRefType());
                object?[] firstHitArgs = { 1, -1f };
                Assert.That((bool)firstHitResolver.Invoke(set, firstHitArgs)!, Is.True);
                Assert.That((float)firstHitArgs[1]!, Is.EqualTo(0f).Within(PositionTolerance));

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");
                Assert.That(GetFirstImpactDelayMs(strike), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_StartupTrimUpdatesCompatibilityHitWindowMirror()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type hitWindowType = RequireType("Arena.Presentation.WeaponStrikeHitWindowAuthoring");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip clip = CreateClipWithLength(2f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    clip,
                    new[]
                    {
                        new AnimationEvent { functionName = "OnStrikeHit", time = 0.75f },
                        new AnimationEvent { functionName = "OnStrikeHit", time = 1.5f },
                    });
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "clip", clip);
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "startupTrimSeconds", 0.5f);

                System.Collections.IList attacks =
                    (System.Collections.IList)combatAnimationSetType.GetField("meleeAttacks")!.GetValue(set)!;
                object?[] args = { null };
                Assert.That(
                    (bool)RequireMethod(
                            attackType,
                            "TryBuildHitWindowMirrorFromEvents",
                            hitWindowType.MakeArrayType().MakeByRefType())
                        .Invoke(attacks[0], args)!,
                    Is.True);

                Array mirrored = (Array)args[0]!;
                Assert.That(
                    (float)hitWindowType.GetField("timeNormalized")!.GetValue(mirrored.GetValue(0))!,
                    Is.EqualTo(0.125f).Within(PositionTolerance));
                Assert.That(
                    (float)hitWindowType.GetField("timeNormalized")!.GetValue(mirrored.GetValue(1))!,
                    Is.EqualTo(0.5f).Within(PositionTolerance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_HitWindowMirrorTracksEveryStrikeHitEvent()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type hitWindowType = RequireType("Arena.Presentation.WeaponStrikeHitWindowAuthoring");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip clip = CreateClipWithLength(2f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    clip,
                    new[]
                    {
                        new AnimationEvent { functionName = "OnStrikeHit", time = 0.5f },
                        new AnimationEvent { functionName = "OnStrikeHit", time = 1.5f },
                    });
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "clip", clip);

                System.Collections.IList attacks =
                    (System.Collections.IList)combatAnimationSetType.GetField("meleeAttacks")!.GetValue(set)!;
                object attack = attacks[0]!;
                object?[] args = { null };
                bool found = (bool)RequireMethod(
                        attackType,
                        "TryBuildHitWindowMirrorFromEvents",
                        hitWindowType.MakeArrayType().MakeByRefType())
                    .Invoke(attack, args)!;

                Assert.That(found, Is.True);
                Array mirrored = (Array)args[0]!;
                Assert.That(mirrored.Length, Is.EqualTo(2));
                Assert.That(
                    (float)hitWindowType.GetField("timeNormalized")!.GetValue(mirrored.GetValue(0))!,
                    Is.EqualTo(0.25f).Within(PositionTolerance));
                Assert.That(
                    (float)hitWindowType.GetField("timeNormalized")!.GetValue(mirrored.GetValue(1))!,
                    Is.EqualTo(0.75f).Within(PositionTolerance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_PhasedMeleeExportOffsetsStrikeHitEventByStartAndLoop()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type phaseSetType = RequireType("Arena.Presentation.WeaponPhasedActionClipSet");
            Type presentationModeType = RequireType("Arena.Presentation.WeaponMeleePresentationMode");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip start = CreateClipWithLength(1f);
            AnimationClip loop = CreateClipWithLength(2f);
            AnimationClip end = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    end,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.25f,
                        },
                    });

                object phaseSet = Activator.CreateInstance(phaseSetType)!;
                phaseSetType.GetField("start")!.SetValue(phaseSet, start);
                phaseSetType.GetField("loop")!.SetValue(phaseSet, loop);
                phaseSetType.GetField("end")!.SetValue(phaseSet, end);

                SetFirstMeleeAttackField(
                    combatAnimationSetType,
                    attackType,
                    set,
                    "presentationMode",
                    Enum.Parse(presentationModeType, "Phased"));
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "phasedGround", phaseSet);

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");

                Assert.That(GetFirstImpactDelayMs(strike), Is.EqualTo(3250));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(end);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_PhasedMeleeExportReadsStrikeHitEventsFromAllPhases()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type phaseSetType = RequireType("Arena.Presentation.WeaponPhasedActionClipSet");
            Type presentationModeType = RequireType("Arena.Presentation.WeaponMeleePresentationMode");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip start = CreateClipWithLength(1f);
            AnimationClip loop = CreateClipWithLength(2f);
            AnimationClip end = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    start,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.2f,
                        },
                    });
                AnimationUtility.SetAnimationEvents(
                    loop,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.3f,
                        },
                    });
                AnimationUtility.SetAnimationEvents(
                    end,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.4f,
                        },
                    });

                object phaseSet = Activator.CreateInstance(phaseSetType)!;
                phaseSetType.GetField("start")!.SetValue(phaseSet, start);
                phaseSetType.GetField("loop")!.SetValue(phaseSet, loop);
                phaseSetType.GetField("end")!.SetValue(phaseSet, end);

                SetFirstMeleeAttackField(
                    combatAnimationSetType,
                    attackType,
                    set,
                    "presentationMode",
                    Enum.Parse(presentationModeType, "Phased"));
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "phasedGround", phaseSet);

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");

                Assert.That(GetImpactDelayMs(strike), Is.EqualTo(new[] { 200, 1300, 3400 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(end);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_StartEndPhasedMeleeExportSkipsMissingLoop()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type phaseSetType = RequireType("Arena.Presentation.WeaponPhasedActionClipSet");
            Type presentationModeType = RequireType("Arena.Presentation.WeaponMeleePresentationMode");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip start = CreateClipWithLength(1f);
            AnimationClip end = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    end,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.25f,
                        },
                    });

                object phaseSet = Activator.CreateInstance(phaseSetType)!;
                phaseSetType.GetField("start")!.SetValue(phaseSet, start);
                phaseSetType.GetField("end")!.SetValue(phaseSet, end);

                SetFirstMeleeAttackField(
                    combatAnimationSetType,
                    attackType,
                    set,
                    "presentationMode",
                    Enum.Parse(presentationModeType, "Phased"));
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "phasedGround", phaseSet);

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");

                Assert.That(GetFirstImpactDelayMs(strike), Is.EqualTo(1250));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(end);
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_LoopEndPhasedMeleeExportUsesResolvedStartAndLoop()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type phaseSetType = RequireType("Arena.Presentation.WeaponPhasedActionClipSet");
            Type presentationModeType = RequireType("Arena.Presentation.WeaponMeleePresentationMode");
            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            AnimationClip loop = CreateClipWithLength(1f);
            AnimationClip end = CreateClipWithLength(1f);
            try
            {
                AnimationUtility.SetAnimationEvents(
                    end,
                    new[]
                    {
                        new AnimationEvent
                        {
                            functionName = "OnStrikeHit",
                            time = 0.25f,
                        },
                    });

                object phaseSet = Activator.CreateInstance(phaseSetType)!;
                phaseSetType.GetField("loop")!.SetValue(phaseSet, loop);
                phaseSetType.GetField("end")!.SetValue(phaseSet, end);

                SetFirstMeleeAttackField(
                    combatAnimationSetType,
                    attackType,
                    set,
                    "presentationMode",
                    Enum.Parse(presentationModeType, "Phased"));
                SetFirstMeleeAttackField(combatAnimationSetType, attackType, set, "phasedGround", phaseSet);

                object export = InvokeInstanceMethod(set, "BuildMeleeExport");
                object strike = FindExportedStrike(GetExportedStrikes(export), "MELEE_ATTACK_1");

                Assert.That(GetFirstImpactDelayMs(strike), Is.EqualTo(2250));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(end);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void DaggerCoupDeGrace_UsesOnlyAirToFloorEndAsSingleClip()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type phasedEntryType = RequireType("Arena.Presentation.WeaponPhasedActionEntry");
            UnityEngine.Object instance = Resources.Load("CombatAnimationSets/Daggers", combatAnimationSetType);
            Assert.That(instance, Is.Not.Null);

            int strikeIndex = (int)InvokeInstanceMethod(
                instance,
                "GetStrikeIndexForActionId",
                "DAGGER_COUP_DE_GRACE");
            AnimationClip clip = (AnimationClip)InvokeInstanceMethod(
                instance,
                "GetStrikeClip",
                strikeIndex);
            Assert.That(clip.name, Is.EqualTo("Attack_Air_to_Floor_01_End"));

            object?[] phasedArgs = { "DAGGER_COUP_DE_GRACE", null };
            bool hasPhasedEntry = (bool)RequireMethod(
                    combatAnimationSetType,
                    "TryGetPhasedMeleeEntry",
                    typeof(string),
                    phasedEntryType.MakeByRefType())
                .Invoke(instance, phasedArgs)!;
            Assert.That(hasPhasedEntry, Is.False);

            object export = InvokeInstanceMethod(instance, "BuildMeleeExport");
            object strike = FindExportedStrike(
                GetExportedStrikes(export),
                "DAGGER_COUP_DE_GRACE");
            Assert.That(GetImpactDelayMs(strike), Is.EqualTo(new[] { 43, 43 }));
        }

        [Test]
        public void ArcherAnimationSet_ExportsStandardArrowProjectilesExceptRainShot()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            UnityEngine.Object instance = Resources.Load("CombatAnimationSets/ArcherBow", combatAnimationSetType);
            Assert.That(instance, Is.Not.Null);

            object export = InvokeInstanceMethod(instance, "BuildMeleeExport");
            object profile = ((Array)export.GetType().GetField("profiles")!.GetValue(export)!).GetValue(0)!;
            Array strikes = (Array)profile.GetType().GetField("strikes")!.GetValue(profile)!;
            string[] projectileStrikeIds =
            {
                "ARCHER_QUICK_SHOT",
                "ARCHER_FOLLOW_THROUGH",
                "ARCHER_LOW_DRAW",
                "ARCHER_FINISHING_SHOT",
                "ARCHER_POWER_SHOT",
                "ARCHER_EVASIVE_SHOT",
                "ARCHER_AIR_SHOT",
                "AUTO_ATTACK_1",
            };

            foreach (string strikeId in projectileStrikeIds)
            {
                object strike = FindExportedStrike(strikes, strikeId);
                object? projectile = strike.GetType().GetField("projectile")!.GetValue(strike);
                Assert.That(projectile, Is.Not.Null, strikeId);
                Assert.That(
                    (string)projectile!.GetType().GetField("projectile_id")!.GetValue(projectile)!,
                    Is.EqualTo("ARROW_STANDARD"),
                    strikeId);
                Assert.That(
                    (bool)projectile.GetType().GetField("requires_initial_line_of_sight")!.GetValue(projectile)!,
                    Is.True,
                    strikeId);

                string[] inheritedNumericFields =
                {
                    "speed",
                    "max_distance",
                    "radius",
                    "spawn_forward",
                    "spawn_height",
                    "aim_height_scale",
                    "update_interval_seconds",
                };
                foreach (string fieldName in inheritedNumericFields)
                {
                    Assert.That(
                        (float)projectile.GetType().GetField(fieldName)!.GetValue(projectile)!,
                        Is.EqualTo(0f),
                        $"{strikeId}.{fieldName} should export 0 to inherit the projectile catalog default");
                }
            }

            Assert.That(
                FindExportedStrike(strikes, "ARCHER_RAIN_SHOT").GetType().GetField("projectile")!.GetValue(
                    FindExportedStrike(strikes, "ARCHER_RAIN_SHOT")),
                Is.Null,
                "Rain Shot should remain a non-single-arrow action until volley behavior exists.");
        }

        [Test]
        public void ArcherAnimationSet_UsesArcherPackWeaponMounts()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type visualBindingType = RequireType("Arena.Presentation.WeaponVisualBinding");

            UnityEngine.Object loaded = Resources.Load("CombatAnimationSets/ArcherBow", combatAnimationSetType);
            Assert.That(loaded, Is.Not.Null);

            Array visuals = (Array)RequireProperty(combatAnimationSetType, "VisualBindings").GetValue(loaded)!;
            Assert.That(visuals, Has.Length.EqualTo(3));

            object drawnBow = FindVisualBinding(visuals, visualBindingType, "bow_drawn");
            Assert.That((string)visualBindingType.GetField("drawnMountId")!.GetValue(drawnBow)!, Is.EqualTo("archer_bow_hand"));
            Assert.That(Convert.ToInt32(visualBindingType.GetField("attachmentMode")!.GetValue(drawnBow)!), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(visualBindingType.GetField("visibility")!.GetValue(drawnBow)!), Is.EqualTo(1));
            Array boneRemaps = (Array)visualBindingType.GetField("skinnedBoneRemaps")!.GetValue(drawnBow)!;
            Assert.That(boneRemaps, Has.Length.EqualTo(0));

            object stowedBow = FindVisualBinding(visuals, visualBindingType, "bow_stowed");
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(stowedBow)!, Is.EqualTo("archer_bow_stowed"));
            Assert.That(Convert.ToInt32(visualBindingType.GetField("visibility")!.GetValue(stowedBow)!), Is.EqualTo(2));

            object quiver = FindVisualBinding(visuals, visualBindingType, "quiver");
            Assert.That((string)visualBindingType.GetField("drawnMountId")!.GetValue(quiver)!, Is.EqualTo("archer_quiver_stowed"));
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(quiver)!, Is.EqualTo("archer_quiver_stowed"));
            Assert.That((Quaternion)visualBindingType.GetField("drawnLocalRotation")!.GetValue(quiver)!, Is.EqualTo(Quaternion.identity));
            Assert.That((Quaternion)visualBindingType.GetField("stowedLocalRotation")!.GetValue(quiver)!, Is.EqualTo(Quaternion.identity));
        }

        [Test]
        public void AnimationSetAssets_ExposeExplicitIdentityAndWeaponPresentation()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");

            foreach ((string resourcePath, string expectedId, int expectedVisuals) in new[]
            {
                ("CombatAnimationSets/SwordAndShield", "SWORD_AND_SHIELD", 2),
            })
            {
                UnityEngine.Object loaded = Resources.Load(resourcePath, combatAnimationSetType);
                Assert.That(loaded, Is.Not.Null, resourcePath);

                Assert.That(
                    (string)RequireProperty(combatAnimationSetType, "AnimationSetIdOrDefault").GetValue(loaded)!,
                    Is.EqualTo(expectedId),
                    resourcePath);
                Assert.That(
                    (string)RequireProperty(combatAnimationSetType, "CombatProfileIdOrDefault").GetValue(loaded)!,
                    Is.EqualTo(expectedId),
                    resourcePath);
                Assert.That(RequireProperty(combatAnimationSetType, "DrawWeaponClip").GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(RequireProperty(combatAnimationSetType, "SheathWeaponClip").GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("airHitF")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("airHitB")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("airHitL")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("airHitR")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("staggerF")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("staggerB")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("staggerL")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("staggerR")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("stunStart")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("stunLoop")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(combatAnimationSetType.GetField("stunEnd")!.GetValue(loaded), Is.Not.Null, resourcePath);
                Assert.That(
                    ((Array)RequireProperty(combatAnimationSetType, "VisualBindings").GetValue(loaded)!).Length,
                    Is.EqualTo(expectedVisuals),
                    resourcePath);
            }
        }

        [Test]
        public void AnimationSetAssets_AuthorCompleteCombatAndNonCombatHitReactions()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");

            foreach (string resourcePath in new[]
            {
                "CombatAnimationSets/ArcherBow",
                "CombatAnimationSets/Daggers",
                "CombatAnimationSets/Staff",
                "CombatAnimationSets/SwordAndShield",
            })
            {
                UnityEngine.Object loaded = Resources.Load(resourcePath, combatAnimationSetType);
                Assert.That(loaded, Is.Not.Null, resourcePath);

                foreach (string fieldName in new[]
                {
                    "hitF", "hitB", "hitL", "hitR",
                    "hitCombatF", "hitCombatB", "hitCombatL", "hitCombatR",
                    "airHitF", "airHitB", "airHitL", "airHitR",
                    "airHitCombatF", "airHitCombatB", "airHitCombatL", "airHitCombatR",
                })
                {
                    Assert.That(combatAnimationSetType.GetField(fieldName)!.GetValue(loaded), Is.Not.Null, $"{resourcePath}.{fieldName}");
                }

                AssertHitReactionClipSet(
                    InvokeInstanceMethod(loaded, "ResolveHitReactionClips", true, false),
                    "Hit_F",
                    "Hit_B",
                    "Hit_L",
                    "Hit_R",
                    $"{resourcePath} grounded non-combat");
                AssertHitReactionClipSet(
                    InvokeInstanceMethod(loaded, "ResolveHitReactionClips", true, true),
                    "Hit_Combat_F",
                    "Hit_Combat_B",
                    "Hit_Combat_L",
                    "Hit_Combat_R",
                    $"{resourcePath} grounded combat");
                AssertHitReactionClipSet(
                    InvokeInstanceMethod(loaded, "ResolveHitReactionClips", false, false),
                    "Hit_Air_F",
                    "Hit_Air_B",
                    "Hit_Air_L",
                    "Hit_Air_R",
                    $"{resourcePath} air non-combat");
                AssertHitReactionClipSet(
                    InvokeInstanceMethod(loaded, "ResolveHitReactionClips", false, true),
                    "Hit_Combat_Air_F",
                    "Hit_Combat_Air_B",
                    "Hit_Combat_Air_L",
                    "Hit_Combat_Air_R",
                    $"{resourcePath} air combat");
            }
        }

        [Test]
        public void DaggersAnimationSet_UsesPackCalibratedDrawnAndStowedMounts()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type visualBindingType = RequireType("Arena.Presentation.WeaponVisualBinding");

            UnityEngine.Object loaded = Resources.Load("CombatAnimationSets/Daggers", combatAnimationSetType);
            Assert.That(loaded, Is.Not.Null);

            Array visuals = (Array)RequireProperty(combatAnimationSetType, "VisualBindings").GetValue(loaded)!;
            object main = FindVisualBinding(visuals, visualBindingType, "dagger_main");
            object off = FindVisualBinding(visuals, visualBindingType, "dagger_off");

            Assert.That((string)visualBindingType.GetField("drawnMountId")!.GetValue(main)!, Is.EqualTo("main_weapon_hand"));
            Assert.That((string)visualBindingType.GetField("drawnMountId")!.GetValue(off)!, Is.EqualTo("off_weapon_hand"));
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(main)!, Is.EqualTo("dagger_main_stowed"));
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(off)!, Is.EqualTo("dagger_off_stowed"));
            AssertVector3(
                (Vector3)visualBindingType.GetField("drawnLocalPosition")!.GetValue(main)!,
                new Vector3(-0.05021231f, 0.10228082f, -0.024196113f));
            AssertQuaternion(
                (Quaternion)visualBindingType.GetField("drawnLocalRotation")!.GetValue(main)!,
                new Quaternion(-0.8719281f, 0.30998212f, -0.291152f, 0.24265818f));
            AssertVector3(
                (Vector3)visualBindingType.GetField("drawnLocalPosition")!.GetValue(off)!,
                new Vector3(0.050624337f, -0.10006088f, 0.031601f));
            AssertQuaternion(
                (Quaternion)visualBindingType.GetField("drawnLocalRotation")!.GetValue(off)!,
                new Quaternion(-0.8739462f, 0.28821984f, -0.31814724f, 0.2278809f));
            AssertVector3((Vector3)visualBindingType.GetField("stowedLocalPosition")!.GetValue(main)!, Vector3.zero);
            AssertQuaternion((Quaternion)visualBindingType.GetField("stowedLocalRotation")!.GetValue(main)!, Quaternion.identity);
            AssertVector3((Vector3)visualBindingType.GetField("stowedLocalPosition")!.GetValue(off)!, Vector3.zero);
            AssertQuaternion((Quaternion)visualBindingType.GetField("stowedLocalRotation")!.GetValue(off)!, Quaternion.identity);
        }

        [Test]
        public void DaggersAnimationSet_AuthorsStealthedCrouchLocomotionMode()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");

            UnityEngine.Object loaded = Resources.Load("CombatAnimationSets/Daggers", combatAnimationSetType);
            Assert.That(loaded, Is.Not.Null);

            Array overrides = (Array)RequireProperty(combatAnimationSetType, "LocomotionModeOverridesOrEmpty").GetValue(loaded)!;
            object stealthed = FindLocomotionModeOverride(overrides, "STEALTHED");
            Type overrideType = stealthed.GetType();

            Assert.That(overrideType.GetField("locomotionIdle")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("locomotionIdleCombat")!.GetValue(stealthed), Is.Not.Null);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("walk")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("walkCombat")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("run")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("runCombat")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("walkStop")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("walkStopCombat")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("runStop")!.GetValue(stealthed)!);
            AssertDirectionalClipSetHasAllDirections(overrideType.GetField("runStopCombat")!.GetValue(stealthed)!);
            Assert.That(overrideType.GetField("turn90L")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn90R")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn180L")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn180R")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn90CombatL")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn90CombatR")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn180CombatL")!.GetValue(stealthed), Is.Not.Null);
            Assert.That(overrideType.GetField("turn180CombatR")!.GetValue(stealthed), Is.Not.Null);
        }

        [Test]
        public void DaggerCombatPrefabs_UseProjectAuthoredUrpMaterial()
        {
            const string materialPath = "Assets/Arena/Resources/CombatAnimationSets/DaggerPackAuthored.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Assert.That(material, Is.Not.Null);
            Assert.That(material!.shader, Is.Not.Null);
            Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));

            string[] prefabPaths =
            {
                "Assets/Arena/Resources/CombatAnimationSets/DaggerMainPackAuthored.prefab",
                "Assets/Arena/Resources/CombatAnimationSets/DaggerOffPackAuthored.prefab",
            };

            foreach (string prefabPath in prefabPaths)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(prefab, Is.Not.Null, prefabPath);

                Renderer[] renderers = prefab!.GetComponentsInChildren<Renderer>(true);
                Assert.That(renderers, Is.Not.Empty, prefabPath);

                foreach (Material rendererMaterial in renderers.SelectMany(renderer => renderer.sharedMaterials))
                {
                    Assert.That(rendererMaterial, Is.Not.Null, prefabPath);
                    Assert.That(AssetDatabase.GetAssetPath(rendererMaterial), Is.EqualTo(materialPath), prefabPath);
                    Assert.That(rendererMaterial!.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"), prefabPath);
                }
            }
        }

        [Test]
        public void SwordAndShieldAnimationSet_UsesCalibratedStowedWeaponVisuals()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type visualBindingType = RequireType("Arena.Presentation.WeaponVisualBinding");

            UnityEngine.Object loaded = Resources.Load("CombatAnimationSets/SwordAndShield", combatAnimationSetType);
            Assert.That(loaded, Is.Not.Null);

            Array visuals = (Array)RequireProperty(combatAnimationSetType, "VisualBindings").GetValue(loaded)!;
            Assert.That(visuals.Length, Is.EqualTo(2));

            object sword = visuals.GetValue(0)!;
            object shield = visuals.GetValue(1)!;
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(sword)!, Is.EqualTo("main_weapon_stowed"));
            Assert.That((string)visualBindingType.GetField("stowedMountId")!.GetValue(shield)!, Is.EqualTo("off_weapon_stowed"));
            AssertVector3(
                (Vector3)visualBindingType.GetField("stowedLocalPosition")!.GetValue(sword)!,
                new Vector3(0.39266303f, 0.3338352f, 0.7462119f));
            AssertQuaternion(
                (Quaternion)visualBindingType.GetField("stowedLocalRotation")!.GetValue(sword)!,
                new Quaternion(0.5385772f, 0.5646582f, -0.42693132f, 0.45697406f));
            AssertVector3(
                (Vector3)visualBindingType.GetField("stowedLocalPosition")!.GetValue(shield)!,
                new Vector3(-0.054233134f, 0.22565512f, 0.16345683f));
            AssertQuaternion(
                (Quaternion)visualBindingType.GetField("stowedLocalRotation")!.GetValue(shield)!,
                new Quaternion(-0.36491364f, -0.27877307f, 0.76177317f, 0.45697403f));
        }

        [Test]
        public void CombatAnimationSet_ExplicitCombatProfileIdDrivesMeleeExportProfile()
        {
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");

            ScriptableObject set = CreateMinimalExportableCombatAnimationSet(combatAnimationSetType);
            combatAnimationSetType.GetField("combatProfileId")!.SetValue(set, "custom_profile");

            object export = InvokeInstanceMethod(set, "BuildMeleeExport");
            string json = JsonUtility.ToJson(export, true);

            Assert.That(json, Does.Contain("\"combat_profile\": \"CUSTOM_PROFILE\""));
        }

        [Test]
        public void ActionBarKeymap_AllowsShiftedAndUnshiftedNumberRows()
        {
            Type keymapType = RequireType("Arena.Combat.ActionBarKeymap");
            Type bindingType = RequireType("Arena.Combat.ActionBarSlotBinding");
            Type slotIdsType = RequireType("Arena.Combat.ActionBarSlotIds");

            object?[] row0Args = { 0, 2, null! };
            object?[] row2Args = { 2, 2, null! };
            bool foundRow0 = (bool)RequireMethod(
                    keymapType,
                    "TryGetBindingForCell",
                    typeof(int),
                    typeof(int),
                    bindingType.MakeByRefType())
                .Invoke(null, row0Args)!;
            bool foundRow2 = (bool)RequireMethod(
                    keymapType,
                    "TryGetBindingForCell",
                    typeof(int),
                    typeof(int),
                    bindingType.MakeByRefType())
                .Invoke(null, row2Args)!;

            Assert.That(foundRow0, Is.True);
            Assert.That(foundRow2, Is.True);
            Assert.That((string)bindingType.GetField("SlotId")!.GetValue(row0Args[2])!, Is.EqualTo(slotIdsType.GetField("Slot02")!.GetValue(null)));
            Assert.That((bool)bindingType.GetField("RequiresShift")!.GetValue(row0Args[2])!, Is.False);
            Assert.That((string)bindingType.GetField("SlotId")!.GetValue(row2Args[2])!, Is.EqualTo(slotIdsType.GetField("Slot22")!.GetValue(null)));
            Assert.That((bool)bindingType.GetField("RequiresShift")!.GetValue(row2Args[2])!, Is.True);
        }

        [Test]
        public void CombatAnimationSetEditor_ImportResolutionRequiresExactCombatProfile()
        {
            Type editorAssemblyType = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.CombatAnimationSetEditor", throwOnError: true)!;
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type manifestDocumentType = RequireType("Arena.Combat.MeleeManifestDocument");
            Type manifestProfileType = RequireType("Arena.Combat.MeleeManifestProfile");
            Type manifestStrikeType = RequireType("Arena.Combat.MeleeManifestStrike");

            ScriptableObject set = ScriptableObject.CreateInstance(combatAnimationSetType);
            combatAnimationSetType.GetField("combatProfileId")!.SetValue(set, "STAFF");

            object doc = Activator.CreateInstance(manifestDocumentType)!;
            object profile = Activator.CreateInstance(manifestProfileType)!;
            object strike = Activator.CreateInstance(manifestStrikeType)!;
            manifestStrikeType.GetField("id")!.SetValue(strike, "TEST_STRIKE");
            manifestProfileType.GetField("combat_profile")!.SetValue(profile, "SWORD_AND_SHIELD");
            manifestProfileType.GetField("strikes")!.SetValue(profile, Array.CreateInstance(manifestStrikeType, 1));
            ((Array)manifestProfileType.GetField("strikes")!.GetValue(profile)!).SetValue(strike, 0);
            manifestDocumentType.GetField("profiles")!.SetValue(doc, Array.CreateInstance(manifestProfileType, 1));
            ((Array)manifestDocumentType.GetField("profiles")!.GetValue(doc)!).SetValue(profile, 0);

            object[] args = { doc, set, null!, null! };
            bool resolved = (bool)RequireMethod(
                    editorAssemblyType,
                    "TryResolveImportedStrikes",
                    manifestDocumentType,
                    combatAnimationSetType,
                    manifestStrikeType.MakeArrayType().MakeByRefType(),
                    typeof(string).MakeByRefType())
                .Invoke(null, args)!;

            Assert.That(resolved, Is.False);
            Assert.That((string)args[3], Does.Contain("exact combat-profile match"));
        }

        [Test]
        public void CombatAnimationSetEditor_ManifestReadPreservesOptionalProjectilePresence()
        {
            Type editorType = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.CombatAnimationSetEditor", throwOnError: true)!;
            MethodInfo deserialize = editorType.GetMethod(
                "DeserializeMeleeManifestDocument",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            string json =
                "{\"profiles\":[{\"combat_profile\":\"TEST\",\"strikes\":[" +
                "{\"id\":\"MELEE\",\"hit_windows\":[{\"impact_delay_ms\":100}]}," +
                "{\"id\":\"ARROW\",\"hit_windows\":[{\"impact_delay_ms\":200}]," +
                "\"projectile\":{\"projectile_id\":\"ARROW_STANDARD\"}}]}]}";

            object document = deserialize.Invoke(null, new object[] { json })!;
            Array profiles = (Array)document.GetType().GetField("profiles")!.GetValue(document)!;
            Array strikes = (Array)profiles.GetValue(0)!.GetType().GetField("strikes")!.GetValue(profiles.GetValue(0))!;
            FieldInfo projectileField = strikes.GetValue(0)!.GetType().GetField("projectile")!;

            Assert.That(projectileField.GetValue(strikes.GetValue(0)), Is.Null);
            Assert.That(projectileField.GetValue(strikes.GetValue(1)), Is.Not.Null);
        }

        [Test]
        public void CombatAnimationSetEditor_ImportResolutionUsesExplicitCombatProfile()
        {
            Type editorAssemblyType = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
                .GetType("Arena.Editor.CombatAnimationSetEditor", throwOnError: true)!;
            Type combatAnimationSetType = RequireType("Arena.Presentation.CombatAnimationSet");
            Type manifestDocumentType = RequireType("Arena.Combat.MeleeManifestDocument");
            Type manifestProfileType = RequireType("Arena.Combat.MeleeManifestProfile");
            Type manifestStrikeType = RequireType("Arena.Combat.MeleeManifestStrike");

            ScriptableObject set = ScriptableObject.CreateInstance(combatAnimationSetType);
            combatAnimationSetType.GetField("combatProfileId")!.SetValue(set, "SWORD_AND_SHIELD");

            object doc = Activator.CreateInstance(manifestDocumentType)!;
            object profile = Activator.CreateInstance(manifestProfileType)!;
            object strike = Activator.CreateInstance(manifestStrikeType)!;
            manifestStrikeType.GetField("id")!.SetValue(strike, "TEST_STRIKE");
            manifestProfileType.GetField("combat_profile")!.SetValue(profile, "SWORD_AND_SHIELD");
            manifestProfileType.GetField("strikes")!.SetValue(profile, Array.CreateInstance(manifestStrikeType, 1));
            ((Array)manifestProfileType.GetField("strikes")!.GetValue(profile)!).SetValue(strike, 0);
            manifestDocumentType.GetField("profiles")!.SetValue(doc, Array.CreateInstance(manifestProfileType, 1));
            ((Array)manifestDocumentType.GetField("profiles")!.GetValue(doc)!).SetValue(profile, 0);

            object[] args = { doc, set, null!, null! };
            bool resolved = (bool)RequireMethod(
                    editorAssemblyType,
                    "TryResolveImportedStrikes",
                    manifestDocumentType,
                    combatAnimationSetType,
                    manifestStrikeType.MakeArrayType().MakeByRefType(),
                    typeof(string).MakeByRefType())
                .Invoke(null, args)!;

            Assert.That(resolved, Is.True);
            Assert.That(((Array)args[2]).Length, Is.EqualTo(1));
            Assert.That((string)args[3], Is.Empty);
        }

        [Test]
        public void PlayerAnimator_CombatTransitionSelection_UsesExactLocomotionBandWhenClipExists()
        {
            Type animatorType = RequireType("Arena.Presentation.PlayerAnimator");
            MethodInfo method = RequireMethod(
                animatorType,
                "TryResolveCombatStanceTransitionStateHash",
                typeof(bool),
                typeof(int),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(int).MakeByRefType());

            int enterRunStateHash = GetStaticField<int>(animatorType, "EnterCombatRunStateHash");
            object?[] args =
            {
                true,
                2,
                true,
                true,
                true,
                0,
            };

            bool resolved = (bool)method.Invoke(null, args)!;

            Assert.That(resolved, Is.True);
            Assert.That((int)args[5]!, Is.EqualTo(enterRunStateHash));
        }

        [Test]
        public void PlayerAnimator_CombatTransitionSelection_DoesNotFallBackToWalkForRunBand()
        {
            Type animatorType = RequireType("Arena.Presentation.PlayerAnimator");
            MethodInfo method = RequireMethod(
                animatorType,
                "TryResolveCombatStanceTransitionStateHash",
                typeof(bool),
                typeof(int),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(int).MakeByRefType());

            object?[] args =
            {
                true,
                2,
                true,
                true,
                false,
                0,
            };

            bool resolved = (bool)method.Invoke(null, args)!;

            Assert.That(resolved, Is.False);
            Assert.That((int)args[5]!, Is.EqualTo(0));
        }

        [Test]
        public void PlayerAnimator_BlockTransitionPolicy_SkipsStartAndEndForMovingDirectionalBlock()
        {
            Type animatorType = RequireType("Arena.Presentation.PlayerAnimator");
            MethodInfo method = RequireMethod(
                animatorType,
                "ShouldUseMovingBlockLoopTransition",
                typeof(bool),
                typeof(float),
                typeof(bool));

            bool movingResolved = (bool)method.Invoke(null, new object?[] { true, 0.5f, true })!;
            bool stationaryResolved = (bool)method.Invoke(null, new object?[] { true, 0.0f, true })!;
            bool noDirectionalResolved = (bool)method.Invoke(null, new object?[] { true, 0.5f, false })!;

            Assert.That(movingResolved, Is.True);
            Assert.That(stationaryResolved, Is.False);
            Assert.That(noDirectionalResolved, Is.False);
        }

        [Test]
        public void ClientSimulationState_PredictedMovementRestriction_ComposesByInputTick()
        {
            object simState = CreateInstance("Arena.Simulation.ClientSimulationState");
            InvokeInstanceMethod(
                simState,
                "SetMovementContext",
                false,
                1.0f,
                PlayerRadius,
                PlayerHeight,
                10u);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                11u,
                false,
                0.0f,
                0.5f);

            object?[] tick10Args = { 10u, false };
            object tick10Sample = RequireMethod(
                    simState.GetType(),
                    "GetMovementContextForTick",
                    typeof(uint),
                    typeof(bool).MakeByRefType())
                .Invoke(simState, tick10Args)!;
            object?[] tick11Args = { 11u, false };
            object tick11Sample = RequireMethod(
                    simState.GetType(),
                    "GetMovementContextForTick",
                    typeof(uint),
                    typeof(bool).MakeByRefType())
                .Invoke(simState, tick11Args)!;

            Assert.That((bool)tick10Args[1]!, Is.False);
            Assert.That((float)tick10Sample.GetType().GetProperty("MoveSpeedMultiplier")!.GetValue(tick10Sample)!, Is.EqualTo(1.0f).Within(PositionTolerance));

            Assert.That((bool)tick11Args[1]!, Is.False);
            Assert.That((uint)tick11Sample.GetType().GetProperty("Tick")!.GetValue(tick11Sample)!, Is.EqualTo(11u));
            Assert.That((float)tick11Sample.GetType().GetProperty("MoveSpeedMultiplier")!.GetValue(tick11Sample)!, Is.EqualTo(0.5f).Within(PositionTolerance));
        }

        [Test]
        public void ClientSimulationState_PredictedMovementRestriction_CanRestoreAuthorityWithLaterTick()
        {
            object simState = CreateInstance("Arena.Simulation.ClientSimulationState");
            InvokeInstanceMethod(
                simState,
                "SetMovementContext",
                false,
                1.0f,
                PlayerRadius,
                PlayerHeight,
                10u);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                11u,
                false,
                0.0f,
                0.5f);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                12u,
                false,
                1.0f,
                4.0f);

            object?[] tick12Args = { 12u, false };
            object tick12Sample = RequireMethod(
                    simState.GetType(),
                    "GetMovementContextForTick",
                    typeof(uint),
                    typeof(bool).MakeByRefType())
                .Invoke(simState, tick12Args)!;

            Assert.That((bool)tick12Args[1]!, Is.False);
            Assert.That((uint)tick12Sample.GetType().GetProperty("Tick")!.GetValue(tick12Sample)!, Is.EqualTo(12u));
            Assert.That((float)tick12Sample.GetType().GetProperty("MoveSpeedMultiplier")!.GetValue(tick12Sample)!, Is.EqualTo(1.0f).Within(PositionTolerance));
        }

        [Test]
        public void ClientSimulationState_ClearingPredictedRestrictions_AllowsAuthoritativeEarlierRelease()
        {
            object simState = CreateInstance("Arena.Simulation.ClientSimulationState");
            InvokeInstanceMethod(
                simState,
                "SetMovementContext",
                false,
                1.0f,
                PlayerRadius,
                PlayerHeight,
                10u);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                11u,
                false,
                0.0f,
                0.5f);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                18u,
                false,
                1.0f,
                4.0f);

            InvokeInstanceMethod(simState, "ClearPredictedMovementRestrictions");
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                11u,
                false,
                0.0f,
                0.5f);
            InvokeInstanceMethod(
                simState,
                "SetPredictedMovementRestriction",
                13u,
                false,
                1.0f,
                4.0f);

            object?[] tick13Args = { 13u, false };
            object tick13Sample = RequireMethod(
                    simState.GetType(),
                    "GetMovementContextForTick",
                    typeof(uint),
                    typeof(bool).MakeByRefType())
                .Invoke(simState, tick13Args)!;

            Assert.That((bool)tick13Args[1]!, Is.False);
            Assert.That((uint)tick13Sample.GetType().GetProperty("Tick")!.GetValue(tick13Sample)!, Is.EqualTo(13u));
            Assert.That((float)tick13Sample.GetType().GetProperty("MoveSpeedMultiplier")!.GetValue(tick13Sample)!, Is.EqualTo(1.0f).Within(PositionTolerance));
        }

        [Test]
        public void ClientSimulationState_MovementActionState_RoundTripsStoredRow()
        {
            object simState = CreateInstance("Arena.Simulation.ClientSimulationState");
            Type actionType = RequireType("SpacetimeDB.Types.MovementActionState");
            object action = Activator.CreateInstance(actionType)!;
            actionType.GetField("Kind")!.SetValue(action, "DODGE");
            actionType.GetField("DirX")!.SetValue(action, 0.75f);
            actionType.GetField("DirZ")!.SetValue(action, -0.25f);
            actionType.GetField("FacingYawStart")!.SetValue(action, 1.2f);

            InvokeInstanceMethod(simState, "SetMovementActionState", action);
            object?[] tryGetArgs = { null! };
            bool found = (bool)RequireMethod(
                    simState.GetType(),
                    "TryGetMovementActionState",
                    actionType.MakeByRefType())
                .Invoke(simState, tryGetArgs)!;

            Assert.That(found, Is.True);
            Assert.That(tryGetArgs[0], Is.Not.Null);
            Assert.That((string)actionType.GetField("Kind")!.GetValue(tryGetArgs[0])!, Is.EqualTo("DODGE"));
            Assert.That((float)actionType.GetField("DirX")!.GetValue(tryGetArgs[0])!, Is.EqualTo(0.75f).Within(PositionTolerance));
            Assert.That((float)actionType.GetField("DirZ")!.GetValue(tryGetArgs[0])!, Is.EqualTo(-0.25f).Within(PositionTolerance));
        }

        [Test]
        public void MovementNetcodeConfig_TickBudgets_AreRetunedForThirtyThreeMillisecondTicks()
        {
            Type configType = RequireType("Arena.Input.MovementNetcodeConfig");

            Assert.That(GetStaticField<int>(configType, "FixedTickMilliseconds"), Is.EqualTo(33));
            // S5: the static input lead is gone — InputLeadController closes
            // the loop between these bounds.
            Assert.That(GetStaticField<int>(configType, "InitialInputLeadTicks"), Is.EqualTo(2));
            Assert.That(GetStaticField<int>(configType, "MaxInputLeadTicks"), Is.LessThan(
                GetStaticField<int>(configType, "MaxPredictionLeadTicks")));
            Assert.That(GetStaticField<int>(configType, "MaxPredictionLeadTicks"), Is.EqualTo(12));
            Assert.That(GetStaticField<int>(configType, "MaxLocalPredictionTicksPerFrame"), Is.EqualTo(5));
            Assert.That(GetStaticField<int>(configType, "MaxTicksToSendPerFrame"), Is.EqualTo(5));
            Assert.That(GetStaticField<int>(configType, "MaxPendingCommands"), Is.EqualTo(96));
        }

        [TestCase(-1f)]
        [TestCase(1f)]
        public void KeyboardTurningWhileMoving_PreservesFullForwardSpeed(float turnAxis)
        {
            Type motorType = RequireType("Arena.Input.LocalPlayerMotor");
            MethodInfo resolveAxes = RequireMethod(
                motorType,
                "ResolveKeyboardTurnAxes",
                typeof(Vector2));
            Vector2 axes = (Vector2)resolveAxes.Invoke(null, new object[] { new Vector2(turnAxis, 1f) })!;

            Assert.That(axes.x, Is.EqualTo(turnAxis).Within(PositionTolerance));
            Assert.That(axes.y, Is.EqualTo(1f).Within(PositionTolerance));

            Type predictionType = RequireType("Arena.Input.MovementPrediction");
            MethodInfo velocityFromIntent = RequireMethod(
                predictionType,
                "VelocityFromIntent",
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(float));
            float moveSpeed = GetStaticField<float>(predictionType, "MoveSpeed");
            Vector2 velocity = (Vector2)velocityFromIntent.Invoke(
                null,
                new object[] { axes.y, 0f, 0f, moveSpeed })!;

            Assert.That(velocity.magnitude, Is.EqualTo(moveSpeed).Within(PositionTolerance));
        }

        [Test]
        public void ClientPredictionConstants_MatchAuthoritativeServerMovementConstants()
        {
            Type movementPredictionType = RequireType("Arena.Input.MovementPrediction");
            Type movementNetcodeConfigType = RequireType("Arena.Input.MovementNetcodeConfig");

            string movementSource = ReadProjectText("server", "src", "movement.rs");
            string gameLoopSource = ReadProjectText("server", "src", "game_loop.rs");

            Assert.That(
                GetStaticField<float>(movementPredictionType, "MoveSpeed"),
                Is.EqualTo(ReadRustFloatConstant(movementSource, "MOVE_SPEED")).Within(PositionTolerance));
            Assert.That(
                GetStaticField<float>(movementPredictionType, "JumpVelocity"),
                Is.EqualTo(ReadRustFloatConstant(movementSource, "JUMP_VELOCITY")).Within(PositionTolerance));
            Assert.That(
                GetStaticField<float>(movementPredictionType, "Gravity"),
                Is.EqualTo(ReadRustFloatConstant(movementSource, "GRAVITY")).Within(PositionTolerance));
            Assert.That(
                GetStaticField<float>(movementPredictionType, "ZeroInputEpsilon"),
                Is.EqualTo(ReadRustFloatConstant(movementSource, "ZERO_INPUT_EPSILON")).Within(PositionTolerance));
            Assert.That(
                GetStaticField<float>(movementPredictionType, "MaxGroundedSnapDown"),
                Is.EqualTo(ReadRustFloatConstant(gameLoopSource, "MAX_GROUNDED_SNAP_DOWN")).Within(PositionTolerance));
            Assert.That(
                GetStaticField<int>(movementNetcodeConfigType, "FixedTickMilliseconds"),
                Is.EqualTo(ReadRustIntConstant(movementSource, "FIXED_TICK_MILLIS")));
        }

        private static object CreateInstance(string typeName)
            => Activator.CreateInstance(RequireType(typeName))
               ?? throw new InvalidOperationException($"Failed to create instance of {typeName}.");

        private static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(PositionTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(PositionTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(PositionTolerance));
        }

        private static void AssertQuaternion(Quaternion actual, Quaternion expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(PositionTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(PositionTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(PositionTolerance));
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(PositionTolerance));
        }

        private static object FindLocomotionModeOverride(Array overrides, string modeId)
        {
            foreach (object modeOverride in overrides)
            {
                string currentModeId = (string)RequireProperty(modeOverride.GetType(), "ModeIdOrEmpty").GetValue(modeOverride)!;
                if (string.Equals(currentModeId, modeId, StringComparison.Ordinal))
                    return modeOverride;
            }

            throw new AssertionException($"Expected locomotion mode override '{modeId}'.");
        }

        private static void AssertDirectionalClipSetHasAllDirections(object directionalClipSet)
        {
            Type directionalClipSetType = directionalClipSet.GetType();
            foreach (string fieldName in new[] { "n", "ne", "e", "se", "s", "sw", "w", "nw" })
            {
                Assert.That(
                    directionalClipSetType.GetField(fieldName)!.GetValue(directionalClipSet),
                    Is.Not.Null,
                    $"Directional clip '{fieldName}' should be authored.");
            }
        }

        private static void AssertHitReactionClipSet(
            object clipSet,
            string expectedForward,
            string expectedBack,
            string expectedLeft,
            string expectedRight,
            string context)
        {
            Assert.That(((AnimationClip)RequireProperty(clipSet.GetType(), "Forward").GetValue(clipSet)!).name, Is.EqualTo(expectedForward), context);
            Assert.That(((AnimationClip)RequireProperty(clipSet.GetType(), "Back").GetValue(clipSet)!).name, Is.EqualTo(expectedBack), context);
            Assert.That(((AnimationClip)RequireProperty(clipSet.GetType(), "Left").GetValue(clipSet)!).name, Is.EqualTo(expectedLeft), context);
            Assert.That(((AnimationClip)RequireProperty(clipSet.GetType(), "Right").GetValue(clipSet)!).name, Is.EqualTo(expectedRight), context);
            Assert.That((bool)RequireProperty(clipSet.GetType(), "IsComplete").GetValue(clipSet)!, Is.True, context);
        }

        private static ScriptableObject CreateMinimalExportableCombatAnimationSet(Type combatAnimationSetType)
        {
            ScriptableObject set = ScriptableObject.CreateInstance(combatAnimationSetType);
            AnimationClip requiredClip = new();
            combatAnimationSetType.GetField("staggerF")!.SetValue(set, requiredClip);
            combatAnimationSetType.GetField("staggerB")!.SetValue(set, requiredClip);
            combatAnimationSetType.GetField("staggerL")!.SetValue(set, requiredClip);
            combatAnimationSetType.GetField("staggerR")!.SetValue(set, requiredClip);
            RequireMethod(combatAnimationSetType, "EnsureMeleeAttackListSize", typeof(int))
                .Invoke(set, new object[] { 1 });
            return set;
        }

        private static object GetStaticProperty(string typeName, string propertyName)
            => RequireProperty(RequireType(typeName), propertyName).GetValue(null)
               ?? throw new InvalidOperationException($"Static property {typeName}.{propertyName} returned null.");

        private static object InvokeStaticMethod(string typeName, string methodName, params object[] args)
            => RequireMethod(RequireType(typeName), methodName, GetArgumentTypes(args)).Invoke(null, args)
               ?? throw new InvalidOperationException($"Static method {typeName}.{methodName} returned null.");

        private static object InvokeInstanceMethod(object target, string methodName, params object[] args)
        {
            MethodInfo method = RequireMethod(target.GetType(), methodName, GetArgumentTypes(args));
            object? result = method.Invoke(target, args);
            if (method.ReturnType == typeof(void))
                return VoidResult.Instance;

            return result
                   ?? throw new InvalidOperationException($"Instance method {target.GetType().FullName}.{methodName} returned null.");
        }

        private static object FindExportedStrike(Array strikes, string strikeId)
        {
            foreach (object strike in strikes)
            {
                string id = (string)strike.GetType().GetField("id")!.GetValue(strike)!;
                if (string.Equals(id, strikeId, StringComparison.Ordinal))
                    return strike;
            }

            throw new AssertionException($"Exported strike {strikeId} was not found.");
        }

        private static Array GetExportedStrikes(object export)
        {
            object profile = ((Array)export.GetType().GetField("profiles")!.GetValue(export)!).GetValue(0)!;
            return (Array)profile.GetType().GetField("strikes")!.GetValue(profile)!;
        }

        private static int GetFirstImpactDelayMs(object strike)
        {
            int[] impactDelayMs = GetImpactDelayMs(strike);
            Assert.That(impactDelayMs.Length, Is.GreaterThan(0));
            return impactDelayMs[0];
        }

        private static int[] GetImpactDelayMs(object strike)
        {
            Array hitWindows = (Array)strike.GetType().GetField("hit_windows")!.GetValue(strike)!;
            var impactDelayMs = new int[hitWindows.Length];
            for (int index = 0; index < hitWindows.Length; index++)
            {
                object hitWindow = hitWindows.GetValue(index)!;
                impactDelayMs[index] = (int)hitWindow.GetType().GetField("impact_delay_ms")!.GetValue(hitWindow)!;
            }

            return impactDelayMs;
        }

        private static void SetFirstMeleeAttackField(
            Type combatAnimationSetType,
            Type attackType,
            ScriptableObject set,
            string fieldName,
            object value)
        {
            System.Collections.IList attacks =
                (System.Collections.IList)combatAnimationSetType.GetField("meleeAttacks")!.GetValue(set)!;
            object attack = attacks[0]!;
            attackType.GetField(fieldName)!.SetValue(attack, value);
            attacks[0] = attack;
        }

        private static AnimationClip CreateClipWithLength(float lengthSeconds)
        {
            AnimationClip clip = new();
            clip.SetCurve(
                relativePath: string.Empty,
                type: typeof(Transform),
                propertyName: "localPosition.x",
                curve: AnimationCurve.Linear(0f, 0f, lengthSeconds, 1f));
            return clip;
        }

        private static object FindVisualBinding(Array visuals, Type visualBindingType, string itemId)
        {
            foreach (object visual in visuals)
            {
                string id = (string)visualBindingType.GetField("itemId")!.GetValue(visual)!;
                if (string.Equals(id, itemId, StringComparison.Ordinal))
                    return visual;
            }

            throw new AssertionException($"Visual binding {itemId} was not found.");
        }

        private static string BuildRelativePath(Transform root, Transform target)
        {
            if (target == root)
                return string.Empty;

            var segments = new System.Collections.Generic.Stack<string>();
            Transform? current = target;
            while (current != null && current != root)
            {
                segments.Push(current.name);
                current = current.parent;
            }

            Assert.That(current, Is.EqualTo(root), $"Transform '{target.name}' is not under '{root.name}'.");
            return string.Join("/", segments);
        }

        private static string ReadProjectText(params string[] relativeSegments)
        {
            string path = Path.Combine(ProjectRootPath, Path.Combine(relativeSegments));
            Assert.That(File.Exists(path), Is.True, $"Expected source file at {path}.");
            return File.ReadAllText(path);
        }

        private static float ReadRustFloatConstant(string source, string constantName)
        {
            string rawValue = ReadRustConstantValue(source, constantName);
            if (!float.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            {
                throw new InvalidOperationException($"Failed to parse float constant {constantName} value '{rawValue}'.");
            }

            return parsed;
        }

        private static int ReadRustIntConstant(string source, string constantName)
        {
            string rawValue = ReadRustConstantValue(source, constantName);
            if (!int.TryParse(rawValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                throw new InvalidOperationException($"Failed to parse int constant {constantName} value '{rawValue}'.");
            }

            return parsed;
        }

        private static string ReadRustConstantValue(string source, string constantName)
        {
            Match match = Regex.Match(
                source,
                $@"(?:pub\s+)?const\s+{Regex.Escape(constantName)}\s*:\s*[^=]+\=\s*([^;]+);",
                RegexOptions.Multiline);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Rust constant {constantName} not found.");
            }

            return match.Groups[1].Value.Trim();
        }

        private static Type[] GetArgumentTypes(object[] args)
        {
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                object arg = args[i];
                types[i] = arg.GetType();
            }

            return types;
        }

        private static Type RequireType(string fullName)
            => RuntimeAssembly.GetType(fullName)
               ?? AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName)).FirstOrDefault(type => type != null)
               ?? throw new InvalidOperationException($"Type {fullName} not found.");

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
            => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, parameterTypes, null)
               ?? throw new InvalidOperationException($"Method {type.FullName}.{name} not found.");

        private static PropertyInfo RequireProperty(Type type, string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
               ?? throw new InvalidOperationException($"Property {type.FullName}.{name} not found.");

        private static T GetStaticField<T>(Type type, string name)
            => (T)(type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                   ?? throw new InvalidOperationException($"Field {type.FullName}.{name} not found."));

        private sealed class VoidResult
        {
            public static readonly VoidResult Instance = new();

            private VoidResult() { }
        }
    }
}
