using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Default FogOfWar Inspector plus a live, read-only view of every locked
/// location that actually exists inside the runtime fog system.
/// </summary>
[CustomEditor(typeof(FogOfWar))]
public class FogOfWarEditor : Editor
{
    public override bool RequiresConstantRepaint()
    {
        // Runtime locations can be added/removed by VisionManager or other
        // gameplay code, so keep the debug section visibly current.
        return Application.isPlaying;
    }


    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10f);

        FogOfWar fog =
            (FogOfWar)target;

        List<FogOfWar.LockedLocationInfo> locations =
            fog.GetLockedLocationSnapshot();

        EditorGUILayout.LabelField(
            $"Runtime Locked Locations ({locations.Count})",
            EditorStyles.boldLabel
        );

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "This section shows the actual active lock list during Play mode. " +
                "Initial Locked Locations above are configuration; this list is runtime state.",
                MessageType.Info
            );

            return;
        }

        if (locations.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No locked locations are currently active.",
                MessageType.None
            );

            return;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            for (int i = 0; i < locations.Count; i++)
            {
                FogOfWar.LockedLocationInfo info =
                    locations[i];

                string displayId =
                    info.IsNamed
                        ? info.Id
                        : "(anonymous)";

                EditorGUILayout.BeginVertical(
                    EditorStyles.helpBox
                );

                EditorGUILayout.LabelField(
                    $"[{i}] {displayId}",
                    EditorStyles.boldLabel
                );

                EditorGUILayout.TextField(
                    "Source",
                    info.Source.ToString()
                );

                EditorGUILayout.TextField(
                    "Tier",
                    info.Tier.ToString()
                );

                EditorGUILayout.Vector3IntField(
                    "Cell",
                    info.Cell
                );

                EditorGUILayout.EndVertical();
            }
        }
    }
}
