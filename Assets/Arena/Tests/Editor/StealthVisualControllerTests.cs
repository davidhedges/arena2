#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Arena.Tests.Editor
{
    public sealed class StealthVisualControllerTests
    {
        [Test]
        public void SetStealthed_OverridesAndRestoresRendererMaterials()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material source = CreateLitMaterial();
            try
            {
                Renderer renderer = root.GetComponent<Renderer>();
                renderer.sharedMaterial = source;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                Component controller = CreateController(root);
                Invoke(controller, "RefreshRenderers", (object)new[] { renderer });

                Invoke(controller, "SetStealthed", true, true);

                Assert.That(ReadProperty<bool>(controller, "IsStealthed"), Is.True);
                Assert.That(ReadProperty<bool>(controller, "HasMaterialOverrides"), Is.True);
                Assert.That(renderer.sharedMaterial, Is.Not.SameAs(source));
                Assert.That(ReadAlpha(renderer.sharedMaterial), Is.EqualTo(0.4f).Within(0.001f));
                Assert.That(renderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(renderer.sharedMaterial.GetFloat("_Surface"), Is.EqualTo(1f));
                Assert.That(renderer.sharedMaterial.GetFloat("_ZWrite"), Is.EqualTo(0f));
                Assert.That(renderer.sharedMaterial.GetShaderPassEnabled("DepthOnly"), Is.False);

                Invoke(controller, "SetStealthed", false, true);

                Assert.That(ReadProperty<bool>(controller, "HasMaterialOverrides"), Is.False);
                Assert.That(renderer.sharedMaterial, Is.SameAs(source));
                Assert.That(renderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void RefreshRenderers_WhileStealthedRestoresOldAndOverridesReplacement()
        {
            GameObject root = new("StealthVisualControllerTest");
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject replacement = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material firstSource = CreateLitMaterial();
            Material replacementSource = CreateLitMaterial();
            try
            {
                first.transform.SetParent(root.transform);
                replacement.transform.SetParent(root.transform);
                Renderer firstRenderer = first.GetComponent<Renderer>();
                Renderer replacementRenderer = replacement.GetComponent<Renderer>();
                firstRenderer.sharedMaterial = firstSource;
                replacementRenderer.sharedMaterial = replacementSource;
                Component controller = CreateController(root);
                Invoke(controller, "RefreshRenderers", (object)new[] { firstRenderer });
                Invoke(controller, "SetStealthed", true, true);

                Invoke(controller, "RefreshRenderers", (object)new[] { replacementRenderer });

                Assert.That(firstRenderer.sharedMaterial, Is.SameAs(firstSource));
                Assert.That(replacementRenderer.sharedMaterial, Is.Not.SameAs(replacementSource));
                Assert.That(ReadAlpha(replacementRenderer.sharedMaterial), Is.EqualTo(0.4f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(firstSource);
                Object.DestroyImmediate(replacementSource);
            }
        }

        private static Material CreateLitMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            return new Material(shader);
        }

        private static Component CreateController(GameObject root)
        {
            Type type = RequireRuntimeType("Arena.Presentation.Appearance.StealthVisualController");
            return root.AddComponent(type);
        }

        private static Type RequireRuntimeType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }

            throw new AssertionException($"Runtime type '{fullName}' was not loaded.");
        }

        private static void Invoke(Component component, string methodName, params object[] arguments)
        {
            MethodInfo? method = component.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Missing method '{methodName}'.");
            method!.Invoke(component, arguments);
        }

        private static T ReadProperty<T>(Component component, string propertyName)
        {
            PropertyInfo? property = component.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, $"Missing property '{propertyName}'.");
            return (T)property!.GetValue(component)!;
        }

        private static float ReadAlpha(Material material)
        {
            if (material.HasProperty("_BaseColor"))
                return material.GetColor("_BaseColor").a;
            return material.GetColor("_Color").a;
        }
    }
}
