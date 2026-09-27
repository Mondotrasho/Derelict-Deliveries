using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Opens marked event sites. It does not subscribe to movement itself:
/// EventDirector decides whether an arrival is an intentional investigation or
/// a simple fly-over, while Point of Interest interactions can call TryTrigger
/// directly because the player already chose them explicitly.
///
/// While the presenter is open this component holds a movement interruption.
/// Intentional mining/derelict investigations spend the movement left after
/// the window finishes. Other explicit callers keep their existing behaviour.
///
/// Pickup sites (fuel) are collected on the spot with no window and no pause,
/// show a small float-up text, and do not claim the arrival, so the ship simply
/// keeps flying.
/// </summary>
[DisallowMultipleComponent]
public class EventTriggerHandler : MonoBehaviour
{
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private MovementAllowance movementAllowance;

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
        if (movementAllowance == null && player != null)
            movementAllowance = player.GetComponentInChildren<MovementAllowance>(true);
        if (movementAllowance == null) movementAllowance = FindFirstObjectByType<MovementAllowance>();

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
    /// Explicitly opens a live site at (or within triggerRadius of) the cell.
    /// Used by systems that already represent an intentional choice, such as
    /// the planet Point of Interest picker.
    /// </summary>
    public bool TryTrigger(Vector3Int playerCell, int triggerRadius)
    {
        if (IsBusy || registry == null || presenter == null) return false;

        EventSite site = registry.FindNearestLive(playerCell, Mathf.Max(0, triggerRadius));
        return TryTriggerSite(site, false);
    }


    /// <summary>
    /// Arrival-facing trigger rule. Pickups still collect when flown over, but
    /// investigation sites only open when the player deliberately committed a
    /// journey whose destination was that exact site. Passing through one does
    /// nothing. Intentional investigation also spends the movement left after
    /// the event window finishes.
    /// </summary>
    public bool TryTriggerArrival(
        Vector3Int playerCell,
        int pickupTriggerRadius,
        EventSite intentionalSite)
    {
        if (IsBusy || registry == null || presenter == null) return false;

        EventSite site =
            registry.FindNearestLive(playerCell, Mathf.Max(0, pickupTriggerRadius));

        if (site == null) return false;

        if (site.Category == EventCategory.Pickup)
        {
            return TryTriggerSite(site, false);
        }

        if (!object.ReferenceEquals(site, intentionalSite) || site.Cell != playerCell)
        {
            return false;
        }

        return TryTriggerSite(site, true);
    }


    private bool TryTriggerSite(EventSite site, bool consumeRemainingMovement)
    {
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

        StartCoroutine(Run(site, ctx, consumeRemainingMovement));
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


    private IEnumerator Run(
        EventSite site,
        EventContext ctx,
        bool consumeRemainingMovement)
    {
        IsBusy = true;
        CurrentSite = site;

        MovementInterruptionHandle hold = player != null
            ? player.AcquireMovementInterruption($"Event: {site.Id}")
            : null;

        // An intentional investigation is the journey destination, not a
        // mid-route interruption. Discard the physical/queued journey now so
        // releasing the event hold cannot re-enter this cell or continue past it.
        if (consumeRemainingMovement && player != null)
        {
            player.CancelCurrentJourney();
        }

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

        // Investigation is a deliberate stop. Keep the event UI inside the
        // player phase, then spend whatever movement remains once it closes.
        // With auto-end enabled this can end the turn, but never underneath
        // an open event window.
        if (consumeRemainingMovement && movementAllowance != null)
        {
            movementAllowance.Spend(movementAllowance.CurrentMovementPoints);
        }

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
