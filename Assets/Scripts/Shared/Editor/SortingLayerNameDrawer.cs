#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector dropdown for fields marked with SortingLayerNameAttribute.
/// Reads the Sorting Layers configured in Project Settings so names cannot be mistyped.
/// </summary>
[CustomPropertyDrawer(typeof(SortingLayerNameAttribute))]
public sealed class SortingLayerNameDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        SortingLayer[] layers = SortingLayer.layers;

        if (layers == null || layers.Length == 0)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        string[] names = new string[layers.Length];
        int currentIndex = 0;

        for (int i = 0; i < layers.Length; i++)
        {
            names[i] = layers[i].name;

            if (layers[i].name == property.stringValue)
                currentIndex = i;
        }

        EditorGUI.BeginProperty(position, label, property);
        int selectedIndex = EditorGUI.Popup(position, label.text, currentIndex, names);
        property.stringValue = names[Mathf.Clamp(selectedIndex, 0, names.Length - 1)];
        EditorGUI.EndProperty();
    }
}
#endif
