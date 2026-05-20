#nullable enable

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class OpenWorldCameraRigAuthoringTests
    {
        private const string CinemachineBrainGuid = "72ece51f2901e7445ab60da3685d6b5f";
        private const string CinemachineVirtualCameraGuid = "45e653bab7fb20e499bda25e1b646fea";
        private const string OpenWorldSceneRoot = "Arena/Content/Scenes/OpenWorld";

        [Test]
        public void RegisteredOpenWorldScenes_AuthorGameplayCameraRig()
        {
            foreach (string sceneName in RegisteredOpenWorldSceneNames())
            {
                string scenePath = Path.Combine(
                    Application.dataPath,
                    OpenWorldSceneRoot,
                    $"{sceneName}.unity");
                Assert.That(File.Exists(scenePath), Is.True, $"{sceneName} scene asset is missing.");

                string yaml = File.ReadAllText(scenePath);
                Assert.That(
                    TryFindGameObjectId(yaml, "m_TagString: MainCamera", out long mainCameraId),
                    Is.True,
                    $"{sceneName} must have a tagged MainCamera.");
                Assert.That(
                    HasMonoBehaviourOnGameObject(yaml, CinemachineBrainGuid, mainCameraId),
                    Is.True,
                    $"{sceneName} MainCamera must have CinemachineBrain.");
                Assert.That(
                    TryFindGameObjectId(yaml, "m_Name: PlayerFollowCamera", out long playerFollowCameraId),
                    Is.True,
                    $"{sceneName} must author PlayerFollowCamera.");
                Assert.That(
                    HasMonoBehaviourOnGameObject(yaml, CinemachineVirtualCameraGuid, playerFollowCameraId),
                    Is.True,
                    $"{sceneName} PlayerFollowCamera must have a Cinemachine follow camera.");
                Assert.That(yaml, Does.Contain("CameraDistance: 6"), $"{sceneName} PlayerFollowCamera must start at third-person distance.");
            }
        }

        private static bool TryFindGameObjectId(string yaml, string requiredLine, out long fileId)
        {
            foreach (string block in Regex.Split(yaml, "(?=--- !u!)"))
            {
                Match header = Regex.Match(block, @"^--- !u!1 &(\d+)", RegexOptions.Multiline);
                if (!header.Success || !block.Contains(requiredLine))
                    continue;

                fileId = long.Parse(header.Groups[1].Value);
                return true;
            }

            fileId = 0;
            return false;
        }

        private static bool HasMonoBehaviourOnGameObject(string yaml, string scriptGuid, long gameObjectId)
        {
            foreach (string block in Regex.Split(yaml, "(?=--- !u!)"))
            {
                if (!block.Contains($"guid: {scriptGuid}"))
                    continue;

                if (block.Contains($"m_GameObject: {{fileID: {gameObjectId}}}"))
                    return true;
            }

            return false;
        }

        private static string[] RegisteredOpenWorldSceneNames()
        {
            Assembly runtimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
            Type catalogType = runtimeAssembly.GetType("Arena.World.OpenWorldTravelCatalog", throwOnError: true)!;
            PropertyInfo allProperty = catalogType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!;
            IEnumerable destinations = (IEnumerable)allProperty.GetValue(null)!;
            var names = new System.Collections.Generic.List<string>();
            foreach (object destination in destinations)
            {
                PropertyInfo sceneName = destination.GetType().GetProperty("SceneName")!;
                names.Add((string)sceneName.GetValue(destination)!);
            }

            return names.ToArray();
        }
    }
}
