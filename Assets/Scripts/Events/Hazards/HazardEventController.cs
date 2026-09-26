using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Unmarked mid-move events. Every cell the ship crosses that counts as a
/// hazard (currently: any asteroid cell) rolls Chance On Asteroid. On a hit the
/// route is paused via a movement interruption, a hazard event is picked from
/// the table (conditions checked against the player's IEventState) and shown
/// through the presenter, then the interruption is released and the paused
/// route resumes on its own.
///
/// There is no site and no marker: the player only sees which cells are risky
/// (RouteHazardOverlay), never whether a hazard will actually fire.
///
/// Each cell rolls at most once per turn (resuming a paused move can report
/// the same cell again), and a cell that has fired is spent: it never fires
/// again and drops off the hazard overlay (Once Per Cell).
/// EventDirector calls TryTrigger during its arrival handling so ordering with
/// Points of Interest, marked sites and scanning stays in one place.
/// </summary>
[DisallowMultipleComponent]
public class HazardEventController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private AsteroidFieldPainter asteroidField;

    [Tooltip("IEventPresenter used to show the hazard (currently DebugEventWindow).")]
    [SerializeField] private MonoBehaviour presenterBehaviour;

    [Header("Hazard Events")]
    [SerializeField] private bool hazardsEnabled = true;

    [Tooltip("Chance per asteroid cell crossed that a hazard event fires.")]
    [Range(0f, 1f)]
    [SerializeField] private float chanceOnAsteroid = 0.1f;

    [Tooltip("Event families that can fire as hazards (definitions must carry one of these tags).")]
    [SerializeField] private List<string> eventTags = new List<string> { "hazard" };

    [SerializeField] private EventTable table;

    [Tooltip("A cell that has fired a hazard never fires again (and stops showing as risky).")]
    [SerializeField] private bool oncePerCell = true;

    [Header("Randomness")]
    [SerializeField] private int randomSeed = 7340;

    public bool IsBusy { get; private set; }
    public bool HazardsEnabled { get => hazardsEnabled; set => hazardsEnabled = value; }
    public float ChanceOnAsteroid { get => chanceOnAsteroid; set => chanceOnAsteroid = Mathf.Clamp01(value); }

    private IEventPresenter presenter;
    private System.Random rng;
    private int hazardSerial;

    private readonly HashSet<Vector3Int> spentCells = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> rolledThisTurn = new HashSet<Vector3Int>();
    private int rolledTurn = -1;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();

        presenter = presenterBehaviour as IEventPresenter;
        if (presenter == null) presenter = FindFirstObjectByType<DebugEventWindow>();

        rng = new System.Random(randomSeed);
    }


    /// <summary>True if crossing this cell can fire a hazard. Also used by RouteHazardOverlay.</summary>
    public bool IsHazardCell(Vector3Int cell)
    {
        if (oncePerCell && spentCells.Contains(cell)) return false;
        return asteroidField != null && asteroidField.HasAsteroidAtCell(cell);
    }


    /// <summary>
    /// Rolls the hazard chance for an entered cell. Returns true if a hazard
    /// fired and claimed this arrival (the caller should not scan).
    /// </summary>
    public bool TryTrigger(Vector3Int cell)
    {
        if (!hazardsEnabled || IsBusy || presenter == null || table == null) return false;
        if (!IsHazardCell(cell)) return false;

        // One roll per cell per turn: a paused-then-resumed move can enter the same cell twice.
        int turn = turnManager != null ? turnManager.CurrentTurn : 0;
        if (turn != rolledTurn)
        {
            rolledTurn = turn;
            rolledThisTurn.Clear();
        }
        if (!rolledThisTurn.Add(cell)) return false;

        if (rng.NextDouble() >= chanceOnAsteroid) return false;

        EventContext context = new EventContext(player != null ? player.EventState : null, null, null, cell, turn);
        EventDefinition definition = table.PickWeighted(eventTags, context, rng);
        if (definition == null) return false;

        spentCells.Add(cell);
        StartCoroutine(Run(cell, definition, context));
        return true;
    }


    private IEnumerator Run(Vector3Int cell, EventDefinition definition, EventContext context)
    {
        IsBusy = true;

        // Pausing happens immediately so the ship stops on the hazard cell.
        MovementInterruptionHandle hold = player != null
            ? player.AcquireMovementInterruption("Hazard: " + definition.Id)
            : null;

        yield return null;   // let contact/combat resolve first

        if (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat)
        {
            hold?.Release();
            IsBusy = false;
            yield break;
        }

        hazardSerial++;
        EventSite site = new EventSite(
            $"hazard:{cell.x},{cell.y}:t{context.Turn}:{hazardSerial}",
            cell, definition, null, context.Turn);
        site.Knowledge = PlanetKnowledgeState.Identified;
        context = context.WithSite(site.State);

        EventOutcome outcome = EventOutcome.LeaveForLater;
        yield return presenter.Present(site, context, o => outcome = o);

        // A hazard cannot be "left for later": it happened either way.
        definition.OnResolve?.Apply(context);
        context.Player?.IncrementCounter(EventKeys.Resolved(EventCategory.Hazard));

        if (outcome.stopJourney && player != null) player.CancelCurrentJourney();
        hold?.Release();   // the paused route resumes by itself

        IsBusy = false;
    }
}
