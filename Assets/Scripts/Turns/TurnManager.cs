using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shared world-time clock for the grid game. Nothing more than counters
/// and events - TurnManager knows nothing about movement, budgets, or
/// any other system, and never references PlayerGridController,
/// MovementAllowance, or anything else in Movement/. Everything that
/// cares about time passing reaches out to this clock; this script never
/// reaches into anything else. That one-directional dependency is what
/// keeps it reusable for enemy ships, resource upkeep, and anything
/// added later without editing this file again.
///
/// Two granularities, kept deliberately distinct:
/// - Tick: the smallest unit of world-time. Reported via AdvanceTick(),
///   called by whatever is moving (see PlayerGridController's
///   cellsPerTick) - normally once per cell crossed, less often than
///   that for a faster mover. Anything reactive (fog decay, later enemy
///   stepping, resource drain) should subscribe to Ticked rather than to
///   raw movement events directly.
/// - Turn: the player-facing bookkeeping window - whose go it is, how
///   much budget is left. Contains some number of ticks. Ends only via
///   EndTurn() - called from the optional waitTurnButton, or from
///   elsewhere in code (MovementAllowance calls this itself once a
///   turn's movement budget runs out, if it is configured to do that).
///
/// ResetClock() zeroes the clock outright - entering a new system, a
/// narrative time-skip - without this script needing to know why.
/// TurnsSince/TicksSince exist so things like a timed quest can record a
/// start value and later ask "how long has it been" without every
/// caller re-deriving the same subtraction.
/// </summary>
public class TurnManager : MonoBehaviour
{
    [Tooltip("Which turn number the game starts on.")]
    [SerializeField]
    private int currentTurn = 1;

    [Tooltip("Optional. Clicking this button ends the current turn - for waiting/passing without moving.")]
    [SerializeField]
    private Button waitTurnButton;


    /// <summary>Raised whenever AdvanceTick() is called, after the tick counters have been updated. Passes the new CurrentTick value.</summary>
    public event Action<int> Ticked;

    /// <summary>Raised after the turn number has been incremented, however that happened. Passes the new CurrentTurn value.</summary>
    public event Action<int> TurnEnded;

    /// <summary>Raised after ResetClock() zeroes the turn and tick counters.</summary>
    public event Action ClockReset;


    /// <summary>The turn number currently in progress.</summary>
    public int CurrentTurn
    {
        get
        {
            return currentTurn;
        }
    }


    /// <summary>
    /// Total ticks elapsed since the clock was last reset. Monotonic -
    /// never decreases except via ResetClock().
    /// </summary>
    public int CurrentTick { get; private set; }


    /// <summary>
    /// Ticks elapsed since the current turn began. Resets to 0 every
    /// EndTurn().
    /// </summary>
    public int TicksThisTurn { get; private set; }


    private void OnEnable()
    {
        if (waitTurnButton != null)
        {
            waitTurnButton.onClick.AddListener(EndTurn);
        }
    }


    private void OnDisable()
    {
        if (waitTurnButton != null)
        {
            waitTurnButton.onClick.RemoveListener(EndTurn);
        }
    }


    /// <summary>
    /// Reports that count tick(s) of world-time have elapsed. Called by
    /// whatever is moving (see PlayerGridController.cellsPerTick) - this
    /// script has no opinion on what counts as a tick, only on tracking
    /// and broadcasting that one happened. Safe to call with count > 1
    /// if something ever needs to report several ticks at once.
    /// </summary>
    public void AdvanceTick(int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        CurrentTick += count;
        TicksThisTurn += count;

        Ticked?.Invoke(CurrentTick);
    }


    /// <summary>
    /// Advances the turn counter and notifies anything listening for a
    /// turn to have ended. Wired automatically to waitTurnButton's click
    /// if one is assigned. Also fine to call directly from other code -
    /// MovementAllowance calls this itself when a turn's movement budget
    /// runs out, if it is configured to do that.
    /// </summary>
    public void EndTurn()
    {
        currentTurn++;
        TicksThisTurn = 0;

        TurnEnded?.Invoke(currentTurn);
    }


    /// <summary>
    /// Zeroes the turn and tick counters outright - for entering a new
    /// system, a narrative time-skip, or anything else that needs a
    /// clean slate rather than an ordinary turn ending.
    /// </summary>
    public void ResetClock()
    {
        currentTurn = 1;
        CurrentTick = 0;
        TicksThisTurn = 0;

        ClockReset?.Invoke();
    }


    /// <summary>
    /// Turns elapsed since startTurn - typically a value the caller read
    /// from CurrentTurn earlier and stored itself (e.g. a timed quest
    /// recording when it began, to compare against a deadline later).
    /// </summary>
    public int TurnsSince(int startTurn)
    {
        return currentTurn - startTurn;
    }


    /// <summary>Ticks elapsed since startTick - the finer-grained equivalent of TurnsSince.</summary>
    public int TicksSince(int startTick)
    {
        return CurrentTick - startTick;
    }
}