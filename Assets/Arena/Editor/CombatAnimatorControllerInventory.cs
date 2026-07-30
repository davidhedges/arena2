#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.Editor
{
    public static class CombatAnimatorControllerInventory
    {
        public const string ControllerPath = "Assets/Arena/Content/Animation/Arena_Character.controller";

        private static readonly string[] RequiredLayerOrder =
        {
            "Base Layer",
            "UpperBody",
            "HitReaction",
            "MeleeAttack",
            "SpellAction",
            "LeftGesture",
        };

        private static readonly HashSet<string> OwnedParameters = new(StringComparer.Ordinal)
        {
            "IsDead",
            "Jump",
            "Grounded",
            "FreeFall",
            "MotionSpeed",
            "InCombat",
            "VelocityX",
            "VelocityZ",
            "TriggerHitF",
            "TriggerHitB",
            "TriggerHitL",
            "TriggerHitR",
            "TriggerStrike1",
            "TriggerStrike2",
            "TriggerStrike3",
            "TriggerStrike4",
            "TriggerSpellAction1",
            "TriggerSpellAction2",
            "TriggerSpellAction3",
            "TriggerSpellAction4",
            "StopX",
            "StopZ",
            "JumpX",
            "JumpZ",
            "TurnAngle",
            "TriggerWalkStop",
            "TriggerRunStop",
            "TriggerTurn",
            "TriggerBlockStart",
            "IsBlocking",
            "TriggerBlockHit",
            "TriggerKnockdown",
            "IsKnockedDown",
            "TriggerGetUp",
            "TriggerDodge",
            "DodgeX",
            "DodgeZ",
            "DodgePhase",
            "TriggerStaggerF",
            "TriggerStaggerB",
            "TriggerStaggerL",
            "TriggerStaggerR",
            "IsHardCrowdControlled",
            "TriggerHardCrowdControl",
            "TriggerParryHit",
        };

        private static readonly string[] OwnershipNotes =
        {
            "IsHardCrowdControlled, TriggerHardCrowdControl, Base Layer/HardCrowdControlLoop, and slot_hard_crowd_control_loop are the hard crowd-control loop path. Runtime APIs should use SetHardCrowdControl(statusKind), not stun-specific aliases.",
        };

        private static readonly HashSet<string> LegacyRetainedParameters = new(StringComparer.Ordinal)
        {
            "IsCasting",
            "TriggerCastDefault",
            "TriggerCastUp",
            "TriggerCastInterrupt",
        };

        private static readonly HashSet<string> CandidateDeleteParameters = new(StringComparer.Ordinal)
        {
            "IsPhasedMeleeActive",
            "TriggerPhasedMeleeStart",
            "TriggerPhasedMeleeEnd",
        };

        private static readonly HashSet<string> OwnedStatePaths = new(StringComparer.Ordinal)
        {
            "Base Layer/BlockEnd",
            "Base Layer/BlockHit",
            "Base Layer/BlockLoop",
            "Base Layer/BlockStart",
            "Base Layer/Death",
            "Base Layer/Dodge",
            "Base Layer/DrawWeapon",
            "Base Layer/EnterCombatIdle",
            "Base Layer/EnterCombatRun",
            "Base Layer/EnterCombatWalk",
            "Base Layer/ExitCombatIdle",
            "Base Layer/ExitCombatRun",
            "Base Layer/ExitCombatWalk",
            "Base Layer/GetUp",
            "Base Layer/Idle Walk Run Blend",
            "Base Layer/IdleCombat",
            "Base Layer/InAir",
            "Base Layer/InAirCombat",
            "Base Layer/JumpLand",
            "Base Layer/JumpLandCombat",
            "Base Layer/JumpStart",
            "Base Layer/JumpStartCombat",
            "Base Layer/KnockdownLoop",
            "Base Layer/KnockdownStart",
            "Base Layer/ParryHit",
            "Base Layer/RunStop",
            "Base Layer/RunStopCombat",
            "Base Layer/SheathWeapon",
            "Base Layer/StaggerB",
            "Base Layer/StaggerF",
            "Base Layer/StaggerL",
            "Base Layer/StaggerR",
            "Base Layer/HardCrowdControlLoop",
            "Base Layer/Turn",
            "Base Layer/TurnCombat",
            "Base Layer/WalkStop",
            "Base Layer/WalkStopCombat",
            "HitReaction/Empty",
            "HitReaction/HitB",
            "HitReaction/HitF",
            "HitReaction/HitL",
            "HitReaction/HitR",
            "MeleeAttack/Empty",
            "MeleeAttack/Strike1",
            "MeleeAttack/Strike2",
            "MeleeAttack/Strike3",
            "MeleeAttack/Strike4",
            "SpellAction/Empty",
            "SpellAction/SpellAction1",
            "SpellAction/SpellAction2",
            "SpellAction/SpellAction3",
            "SpellAction/SpellAction4",
            "SpellAction/SpellCastHoldAction1",
            "SpellAction/SpellCastHoldAction2",
            "SpellAction/SpellCastHoldAction3",
            "SpellAction/SpellCastHoldAction4",
            "UpperBody/Empty",
            "UpperBody/UpperBodyBlockEnd",
            "UpperBody/UpperBodyBlockHit",
            "UpperBody/UpperBodyBlockLoop",
            "UpperBody/UpperBodyBlockStart",
            "UpperBody/UpperBodyDrawWeapon",
            "UpperBody/UpperBodyRecoveryAction1",
            "UpperBody/UpperBodySheathWeapon",
            "UpperBody/UpperBodySpellAction1",
            "UpperBody/UpperBodySpellAction2",
            "UpperBody/UpperBodySpellAction3",
            "UpperBody/UpperBodySpellAction4",
            "LeftGesture/Empty",
            "LeftGesture/LeftGestureSpellAction1",
            "LeftGesture/LeftGestureSpellAction2",
            "LeftGesture/LeftGestureSpellAction3",
            "LeftGesture/LeftGestureSpellAction4",
        };

        private static readonly HashSet<string> LegacyRetainedStatePaths = new(StringComparer.Ordinal)
        {
            "Base Layer/BlockHitBreak",
            "UpperBody/CastDefault",
            "UpperBody/CastUp",
        };

        private static readonly HashSet<string> CandidateDeleteStatePaths = new(StringComparer.Ordinal)
        {
            "Base Layer/PhasedMeleeEnd",
            "Base Layer/PhasedMeleeLoop",
            "Base Layer/PhasedMeleeStart",
        };

        public readonly struct StateRecord
        {
            public StateRecord(string layerName, string path, string stateName, string motionName)
            {
                LayerName = layerName;
                Path = path;
                StateName = stateName;
                MotionName = motionName;
            }

            public string LayerName { get; }
            public string Path { get; }
            public string StateName { get; }
            public string MotionName { get; }
        }

        public sealed class Report
        {
            public Report(
                string[] layers,
                string[] parameters,
                StateRecord[] states,
                string[] legacyRetainedParameters,
                string[] candidateDeleteParameters,
                string[] unclassifiedParameters,
                string[] legacyRetainedStates,
                string[] candidateDeleteStates,
                string[] unclassifiedStates,
                string[] issues)
            {
                Layers = layers;
                Parameters = parameters;
                States = states;
                LegacyRetainedParameters = legacyRetainedParameters;
                CandidateDeleteParameters = candidateDeleteParameters;
                UnclassifiedParameters = unclassifiedParameters;
                LegacyRetainedStates = legacyRetainedStates;
                CandidateDeleteStates = candidateDeleteStates;
                UnclassifiedStates = unclassifiedStates;
                Issues = issues;
            }

            public string[] Layers { get; }
            public string[] Parameters { get; }
            public StateRecord[] States { get; }
            public string[] LegacyRetainedParameters { get; }
            public string[] CandidateDeleteParameters { get; }
            public string[] UnclassifiedParameters { get; }
            public string[] LegacyRetainedStates { get; }
            public string[] CandidateDeleteStates { get; }
            public string[] UnclassifiedStates { get; }
            public string[] Issues { get; }
        }

        [MenuItem("Arena/Animation/Print Combat Controller Inventory")]
        public static void PrintDefaultInventory()
        {
            Report report = BuildDefaultReport();
            Debug.Log(FormatReport(report));
        }

        public static Report BuildDefaultReport()
        {
            AnimatorController? controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                return new Report(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<StateRecord>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new[] { $"{ControllerPath} could not be loaded." });
            }

            return BuildReport(controller);
        }

        public static Report BuildReport(AnimatorController controller)
        {
            var states = new List<StateRecord>();
            string[] layers = controller.layers.Select(layer => layer.name ?? string.Empty).ToArray();
            string[] parameters = controller.parameters.Select(parameter => parameter.name ?? string.Empty).ToArray();

            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                string layerName = string.IsNullOrWhiteSpace(layer.name)
                    ? $"<unnamed layer {layerIndex}>"
                    : layer.name;
                CollectStates(layer.stateMachine, layerName, string.Empty, states);
            }

            return new Report(
                layers,
                parameters,
                states.ToArray(),
                parameters.Where(LegacyRetainedParameters.Contains).OrderBy(parameter => parameter).ToArray(),
                parameters.Where(CandidateDeleteParameters.Contains).OrderBy(parameter => parameter).ToArray(),
                parameters.Where(IsUnclassifiedParameter).OrderBy(parameter => parameter).ToArray(),
                states.Select(GetFullStatePath).Where(LegacyRetainedStatePaths.Contains).OrderBy(path => path).ToArray(),
                states.Select(GetFullStatePath).Where(CandidateDeleteStatePaths.Contains).OrderBy(path => path).ToArray(),
                states.Select(GetFullStatePath).Where(IsUnclassifiedState).OrderBy(path => path).ToArray(),
                BuildIssues(layers, parameters, states).ToArray());
        }

        public static string FormatReport(Report report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[CombatAnimatorControllerInventory]");
            builder.AppendLine($"Layers ({report.Layers.Length}): {string.Join(", ", report.Layers)}");
            builder.AppendLine($"Parameters ({report.Parameters.Length}): {string.Join(", ", report.Parameters)}");
            builder.AppendLine($"States ({report.States.Length}):");
            foreach (StateRecord state in report.States.OrderBy(state => state.LayerName).ThenBy(state => state.Path))
                builder.AppendLine($"- {state.LayerName}/{state.Path} motion={state.MotionName}");
            builder.AppendLine("Ownership:");
            foreach (string note in OwnershipNotes)
                builder.AppendLine($"- note: {note}");
            builder.AppendLine($"- legacy-retained parameters ({report.LegacyRetainedParameters.Length}): {FormatList(report.LegacyRetainedParameters)}");
            builder.AppendLine($"- candidate-delete parameters ({report.CandidateDeleteParameters.Length}): {FormatList(report.CandidateDeleteParameters)}");
            builder.AppendLine($"- unclassified parameters ({report.UnclassifiedParameters.Length}): {FormatList(report.UnclassifiedParameters)}");
            builder.AppendLine($"- legacy-retained states ({report.LegacyRetainedStates.Length}): {FormatList(report.LegacyRetainedStates)}");
            builder.AppendLine($"- candidate-delete states ({report.CandidateDeleteStates.Length}): {FormatList(report.CandidateDeleteStates)}");
            builder.AppendLine($"- unclassified states ({report.UnclassifiedStates.Length}): {FormatList(report.UnclassifiedStates)}");
            if (report.Issues.Length > 0)
            {
                builder.AppendLine("Issues:");
                foreach (string issue in report.Issues)
                    builder.AppendLine($"- {issue}");
            }

            return builder.ToString();
        }

        private static void CollectStates(
            AnimatorStateMachine stateMachine,
            string layerName,
            string parentPath,
            List<StateRecord> states)
        {
            foreach (ChildAnimatorState child in stateMachine.states)
            {
                if (child.state == null)
                    continue;

                string stateName = child.state.name ?? string.Empty;
                string path = string.IsNullOrEmpty(parentPath) ? stateName : $"{parentPath}/{stateName}";
                string motionName = child.state.motion != null ? child.state.motion.name : "<none>";
                states.Add(new StateRecord(layerName, path, stateName, motionName));
            }

            foreach (ChildAnimatorStateMachine childMachine in stateMachine.stateMachines)
            {
                if (childMachine.stateMachine == null)
                    continue;

                string machineName = childMachine.stateMachine.name ?? string.Empty;
                string path = string.IsNullOrEmpty(parentPath) ? machineName : $"{parentPath}/{machineName}";
                CollectStates(childMachine.stateMachine, layerName, path, states);
            }
        }

        private static string FormatList(IReadOnlyList<string> values)
        {
            return values.Count == 0 ? "<none>" : string.Join(", ", values);
        }

        private static string GetFullStatePath(StateRecord state)
        {
            return $"{state.LayerName}/{state.Path}";
        }

        private static bool IsUnclassifiedParameter(string parameter)
        {
            return !OwnedParameters.Contains(parameter)
                   && !LegacyRetainedParameters.Contains(parameter)
                   && !CandidateDeleteParameters.Contains(parameter);
        }

        private static bool IsUnclassifiedState(string fullStatePath)
        {
            return !OwnedStatePaths.Contains(fullStatePath)
                   && !LegacyRetainedStatePaths.Contains(fullStatePath)
                   && !CandidateDeleteStatePaths.Contains(fullStatePath);
        }

        private static IEnumerable<string> BuildIssues(
            string[] layers,
            string[] parameters,
            IReadOnlyList<StateRecord> states)
        {
            for (int i = 0; i < RequiredLayerOrder.Length; i++)
            {
                if (layers.Length <= i || !string.Equals(layers[i], RequiredLayerOrder[i], StringComparison.Ordinal))
                    yield return $"Layer {i} should be '{RequiredLayerOrder[i]}'.";
            }

            for (int i = 0; i < layers.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(layers[i]))
                    yield return $"Layer {i} is unnamed.";
            }

            foreach (string parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter))
                    yield return "Animator has an unnamed parameter.";
                else if (IsUnclassifiedParameter(parameter))
                    yield return $"Animator parameter '{parameter}' has no Phase 10 ownership classification.";
            }

            foreach (IGrouping<string, StateRecord> layerGroup in states.GroupBy(state => state.LayerName))
            {
                foreach (IGrouping<string, StateRecord> duplicateGroup in layerGroup.GroupBy(state => state.StateName))
                {
                    if (string.IsNullOrWhiteSpace(duplicateGroup.Key))
                    {
                        yield return $"Layer '{layerGroup.Key}' has an unnamed state.";
                        continue;
                    }

                    if (duplicateGroup.Count() > 1)
                        yield return $"Layer '{layerGroup.Key}' has duplicate state '{duplicateGroup.Key}'.";
                }
            }

            foreach (StateRecord state in states)
            {
                string fullStatePath = GetFullStatePath(state);
                if (IsUnclassifiedState(fullStatePath))
                    yield return $"Animator state '{fullStatePath}' has no Phase 10 ownership classification.";
            }
        }
    }
}
