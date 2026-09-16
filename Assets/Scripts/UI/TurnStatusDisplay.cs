using TMPro;
using UnityEngine;

/// <summary>
/// Read-only status display for turn number, movement budget, and what
/// the player can do next. Pulls from TurnManager, MovementAllowance,
/// RoutePlanner, and MovementPlanController - none of those know this
/// script exists, the same one-directional pattern RoutePathRenderer
/// already uses to stay decoupled from what it's displaying.
///
/// Every text field is optional and independent - assign only the ones
/// your layout actually uses, the rest are simply skipped.
/// </summary>
public class TurnStatusDisplay : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private TurnManager turnManager;

    [SerializeField]
    private MovementAllowance movementAllowance;

    [SerializeField]
    private RoutePlanner routePlanner;

    [SerializeField]
    private MovementPlanController movementPlanController;


    [Header("UI (all optional)")]

    [Tooltip("e.g. \"Turn 3\"")]
    [SerializeField]
    private TextMeshProUGUI turnText;

    [Tooltip("e.g. \"Movement: 5 / 8\"")]
    [SerializeField]
    private TextMeshProUGUI budgetText;

    [Tooltip("A short hint for what to do next - \"Plan a route\", \"Route planned - press Commit\", \"Continue to next leg\", etc.")]
    [SerializeField]
    private TextMeshProUGUI nextActionText;

    [Tooltip("How long, in seconds, the RouteTooExpensive message stays before the next refresh would naturally replace it. Purely a minimum display time, not a countdown - any real change (a new plan, a commit, a turn ending) overwrites it immediately regardless.")]
    [SerializeField, Min(0f)]
    private float tooExpensiveMessageSeconds = 1.5f;


    private float tooExpensiveMessageUntil = -1f;


    private void OnEnable()
    {
        if (turnManager != null)
        {
            turnManager.TurnEnded += HandleChanged;
        }

        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged += HandleChanged;
            movementAllowance.MovementCostRulesChanged += HandleChanged;
        }

        if (routePlanner != null)
        {
            routePlanner.RouteChanged += HandleRouteChanged;
        }

        if (movementPlanController != null)
        {
            movementPlanController.QueuedRemainderChanged += HandleChanged;
            movementPlanController.RouteCommitted += HandleChanged;
            movementPlanController.RouteCancelled += HandleChanged;
            movementPlanController.RouteTooExpensive += HandleRouteTooExpensive;
        }

        RefreshDisplay();
    }


    private void OnDisable()
    {
        if (turnManager != null)
        {
            turnManager.TurnEnded -= HandleChanged;
        }

        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged -= HandleChanged;
            movementAllowance.MovementCostRulesChanged -= HandleChanged;
        }

        if (routePlanner != null)
        {
            routePlanner.RouteChanged -= HandleRouteChanged;
        }

        if (movementPlanController != null)
        {
            movementPlanController.QueuedRemainderChanged -= HandleChanged;
            movementPlanController.RouteCommitted -= HandleChanged;
            movementPlanController.RouteCancelled -= HandleChanged;
            movementPlanController.RouteTooExpensive -= HandleRouteTooExpensive;
        }
    }


    private void HandleChanged(int _)
    {
        RefreshDisplay();
    }


    private void HandleChanged()
    {
        RefreshDisplay();
    }


    private void HandleRouteChanged(System.Collections.Generic.List<Vector3Int> path)
    {
        RefreshDisplay();
    }


    /// <summary>
    /// Shows a "too far this turn" hint for at least
    /// tooExpensiveMessageSeconds, so the message is readable even though
    /// RouteTooExpensive fires and passes in the same frame as the click
    /// that triggered it. Update() clears it again once that time is up,
    /// unless something else has already replaced it.
    /// </summary>
    private void HandleRouteTooExpensive()
    {
        tooExpensiveMessageUntil = Time.unscaledTime + tooExpensiveMessageSeconds;

        RefreshDisplay();
    }


    private void Update()
    {
        if (tooExpensiveMessageUntil >= 0f &&
            Time.unscaledTime >= tooExpensiveMessageUntil)
        {
            tooExpensiveMessageUntil = -1f;

            RefreshDisplay();
        }
    }


    private void RefreshDisplay()
    {
        if (turnText != null && turnManager != null)
        {
            turnText.text = $"Turn {turnManager.CurrentTurn}";
        }

        if (budgetText != null && movementAllowance != null)
        {
            budgetText.text =
                $"Movement: {movementAllowance.CurrentMovementPoints} / {movementAllowance.MaxMovementPoints}";
        }

        if (nextActionText != null)
        {
            nextActionText.text = BuildNextActionMessage();
        }
    }


    /// <summary>
    /// Plain-language summary of what committing (or ending the turn)
    /// would currently do, in priority order: a still-fresh "too
    /// expensive" result outranks everything else briefly, then a queued
    /// remainder, then a freshly planned route, then the idle state with
    /// nothing planned at all.
    /// </summary>
    private string BuildNextActionMessage()
    {
        if (tooExpensiveMessageUntil >= 0f)
        {
            return "Too far to reach this turn - wait or plan a shorter route.";
        }

        if (movementPlanController != null && movementPlanController.HasQueuedRemainder)
        {
            return "Route continues next turn - press Commit or End Turn.";
        }

        if (routePlanner != null && routePlanner.HasPlannedRoute)
        {
            return "Route planned - press Commit to fly it.";
        }

        return "Plan a route, or press End Turn to pass.";
    }
}