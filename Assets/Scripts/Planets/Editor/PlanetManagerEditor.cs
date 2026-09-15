using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Custom Inspector for PlanetManager.
///
/// The main reason this exists is the planet-tile dropdown. Each Planet stores
/// an integer tile index, but the Inspector displays the actual names from the
/// manager's Planet Tile Library.
/// </summary>
[CustomEditor(typeof(PlanetManager))]
public class PlanetManagerEditor : Editor
{
    private SerializedProperty planetTiles;
    private SerializedProperty planets;

    private SerializedProperty sortingLayerName;
    private SerializedProperty sortingOrder;

    private SerializedProperty showEditorPreview;
    private SerializedProperty editorPreviewOpacity;

    private SerializedProperty animatePlanets;
    private SerializedProperty wiggleAmount;
    private SerializedProperty wiggleSpeed;
    private SerializedProperty skewAmount;
    private SerializedProperty skewSpeed;
    private SerializedProperty rotationWiggle;

    private ReorderableList planetList;


    private void OnEnable()
    {
        planetTiles =
            serializedObject.FindProperty("planetTiles");

        planets =
            serializedObject.FindProperty("planets");

        sortingLayerName =
            serializedObject.FindProperty("sortingLayerName");

        sortingOrder =
            serializedObject.FindProperty("sortingOrder");

        showEditorPreview =
            serializedObject.FindProperty("showEditorPreview");

        editorPreviewOpacity =
            serializedObject.FindProperty("editorPreviewOpacity");

        animatePlanets =
            serializedObject.FindProperty("animatePlanets");

        wiggleAmount =
            serializedObject.FindProperty("wiggleAmount");

        wiggleSpeed =
            serializedObject.FindProperty("wiggleSpeed");

        skewAmount =
            serializedObject.FindProperty("skewAmount");

        skewSpeed =
            serializedObject.FindProperty("skewSpeed");

        rotationWiggle =
            serializedObject.FindProperty("rotationWiggle");

        BuildPlanetList();
    }


    /// <summary>
    /// VisionManager modifies nested Planet visibility values at runtime.
    /// Keep this custom Inspector repainting during Play mode so the checkboxes
    /// visibly follow the live state.
    /// </summary>
    public override bool RequiresConstantRepaint()
    {
        return Application.isPlaying;
    }


    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUI.BeginChangeCheck();

        DrawTileLibrary();

        EditorGUILayout.Space(8f);

        DrawPlanetPlacements();

        EditorGUILayout.Space(8f);

        DrawRendering();

        EditorGUILayout.Space(8f);

        DrawEditorPreview();

        EditorGUILayout.Space(8f);

        DrawAnimation();

        bool changed =
            EditorGUI.EndChangeCheck();

        serializedObject.ApplyModifiedProperties();

        if (changed)
        {
            PlanetManager manager =
                (PlanetManager)target;

            // Queue the change. PlanetManager performs hierarchy/Tilemap work
            // later from its safe Update path.
            manager.RequestRefresh();

            EditorUtility.SetDirty(manager);

            // Ensure ExecuteAlways gets another editor update for previews.
            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }
    }


    private void DrawTileLibrary()
    {
        EditorGUILayout.LabelField(
            "Planet Tile Library",
            EditorStyles.boldLabel
        );

        EditorGUILayout.PropertyField(
            planetTiles,
            includeChildren: true
        );
    }


    private void DrawPlanetPlacements()
    {
        EditorGUILayout.LabelField(
            "Planet Placements",
            EditorStyles.boldLabel
        );

        planetList.DoLayoutList();
    }


    private void DrawRendering()
    {
        EditorGUILayout.LabelField(
            "Rendering",
            EditorStyles.boldLabel
        );

        EditorGUILayout.PropertyField(
            sortingLayerName
        );

        EditorGUILayout.PropertyField(
            sortingOrder
        );
    }


    private void DrawEditorPreview()
    {
        EditorGUILayout.LabelField(
            "Editor Placement Preview",
            EditorStyles.boldLabel
        );

        EditorGUILayout.PropertyField(
            showEditorPreview
        );

        if (showEditorPreview.boolValue)
        {
            EditorGUILayout.PropertyField(
                editorPreviewOpacity
            );
        }
    }


    private void DrawAnimation()
    {
        EditorGUILayout.LabelField(
            "Planet Animation",
            EditorStyles.boldLabel
        );

        EditorGUILayout.PropertyField(
            animatePlanets
        );

        if (!animatePlanets.boolValue)
            return;

        EditorGUILayout.PropertyField(
            wiggleAmount
        );

        EditorGUILayout.PropertyField(
            wiggleSpeed
        );

        EditorGUILayout.PropertyField(
            skewAmount
        );

        EditorGUILayout.PropertyField(
            skewSpeed
        );

        EditorGUILayout.PropertyField(
            rotationWiggle
        );
    }


    private void BuildPlanetList()
    {
        planetList =
            new ReorderableList(
                serializedObject,
                planets,
                draggable: true,
                displayHeader: true,
                displayAddButton: true,
                displayRemoveButton: true
            );

        planetList.drawHeaderCallback =
            rect =>
            {
                EditorGUI.LabelField(
                    rect,
                    "Planets"
                );
            };

        planetList.elementHeightCallback =
            index =>
            {
                SerializedProperty element =
                    planets.GetArrayElementAtIndex(index);

                float lineHeight =
                    EditorGUIUtility.singleLineHeight;

                if (!element.isExpanded)
                    return lineHeight + 8f;

                // Fixed rows:
                // foldout, id, display name, cell, tile,
                // visibility label + 5 fields,
                // animation label + 2 fields.
                float height =
                    14f * (lineHeight + 2f) + 20f;

                SerializedProperty eventState =
                    element.FindPropertyRelative("eventState");

                height +=
                    EditorGUI.GetPropertyHeight(
                        eventState,
                        includeChildren: true
                    );

                return height;
            };

        planetList.drawElementCallback =
            DrawPlanetElement;

        planetList.onAddCallback =
            list =>
            {
                int newIndex =
                    planets.arraySize;

                planets.InsertArrayElementAtIndex(
                    newIndex
                );

                SerializedProperty element =
                    planets.GetArrayElementAtIndex(
                        newIndex
                    );

                ResetNewPlanet(
                    element,
                    newIndex
                );

                element.isExpanded = true;
            };
    }


    private void DrawPlanetElement(
        Rect rect,
        int index,
        bool isActive,
        bool isFocused)
    {
        SerializedProperty element =
            planets.GetArrayElementAtIndex(index);

        float lineHeight =
            EditorGUIUtility.singleLineHeight;

        rect.y += 2f;
        rect.height = lineHeight;

        SerializedProperty id =
            element.FindPropertyRelative("id");

        string title =
            string.IsNullOrWhiteSpace(id.stringValue)
                ? $"Planet {index + 1}"
                : id.stringValue;

        element.isExpanded =
            EditorGUI.Foldout(
                rect,
                element.isExpanded,
                title,
                true
            );

        if (!element.isExpanded)
            return;

        rect.y += lineHeight + 2f;

        EditorGUI.indentLevel++;

        EditorGUI.PropertyField(
            rect,
            id
        );

        rect.y += lineHeight + 2f;

        SerializedProperty displayName =
            element.FindPropertyRelative("displayName");

        EditorGUI.PropertyField(
            rect,
            displayName,
            new GUIContent(
                "Display Name",
                "Human-readable name shown once the planet is identified. Leave blank to fall back to Id."
            )
        );

        rect.y += lineHeight + 2f;

        SerializedProperty cell =
            element.FindPropertyRelative("cell");

        EditorGUI.PropertyField(
            rect,
            cell
        );

        rect.y += lineHeight + 2f;

        DrawTileDropdown(
            rect,
            element.FindPropertyRelative("tileIndex")
        );

        rect.y += lineHeight + 4f;

        SerializedProperty visibility =
            element.FindPropertyRelative("visibility");

        EditorGUI.LabelField(
            rect,
            "Visibility",
            EditorStyles.boldLabel
        );

        rect.y += lineHeight + 2f;

        SerializedProperty currentlyVisible =
            visibility.FindPropertyRelative("currentlyVisible");

        EditorGUI.PropertyField(
            rect,
            currentlyVisible,
            new GUIContent(
                Application.isPlaying
                    ? "Currently Visible (Live)"
                    : "Starting Visible",
                Application.isPlaying
                    ? "Runtime live player visibility. VisionManager updates this from the player's current position."
                    : "Authored starting reveal. VisionManager imports this into FogOfWar at Play start, then the runtime field returns to live-visibility meaning."
            )
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            visibility.FindPropertyRelative("discovered")
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            visibility.FindPropertyRelative("rememberLocation")
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            visibility.FindPropertyRelative("knowledgeState"),
            new GUIContent(
                "Knowledge State",
                "Unknown = identity not known, Detected = location/partial identity known, Identified = real name known."
            )
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            element.FindPropertyRelative("revealFogWhenDiscovered"),
            new GUIContent(
                "Reveal Fog When Discovered",
                "If disabled, this planet can still be visible, discovered and remembered, but VisionManager will never create a FogOfWar locked location for it."
            )
        );

        rect.y += lineHeight + 4f;

        EditorGUI.LabelField(
            rect,
            "Animation",
            EditorStyles.boldLabel
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            element.FindPropertyRelative("animate")
        );

        rect.y += lineHeight + 2f;

        EditorGUI.PropertyField(
            rect,
            element.FindPropertyRelative("animationStrength")
        );

        rect.y += lineHeight + 4f;

        SerializedProperty eventState =
            element.FindPropertyRelative("eventState");

        rect.height =
            EditorGUI.GetPropertyHeight(
                eventState,
                includeChildren: true
            );

        EditorGUI.PropertyField(
            rect,
            eventState,
            new GUIContent("Quest / Event Data"),
            includeChildren: true
        );

        EditorGUI.indentLevel--;
    }


    private void DrawTileDropdown(
        Rect rect,
        SerializedProperty tileIndex)
    {
        string[] options =
            BuildTileOptions();

        if (options.Length == 0)
        {
            EditorGUI.LabelField(
                rect,
                "Planet Tile",
                "No tiles in Planet Tile Library"
            );

            tileIndex.intValue = 0;
            return;
        }

        int selected =
            Mathf.Clamp(
                tileIndex.intValue,
                0,
                options.Length - 1
            );

        selected =
            EditorGUI.Popup(
                rect,
                "Planet Tile",
                selected,
                options
            );

        tileIndex.intValue =
            selected;
    }


    private string[] BuildTileOptions()
    {
        if (planetTiles.arraySize == 0)
            return System.Array.Empty<string>();

        List<string> names =
            new List<string>();

        for (int i = 0; i < planetTiles.arraySize; i++)
        {
            SerializedProperty entry =
                planetTiles.GetArrayElementAtIndex(i);

            TileBase tile =
                entry.objectReferenceValue as TileBase;

            names.Add(
                tile != null
                    ? $"{i}: {tile.name}"
                    : $"{i}: (None)"
            );
        }

        return names.ToArray();
    }


    private void ResetNewPlanet(
        SerializedProperty element,
        int index)
    {
        element.FindPropertyRelative("id").stringValue =
            $"Planet {index + 1}";

        element.FindPropertyRelative("displayName").stringValue =
            $"Planet {index + 1}";

        element.FindPropertyRelative("cell").vector3IntValue =
            Vector3Int.zero;

        element.FindPropertyRelative("tileIndex").intValue =
            0;

        SerializedProperty visibility =
            element.FindPropertyRelative("visibility");

        visibility.FindPropertyRelative("currentlyVisible").boolValue =
            false;

        visibility.FindPropertyRelative("discovered").boolValue =
            false;

        visibility.FindPropertyRelative("rememberLocation").boolValue =
            false;

        visibility.FindPropertyRelative("knowledgeState").enumValueIndex =
            (int)PlanetKnowledgeState.Unknown;

        element.FindPropertyRelative("revealFogWhenDiscovered").boolValue =
            true;

        element.FindPropertyRelative("animate").boolValue =
            true;

        element.FindPropertyRelative("animationStrength").floatValue =
            1f;
    }
}
