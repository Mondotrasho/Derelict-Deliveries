using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Opens an event when the player enters a live site. Does NOT subscribe to
/// arrival itself: EventDirector calls TryTrigger first on every arrival, so a
/// site rolled by this arrival can never trigger on the same arrival.
///
/// While the presenter is open this component holds a movement interruption;
/// releasing it lets the paused route resume on its own.
///
/// Pickup sites (fuel) are the exception: they are collected on the spot with
/// no window and no pause, show a small float-up text, and do not claim the
/// arrival, so the ship simply keeps flying.
/// </summary>
[DisallowMultipleComponent]
public class EventTriggerHandler : MonoBehaviour
{
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private EventSiteRegistry registry;

    [Tooltip("Component implementing IEventPresenter (currently EventDiscovery/DebugEventWindow).")]
    [SerializeField] private MonoBehaviour presenterBehaviour;

    [Tooltip("Assign explicitly (the panel starts inactive). If another system already has it open on arrival, the event is left for later.")]
    [SerializeField] private DialoguePanelController dialoguePanel;

    [Header("Pickups")]
    [SerializeField] private Color pickupTextColour = new Color(1f, 0.85f, 0.3f, 1f);
    [Tooltip("TMP font asset for the float-up text (e.g. the same one as the UI). Empty = TextMeshPro's default.")]
    [SerializeField] private TMP_FontAsset pickupTextFont;
    [Tooltip("Text height as a fraction of one grid tile.")]
    [SerializeField] private float pickupTextTileHeight = 0.6f;
    [SortingLayerName]
    [SerializeField] private string pickupTextSortingLayer = "Default";
    [SerializeField] private int pickupTextSortingOrder = 200;

    public bool IsBusy { get; private set; }
    public EventSite CurrentSite { get; private set; }

    private IEventPresenter presenter;


    private void Awake()
    {
        if (player == null) player = GetComponent<PlayerShipState>();
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();

        presenter = presenterBehaviour as IEventPresenter;
        if (presenterBehaviour != null && presenter == null)
            Debug.LogWarning($"{name}: Presenter Behaviour does not implement IEventPresenter.", this);

        if (presenter == null)
        {
            foreach (MonoBehaviour mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb is IEventPresenter p)
                {
                    presenter = p;
                    presenterBehaviour = mb;
                    break;
                }
            }
        }
    }


    private void OnEnable()
    {
        if (turnManager != null) turnManager.PhaseChanged += HandlePhaseChanged;
    }


    private void OnDisable()
    {
        if (turnManager != null) turnManager.PhaseChanged -= HandlePhaseChanged;
    }


    /// <summary>
    /// Returns true if a live site at (or within triggerRadius of) the cell
    /// claimed this arrival. The caller must then skip scanning.
    /// </summary>
    public bool TryTrigger(Vector3Int playerCell, int triggerRadius)
    {
        if (IsBusy || registry == null || presenter == null) return false;

        EventSite site = registry.FindNearestLive(playerCell, Mathf.Max(0, triggerRadius));
        if (site == null) return false;

        // Re-validate now rather than every turn: the world or state may have moved on.
        bool worldValid = site.Source == null || site.Source.IsSiteStillValid(site);
        EventContext ctx = BuildContext(site);
        bool conditionsHold = site.Definition == null ||
                              site.Definition.Conditions == null ||
                              site.Definition.Conditions.IsMet(ctx);

        if (!worldValid || !conditionsHold)
        {
            Debug.Log($"Event site {site.Id} is no longer valid (world={worldValid}, conditions={conditionsHold}); removing.", this);
            registry.SetState(site, EventSiteState.Expired);
            registry.Remove(site);
            return false;
        }

        if (site.Category == EventCategory.Pickup)
        {
            CollectPickup(site, ctx);
            return false;   // not claimed: the ship keeps flying, the arrival carries on as normal
        }

        StartCoroutine(Run(site, ctx));
        return true;
    }


    private void CollectPickup(EventSite site, EventContext ctx)
    {
        ApplyResolution(site, ctx);
        string summary = site.Definition != null
            ? EventChoiceResolver.ApplyResources(site.Definition.PickupResources, ctx, player)
            : "";

        GridMap grid = FindFirstObjectByType<GridMap>();
        if (grid != null)
        {
            float tile = Vector3.Distance(grid.CellToWorld(Vector3Int.zero), grid.CellToWorld(Vector3Int.right));
            string text = !string.IsNullOrEmpty(summary) ? summary : (site.Definition != null ? site.Definition.DisplayName : "");
            FloatUpText.Spawn(grid.CellToWorld(site.Cell), text, pickupTextColour, tile * pickupTextTileHeight,
                              pickupTextFont, pickupTextSortingLayer, pickupTextSortingOrder, tile);
        }

        registry.SetState(site, EventSiteState.Resolved);
        registry.Remove(site);
    }


    private IEnumerator Run(EventSite site, EventContext ctx)
    {
        IsBusy = true;
        CurrentSite = site;

        MovementInterruptionHandle hold = player != null
            ? player.AcquireMovementInterruption($"Event: {site.Id}")
            : null;

        // Let any other arrival subscriber (e.g. PlanetDialogueTestTrigger) run first.
        yield return null;

        if ((dialoguePanel != null && dialoguePanel.IsDialogueOpen) ||
            (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat))
        {
            hold?.Release();
            CurrentSite = null;
            IsBusy = false;
            yield break;
        }

        registry.SetState(site, EventSiteState.Triggered);
        registry.RaiseKnowledge(site, PlanetKnowledgeState.Identified);   // you are on top of it

        EventOutcome outcome = EventOutcome.LeaveForLater;
        bool finished = false;
        yield return presenter.Present(site, ctx, o =>
        {
            outcome = o;
            finished = true;
        });
        if (!finished) outcome = EventOutcome.LeaveForLater;

        if (outcome.resolved) ApplyResolution(site, ctx);

        if (outcome.stopJourney && player != null) player.CancelCurrentJourney();
        hold?.Release();

        if (outcome.resolved)
        {
            registry.SetState(site, EventSiteState.Resolved);
            registry.Remove(site);
        }
        else if (registry.HasSiteAtCell(site.Cell))
        {
            registry.SetState(site, EventSiteState.Active);
        }

        CurrentSite = null;
        IsBusy = false;
    }


    private void ApplyResolution(EventSite site, EventContext ctx)
    {
        EventDefinition def = site.Definition;
        if (def != null)
        {
            def.OnResolve?.Apply(ctx);
            if (def.OncePerPlanet && ctx.Planet != null) ctx.Planet.SetFlag(EventKeys.Done(def.Id), true);
        }

        ctx.Player?.IncrementCounter(EventKeys.Resolved(site.Category));
    }


    private EventContext BuildContext(EventSite site)
    {
        int turn = turnManager != null ? turnManager.CurrentTurn : 0;
        EventContext ctx = site.Source != null
            ? site.Source.BuildContext(site.Cell, turn)
            : new EventContext(player != null ? player.EventState : null, site.Planet, null, site.Cell, turn);
        return ctx.WithSite(site.State);
    }


    private void HandlePhaseChanged(TurnPhase phase)
    {
        if (IsBusy && phase == TurnPhase.Combat) presenter?.Cancel();
    }
}
