#nullable enable

using UnityEditor;
using UnityEngine;
using Arena.Presentation;

namespace Arena.Editor
{
    [CustomPropertyDrawer(typeof(WeaponStrikeCombatAuthoring))]
    public sealed class WeaponStrikeCombatAuthoringDrawer : PropertyDrawer
    {
        private const float SectionSpacing = 4f;
        private const string HitWindowGuidance =
            "Hit windows are authored with OnStrikeHit events in Arena/Animation/Event Stamper. " +
            "This read-only array is synchronized for compatibility.";

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            Rect current = new(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            DrawField(ref current, property, "id");
            DrawField(ref current, property, "slotId");
            using (new EditorGUI.DisabledScope(true))
                DrawField(ref current, property, "hitWindows", true, "Hit Windows (Event Mirror)");
            DrawHelpBox(ref current, HitWindowGuidance);
            DrawField(ref current, property, "recoveryMs");
            DrawField(ref current, property, "comboFrom");
            DrawField(ref current, property, "comboOpenMs");
            DrawField(ref current, property, "comboGraceMs");
            current.y += SectionSpacing;
            DrawSectionLabel(ref current, "Caster Requirement");
            using (new EditorGUI.IndentLevelScope())
            {
                DrawField(ref current, property, "aerialExecutionMode", labelOverride: "Caster Movement State");
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float total = 0f;

            total += FieldHeight(property, "id");
            total += FieldHeight(property, "slotId");
            total += FieldHeight(property, "hitWindows", true);
            total += HelpBoxHeight(HitWindowGuidance);
            total += FieldHeight(property, "recoveryMs");
            total += FieldHeight(property, "comboFrom");
            total += FieldHeight(property, "comboOpenMs");
            total += FieldHeight(property, "comboGraceMs");
            total += SectionSpacing + EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            total += FieldHeight(property, "aerialExecutionMode");

            return total - EditorGUIUtility.standardVerticalSpacing;
        }

        private static void DrawField(
            ref Rect current,
            SerializedProperty parent,
            string childName,
            bool includeChildren = false,
            string? labelOverride = null)
        {
            SerializedProperty? child = parent.FindPropertyRelative(childName);
            if (child == null)
                return;

            float height = EditorGUI.GetPropertyHeight(child, includeChildren);
            current.height = height;
            if (string.IsNullOrWhiteSpace(labelOverride))
                EditorGUI.PropertyField(current, child, includeChildren);
            else
                EditorGUI.PropertyField(current, child, new GUIContent(labelOverride), includeChildren);
            current.y += height + EditorGUIUtility.standardVerticalSpacing;
            current.height = EditorGUIUtility.singleLineHeight;
        }

        private static void DrawSectionLabel(ref Rect current, string text)
        {
            current.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.LabelField(current, text, EditorStyles.boldLabel);
            current.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
        }

        private static void DrawHelpBox(ref Rect current, string text)
        {
            current.height = HelpBoxHeight(text) - EditorGUIUtility.standardVerticalSpacing;
            EditorGUI.HelpBox(current, text, MessageType.Info);
            current.y += current.height + EditorGUIUtility.standardVerticalSpacing;
            current.height = EditorGUIUtility.singleLineHeight;
        }

        private static float HelpBoxHeight(string text)
        {
            float contentHeight = EditorStyles.helpBox.CalcHeight(
                new GUIContent(text),
                Mathf.Max(100f, EditorGUIUtility.currentViewWidth - 60f));
            return Mathf.Max(EditorGUIUtility.singleLineHeight * 2f, contentHeight)
                + EditorGUIUtility.standardVerticalSpacing;
        }

        private static float FieldHeight(
            SerializedProperty parent,
            string childName,
            bool includeChildren = false)
        {
            SerializedProperty? child = parent.FindPropertyRelative(childName);
            if (child == null)
                return 0f;

            return EditorGUI.GetPropertyHeight(child, includeChildren)
                + EditorGUIUtility.standardVerticalSpacing;
        }
    }
}
