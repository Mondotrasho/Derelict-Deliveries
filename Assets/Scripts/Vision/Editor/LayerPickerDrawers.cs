using System.Linq;
using UnityEditor;
using UnityEngine;

// Editor-only drawers for [SortingLayerPicker] and [LayerPicker].
// Must live in an Editor folder (Assets/Scripts/Vision/Editor/).

[CustomPropertyDrawer(typeof(SortingLayerPickerAttribute))]
public sealed class SortingLayerPickerDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.Integer)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        SortingLayer[] layers = SortingLayer.layers;
        string[] names = layers.Select(l => l.name).ToArray();
        int index = System.Array.FindIndex(layers, l => l.id == property.intValue);
        if (index < 0) index = 0;   // missing / deleted layer -> Default

        EditorGUI.BeginProperty(position, label, property);
        int picked = EditorGUI.Popup(position, label.text, index, names);
        property.intValue = layers.Length > 0 ? layers[picked].id : 0;
        EditorGUI.EndProperty();
    }
}

[CustomPropertyDrawer(typeof(LayerPickerAttribute))]
public sealed class LayerPickerDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.Integer)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        EditorGUI.BeginProperty(position, label, property);
        property.intValue = EditorGUI.LayerField(position, label, property.intValue);
        EditorGUI.EndProperty();
    }
}
