using System;
using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>
/// Temporary IMGUI event presenter (same pattern as EnemyEncounterTestController).
/// Shows the triggered site and offers outcome buttons that each call one
/// existing API, so the whole loop - event -> world change -> state written ->
/// conditions change what appears next - can be tested before real UI exists.
///
/// Replace by assigning a different IEventPresenter on EventTriggerHandler.
/// </summary>
public class DebugEventWindow : MonoBehaviour, IEventPresenter
{
    [Header("Outcome Targets")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private AsteroidFieldPainter asteroidField;
    [SerializeField] private EnemySpawnController enemySpawner;

    [Header("Window")]
    [SerializeField] private Rect windowRect = new Rect(30f, 30f, 430f, 560f);
    [SerializeField] private int windowId = 48152;

    [Header("Test Outcome Amounts")]
    [Min(0f)] [SerializeField] private float salvageFuelAmount = 10f;
    [Min(0f)] [SerializeField] private float hullHitAmount = 10f;
    [Min(1)] [SerializeField] private int ambushWaveSize = 1;

    public bool IsOpen => site != null;

    private EventSite site;
    private EventContext context;
    private bool hasResult;
    private EventOutcome result;
    private bool stopJourney;
    private Vector2 scroll;
    private string lastAction = "";


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
    }


    public IEnumerator Present(EventSite eventSite, EventContext eventContext, Action<EventOutcome> done)
    {
        site = eventSite;
        context = eventContext;
        hasResult = false;
        stopJourney = false;
        lastAction = "";
        scroll = Vector2.zero;

        while (!hasResult) yield return null;

        EventOutcome outcome = result;
        site = null;
        done?.Invoke(outcome);
    }


    public void Cancel()
    {
        if (site == null) return;
        Finish(false);
    }


    private void Finish(bool resolved)
    {
        result = new EventOutcome { resolved = resolved, stopJourney = stopJourney };
        hasResult = true;
    }


    private void OnGUI()
    {
        if (site == null || hasResult) return;
        windowRect = GUI.ModalWindow(windowId, windowRect, DrawWindow, "Event (debug)");
    }


    private void DrawWindow(int id)
    {
        EventDefinition def = site.Definition;

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));

        GUILayout.Label(def != null ? $"<b>{def.DisplayName}</b>  ({def.Id})" : "<b>(no definition)</b>",
            RichLabel());
        GUILayout.Label($"Category: {site.Category}    Knowledge: {site.Knowledge}");
        GUILayout.Label($"Cell: {site.Cell.x}, {site.Cell.y}    Created turn: {site.CreatedTurn}    Source: {(site.Source != null ? site.Source.SourceId : "-")}");
        GUILayout.Label("Tags: " + JoinTags(site.State.Tags));

        if (site.Planet != null)
        {
            GUILayout.Label($"Planet: {site.Planet.displayName} ({site.Planet.id})");
            GUILayout.Label("Planet tags: " + JoinTags(site.Planet.eventState.Tags));
        }

        if (def != null)
        {
            bool conditionsOk = def.Conditions == null || def.Conditions.IsMet(context);
            GUILayout.Label($"Conditions: {(def.Conditions == null || def.Conditions.IsEmpty ? "none" : conditionsOk ? "pass" : "FAIL")}"
                            + (def.OncePerPlanet ? "    once per planet" : ""));
            GUILayout.Space(6f);
            GUILayout.Label(def.DebugText, WrapLabel());
        }

        if (!string.IsNullOrEmpty(lastAction))
        {
            GUILayout.Space(6f);
            GUILayout.Label(lastAction, WrapLabel());
        }

        GUILayout.EndScrollView();

        GUILayout.Space(6f);
        GUILayout.Label("Resolve with a test outcome:");

        bool asteroidHere = asteroidField != null && asteroidField.HasAsteroidAtCell(site.Cell);
        GUI.enabled = asteroidHere;
        if (GUILayout.Button("Mine it (consume asteroid)"))
        {
            asteroidField.TryConsumeCell(site.Cell);
            Finish(true);
        }

        GUI.enabled = player != null && player.Resources != null;
        if (GUILayout.Button($"Salvage fuel (+{salvageFuelAmount:0.#})"))
        {
            player.Resources.AddFuel(salvageFuelAmount);
            Finish(true);
        }

        if (GUILayout.Button($"Hull hit (-{hullHitAmount:0.#})"))
        {
            player.Resources.ApplyHullDamage(hullHitAmount);
            Finish(true);
        }

        GUI.enabled = enemySpawner != null;
        if (GUILayout.Button($"Ambush (spawn {ambushWaveSize})"))
        {
            int spawned = enemySpawner.SpawnWave(ambushWaveSize);
            lastAction = $"Ambush spawned {spawned} ship(s).";
            Finish(true);
        }

        GUI.enabled = true;
        if (GUILayout.Button("Resolve (no side effect)"))
        {
            Finish(true);
        }

        GUILayout.Space(4f);
        stopJourney = GUILayout.Toggle(stopJourney, "Stop here (cancel the rest of the journey)");

        GUILayout.Space(4f);
        if (GUILayout.Button("Leave for later"))
        {
            Finish(false);
        }

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }


    private static string JoinTags(System.Collections.Generic.IReadOnlyList<string> tags)
    {
        if (tags == null || tags.Count == 0) return "-";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(tags[i]);
        }
        return sb.ToString();
    }


    private static GUIStyle richLabel;
    private static GUIStyle wrapLabel;

    private static GUIStyle RichLabel()
    {
        if (richLabel == null) richLabel = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
        return richLabel;
    }

    private static GUIStyle WrapLabel()
    {
        if (wrapLabel == null) wrapLabel = new GUIStyle(GUI.skin.label) { wordWrap = true };
        return wrapLabel;
    }
}
