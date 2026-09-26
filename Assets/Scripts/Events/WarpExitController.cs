using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// N5 - the way out, and the end of a run.
///
/// Once fuel reaches the threshold, every walkable cell on the edge of the map
/// pulses with a highlight. Flying onto one asks WARP / NOT YET in the event
/// window. WARP ends the run on a summary (turns, resources, story beats) with
/// RESTART. Losing a fight (TurnPhase.Defeat) ends the run on the same summary.
///
/// EventDirector asks TryOpen first on every arrival, before planets and events.
/// Uses the event UI's BannerChoiceView, so it looks like every other event.
/// </summary>
public sealed class WarpExitController : MonoBehaviour
{
    public enum SummaryKind { Counter, Flag, Tag }

    [Serializable]
    public sealed class SummaryLine
    {
        public string label = "Mining jobs";
        public SummaryKind kind = SummaryKind.Counter;
        public string key = "MiningJobsDone";
        [Tooltip("Counters: show even when 0. Flags/tags only ever show when set.")]
        public bool showWhenZero = false;
    }

    [Header("References (found automatically if empty)")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private GridMap gridMap;
    [SerializeField] private TurnManager turnManager;
    [Tooltip("The event window (EventPanel's Banner Choice View).")]
    [SerializeField] private BannerChoiceView view;
    [Tooltip("Defeat waits for the combat screen to close before showing the summary.")]
    [SerializeField] private CombatScreenController combatScreen;

    [Header("Warp")]
    [Tooltip("Fuel needed to warp, as a share of max fuel (1 = full tank).")]
    [Range(0f, 1f)] [SerializeField] private float fuelThreshold = 1f;
    [SerializeField] private Sprite warpBanner;
    [SerializeField] private Sprite defeatBanner;
    [TextArea(2, 4)] [SerializeField] private string promptText = "The jump drive is spooled and humming. The edge of the system is right there. Leave now, or keep poking around?";
    [SerializeField] private string warpLabel = "WARP";
    [SerializeField] private string notYetLabel = "NOT YET";
    [SerializeField] private string restartLabel = "RESTART";

    [Header("Edge Highlight")]
    [SerializeField] private Color edgeColour = new Color(0.3f, 1f, 0.6f, 0.6f);
    [SerializeField] private float pulseSpeed = 2.5f;
    [Range(0f, 1f)] [SerializeField] private float pulseMinAlpha = 0.25f;
    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Foreground";
    [SerializeField] private int sortingOrder = 118;

    [Header("Run Summary")]
    [SerializeField] private List<SummaryLine> summaryLines = new List<SummaryLine>
    {
        new SummaryLine { label = "Mining jobs", kind = SummaryKind.Counter, key = "MiningJobsDone" },
        new SummaryLine { label = "Hazards survived", kind = SummaryKind.Counter, key = "HazardsSurvived" },
        new SummaryLine { label = "Wrecks searched", kind = SummaryKind.Counter, key = "events.resolved.derelict" },
        new SummaryLine { label = "Fuel collected", kind = SummaryKind.Counter, key = "events.resolved.pickup" },
        new SummaryLine { label = "Defiled a shrine", kind = SummaryKind.Flag, key = "quest:shrine:defiled" },
        new SummaryLine { label = "Shot a life capsule", kind = SummaryKind.Tag, key = "capsule:shot" },
    };

    public bool IsBusy { get; private set; }
    public bool RunOver { get; private set; }
    public bool IsReady
    {
        get
        {
            ShipResources r = player != null ? player.Resources : null;
            return r != null && r.MaxFuel > 0f && r.Fuel >= r.MaxFuel * fuelThreshold - 0.001f;
        }
    }

    private readonly List<Vector3Int> edgeCells = new List<Vector3Int>();
    private readonly List<SpriteRenderer> edgeRenderers = new List<SpriteRenderer>();
    private Transform edgeRoot;
    private int declinedTurn = -1;
    private MovementInterruptionHandle hold;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (combatScreen == null) combatScreen = FindFirstObjectByType<CombatScreenController>();
    }


    private void OnEnable()
    {
        if (turnManager != null) turnManager.PhaseChanged += HandlePhaseChanged;
    }


    private void OnDisable()
    {
        if (turnManager != null) turnManager.PhaseChanged -= HandlePhaseChanged;
    }


    private void Start()
    {
        BuildEdge();
    }


    private void Update()
    {
        if (edgeRoot == null) return;
        bool show = IsReady && !RunOver;
        if (edgeRoot.gameObject.activeSelf != show) edgeRoot.gameObject.SetActive(show);
        if (!show) return;

        float k = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * pulseSpeed);
        Color c = edgeColour;
        c.a = edgeColour.a * Mathf.Lerp(pulseMinAlpha, 1f, k);
        foreach (SpriteRenderer sr in edgeRenderers) sr.color = c;
    }


    // ------------------------------------------------------------ arrival

    /// <summary>Called by EventDirector first on every arrival. True = this arrival is claimed.</summary>
    public bool TryOpen(Vector3Int cell)
    {
        if (IsBusy || RunOver || view == null || gridMap == null) return false;
        if (!IsReady || !gridMap.IsMapEdgeCell(cell)) return false;
        int turn = turnManager != null ? turnManager.CurrentTurn : 0;
        if (turn == declinedTurn) return false;   // said "not yet" this turn: don't nag on every edge cell

        StartCoroutine(Prompt(turn));
        return true;
    }


    private IEnumerator Prompt(int turn)
    {
        IsBusy = true;
        hold = player != null ? player.AcquireMovementInterruption("Warp exit") : null;
        yield return null;   // let contact/combat resolve first

        if (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat)
        {
            Release();
            IsBusy = false;
            yield break;
        }

        ShipResources r = player.Resources;
        view.Show(warpBanner, "SYS://JUMP DRIVE", $"FUEL {r.Fuel:0}/{r.MaxFuel:0}", promptText, false);
        view.SetChoices(new[] { new BannerChoiceView.Choice("warp", warpLabel), new BannerChoiceView.Choice("stay", notYetLabel) });

        string picked = null;
        yield return view.WaitForChoice(id => picked = id);

        if (picked == "warp")
        {
            RunOver = true;
            yield return ShowSummary(true);   // keeps the hold: the run is over
            yield break;
        }

        view.Hide();
        declinedTurn = turn;
        Release();   // the paused route carries on
        IsBusy = false;
    }


    private void HandlePhaseChanged(TurnPhase phase)
    {
        if (phase != TurnPhase.Defeat || RunOver) return;
        RunOver = true;
        StartCoroutine(DefeatRoutine());
    }


    private IEnumerator DefeatRoutine()
    {
        yield return null;
        while (combatScreen != null && combatScreen.IsOpen) yield return null;   // let the fight's ending play out
        if (hold == null && player != null) hold = player.AcquireMovementInterruption("Run over");
        yield return ShowSummary(false);
    }


    // ------------------------------------------------------------ summary

    private IEnumerator ShowSummary(bool warped)
    {
        if (view == null) yield break;

        string picked = null;
        while (picked != "restart")
        {
            // Re-shown if anything else closed the window: the run is over either way.
            view.Show(
                warped ? warpBanner : defeatBanner,
                warped ? "SYS://JUMP COMPLETE" : "SYS://SIGNAL LOST",
                warped ? "RUN COMPLETE" : "HULL BREACH",
                BuildSummary(warped),
                !warped);
            view.SetChoices(new[] { new BannerChoiceView.Choice("restart", restartLabel) });
            yield return view.WaitForChoice(id => picked = id);
        }

        Restart();
    }


    private string BuildSummary(bool warped)
    {
        var sb = new StringBuilder();
        sb.Append(warped ? "You made the jump. The system shrinks to a dot behind you.\n\n"
                         : "The hull gives out. Somewhere, a delivery goes undelivered.\n\n");

        int turns = turnManager != null ? turnManager.CurrentTurn : 0;
        sb.Append($"TURNS  {turns}\n");

        ShipResources r = player != null ? player.Resources : null;
        if (r != null)
        {
            sb.Append($"HULL {r.HullIntegrity:0}/{r.MaxHullIntegrity:0}   SHIELDS {r.Shields:0}/{r.MaxShields:0}   FUEL {r.Fuel:0}/{r.MaxFuel:0}\n");
            sb.Append($"CREW {r.Crew}/{r.MaxCrew}");
        }
        IEventState state = player != null ? player.EventState : null;
        if (state != null) sb.Append($"   SUPPLIES {state.GetCounter(EventKeys.Supplies)}");
        sb.Append('\n');

        if (state != null)
        {
            foreach (SummaryLine line in summaryLines)
            {
                if (line == null || string.IsNullOrWhiteSpace(line.key)) continue;
                switch (line.kind)
                {
                    case SummaryKind.Counter:
                        int n = state.GetCounter(line.key);
                        if (n != 0 || line.showWhenZero) sb.Append($"- {line.label}: {n}\n");
                        break;
                    case SummaryKind.Flag:
                        if (state.GetFlag(line.key)) sb.Append($"- {line.label}\n");
                        break;
                    case SummaryKind.Tag:
                        if (state.HasTag(line.key)) sb.Append($"- {line.label}\n");
                        break;
                }
            }
        }
        return sb.ToString().TrimEnd('\n');
    }


    private static void Restart()
    {
        Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
        if (scene.buildIndex < 0)
        {
            // Not in Build Settings: still works in the editor.
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
            return;
        }
#endif
        SceneManager.LoadScene(scene.buildIndex);
    }


    private void Release()
    {
        hold?.Release();
        hold = null;
    }


    // ------------------------------------------------------------ edge overlay

    private void BuildEdge()
    {
        if (gridMap == null) return;
        edgeCells.Clear();
        BoundsInt b = gridMap.GroundBounds;
        for (int x = b.xMin; x < b.xMax; x++)
        {
            for (int y = b.yMin; y < b.yMax; y++)
            {
                Vector3Int cell = new Vector3Int(x, y, 0);
                if (gridMap.IsMapEdgeCell(cell)) edgeCells.Add(cell);
            }
        }

        edgeRoot = new GameObject("Warp Edge (Generated)").transform;
        edgeRoot.SetParent(transform, false);

        Sprite sprite = BuildEdgeSprite();
        float tile = Vector3.Distance(gridMap.CellToWorld(Vector3Int.zero), gridMap.CellToWorld(Vector3Int.right));
        float scale = sprite != null ? tile / Mathf.Max(0.0001f, sprite.bounds.size.x) : 1f;

        foreach (Vector3Int cell in edgeCells)
        {
            GameObject go = new GameObject("Edge " + cell);
            go.transform.SetParent(edgeRoot, false);
            go.transform.position = gridMap.CellToWorld(cell);
            go.transform.localScale = new Vector3(scale, scale, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = edgeColour;
            sr.sortingLayerName = sortingLayerName;
            sr.sortingOrder = sortingOrder;
            edgeRenderers.Add(sr);
        }

        edgeRoot.gameObject.SetActive(false);
    }


    private static Sprite BuildEdgeSprite()
    {
        const int size = 8;
        string[] rows = new string[size];
        for (int row = 0; row < size; row++)
        {
            char[] line = new char[size];
            for (int x = 0; x < size; x++)
            {
                int y = size - 1 - row;
                bool border = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                bool chevron = (x + y) % 4 == 0;
                line[x] = border || chevron ? '#' : '.';
            }
            rows[row] = new string(line);
        }
        return PixelGlyphFactory.GetSprite("warp-edge", rows, size);
    }
}
