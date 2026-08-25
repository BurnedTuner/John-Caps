using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom inspector drawer for CapDeckDefinition.ComposedCapEntry.
///
/// Without this, Unity's default array/list inspector sometimes renders the
/// [Range(0,3)] int fields inside the struct as plain int fields (no slider).
/// This is a known Unity quirk — PropertyAttributes on fields inside a struct
/// that's inside an array aren't always propagated to the inspector.
///
/// This drawer forces the slider rendering, ensuring the BombLevel / FlipperLevel /
/// DefenderLevel / PredictorLevel fields always show as 0-3 sliders in the inspector.
///
/// Place this script in an Editor folder (any folder named "Editor").
/// </summary>
[CustomPropertyDrawer(typeof(CapDeckDefinition.ComposedCapEntry))]
public class ComposedCapEntryDrawer : PropertyDrawer
{
    const float LineHeight = 18f;
    const float Spacing = 2f;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        // BasePrefab (1 line) + 4 ability sliders (4 lines) = 5 lines total.
        return (LineHeight + Spacing) * 5;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        // Draw the foldout label (e.g., "Element 0") — but since we're drawing
        // a custom layout, we draw the fields directly without a foldout.
        // The label is shown on the first line.

        SerializedProperty basePrefab = property.FindPropertyRelative("BasePrefab");
        SerializedProperty bombLevel = property.FindPropertyRelative("BombLevel");
        SerializedProperty flipperLevel = property.FindPropertyRelative("FlipperLevel");
        SerializedProperty defenderLevel = property.FindPropertyRelative("DefenderLevel");
        SerializedProperty predictorLevel = property.FindPropertyRelative("PredictorLevel");

        float y = position.y;
        float labelWidth = 90f;
        float fieldX = position.x + labelWidth;
        float fieldWidth = position.width - labelWidth;

        // BasePrefab
        Rect baseRect = new Rect(position.x, y, position.width, LineHeight);
        EditorGUI.LabelField(new Rect(position.x, y, labelWidth, LineHeight), "Base Prefab");
        EditorGUI.PropertyField(new Rect(fieldX, y, fieldWidth, LineHeight), basePrefab, GUIContent.none);
        y += LineHeight + Spacing;

        // BombLevel (slider 0-3)
        EditorGUI.LabelField(new Rect(position.x, y, labelWidth, LineHeight), "Bomb");
        bombLevel.intValue = EditorGUI.IntSlider(new Rect(fieldX, y, fieldWidth, LineHeight), bombLevel.intValue, 0, 3);
        y += LineHeight + Spacing;

        // FlipperLevel (slider 0-3)
        EditorGUI.LabelField(new Rect(position.x, y, labelWidth, LineHeight), "Flipper");
        flipperLevel.intValue = EditorGUI.IntSlider(new Rect(fieldX, y, fieldWidth, LineHeight), flipperLevel.intValue, 0, 3);
        y += LineHeight + Spacing;

        // DefenderLevel (slider 0-3)
        EditorGUI.LabelField(new Rect(position.x, y, labelWidth, LineHeight), "Defender");
        defenderLevel.intValue = EditorGUI.IntSlider(new Rect(fieldX, y, fieldWidth, LineHeight), defenderLevel.intValue, 0, 3);
        y += LineHeight + Spacing;

        // PredictorLevel (slider 0-3)
        EditorGUI.LabelField(new Rect(position.x, y, labelWidth, LineHeight), "Predictor");
        predictorLevel.intValue = EditorGUI.IntSlider(new Rect(fieldX, y, fieldWidth, LineHeight), predictorLevel.intValue, 0, 3);

        EditorGUI.EndProperty();
    }
}
