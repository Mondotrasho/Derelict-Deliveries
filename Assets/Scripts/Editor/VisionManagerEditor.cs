using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Default VisionManager Inspector plus a live, read-only per-planet debug list.
///
/// The important distinction is:
/// - Live Tier: what the ship can see RIGHT NOW from its current position.
/// - Effective Tier: what FogOfWar is actually rendering after combining live
///   vision, remembered locations and authored starting/locked reveals.
/// </summary>
[CustomEditor(typeof(VisionManager))]
public class VisionManagerEditor : Editor
{
    public override bool RequiresConstantRepaint()
    {
        return Application.isPlaying;
    }


    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        VisionManager manager =
            (VisionManager)target;

        EditorGUILayout.Space(10f);

        EditorGUILayout.LabelField(
            "Runtime Planet Vision",
            EditorStyles.boldLabel
        );


        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Enter Play mode to see live and effective planet visibility.",
                MessageType.Info
            );

            return;
        }


        EditorGUILayout.LabelField(
            "Currently Visible (Live)",
            manager.CurrentlyVisiblePlanetCount.ToString()
        );

        EditorGUILayout.LabelField(
            "Discovered",
            manager.DiscoveredPlanetCount.ToString()
        );

        EditorGUILayout.LabelField(
            "Managed Fog Locks",
            manager.ManagedPlanetLockCount.ToString()
        );


        List<VisionManager.PlanetVisionInfo> planets =
            manager.GetPlanetVisionSnapshot();


        if (planets.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No planets found in PlanetManager.",
                MessageType.None
            );

            return;
        }


        using (new EditorGUI.DisabledScope(true))
        {
            for (int i = 0; i < planets.Count; i++)
            {
                VisionManager.PlanetVisionInfo info =
                    planets[i];

                string id =
                    string.IsNullOrWhiteSpace(info.Id)
                        ? "(no ID)"
                        : info.Id;


                EditorGUILayout.BeginVertical(
                    EditorStyles.helpBox
                );


                EditorGUILayout.LabelField(
                    $"[{i}] {id}",
                    EditorStyles.boldLabel
                );

                EditorGUILayout.Vector3IntField(
                    "Cell",
                    info.Cell
                );

                EditorGUILayout.TextField(
                    "Live Tier",
                    info.LiveTier.ToString()
                );

                EditorGUILayout.TextField(
                    "Effective Tier",
                    info.EffectiveTier.ToString()
                );

                EditorGUILayout.TextField(
                    "Knowledge State",
                    info.KnowledgeState.ToString()
                );

                EditorGUILayout.Toggle(
                    "Currently Visible (Live)",
                    info.CurrentlyVisible
                );

                EditorGUILayout.Toggle(
                    "Discovered",
                    info.Discovered
                );

                EditorGUILayout.Toggle(
                    "Remember Location",
                    info.RememberLocation
                );

                EditorGUILayout.Toggle(
                    "Reveal Fog When Discovered",
                    info.RevealFogWhenDiscovered
                );


                EditorGUILayout.Space(2f);

                EditorGUILayout.Toggle(
                    "Has Starting Fog Lock",
                    info.HasStartingFogLock
                );

                if (info.HasStartingFogLock)
                {
                    EditorGUILayout.TextField(
                        "Starting Fog Tier",
                        info.StartingFogLockTier.ToString()
                    );
                }


                EditorGUILayout.Toggle(
                    "Has Remembered Fog Lock",
                    info.HasFogLock
                );

                if (info.HasFogLock)
                {
                    EditorGUILayout.TextField(
                        "Remembered Fog Tier",
                        info.FogLockTier.ToString()
                    );
                }


                EditorGUILayout.EndVertical();
            }
        }
    }
}
