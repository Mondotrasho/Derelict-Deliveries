using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Temporary IMGUI picker for Point of Interest options (same style as the
/// other debug windows). Lists every available option plus Leave.
/// </summary>
public class DebugPoiPicker : MonoBehaviour, IPoiPicker
{
    [SerializeField] private Rect windowRect = new Rect(30f, 30f, 420f, 460f);
    [SerializeField] private int windowId = 48153;

    private Planet planet;
    private IReadOnlyList<PoiChoice> choices;
    private bool done;
    private PoiChoice result;
    private Vector2 scroll;

    private static GUIStyle titleStyle;
    private static GUIStyle wrapStyle;


    public IEnumerator Pick(Planet targetPlanet, IReadOnlyList<PoiChoice> options, Action<PoiChoice> picked)
    {
        planet = targetPlanet;
        choices = options;
        done = false;
        result = null;
        scroll = Vector2.zero;

        while (!done) yield return null;

        PoiChoice chosen = result;
        planet = null;
        choices = null;
        picked?.Invoke(chosen);
    }


    public void Cancel()
    {
        result = null;
        done = true;
    }


    private void OnGUI()
    {
        if (choices == null || done) return;
        string title = planet != null
            ? (string.IsNullOrWhiteSpace(planet.displayName) ? planet.id : planet.displayName)
            : "Point of interest";
        windowRect = GUI.ModalWindow(windowId, windowRect, Draw, title);
    }


    private void Draw(int id)
    {
        if (titleStyle == null) titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        if (wrapStyle == null) wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };

        GUILayout.Label("What do you want to do here?");
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));

        foreach (PoiChoice choice in choices)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(choice.Title, titleStyle);
            if (!string.IsNullOrWhiteSpace(choice.Description)) GUILayout.Label(choice.Description, wrapStyle);
            if (choice.Option != null) GUILayout.Label("[" + choice.Option.kind + "]");
            if (GUILayout.Button("Choose"))
            {
                result = choice;
                done = true;
            }
            GUILayout.EndVertical();
        }

        GUILayout.EndScrollView();

        if (GUILayout.Button("Leave"))
        {
            result = null;
            done = true;
        }

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }
}
