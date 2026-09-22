using System;
using UnityEngine;
using UnityEngine.UI;

public enum TurnPhase
{
    Player,
    WaitingForPlayerMovement,
    Enemy,
    Combat,
    Defeat
}

/// <summary>
/// Owns the overworld phase sequence. A turn number describes one complete
/// player/enemy cycle and advances only after the enemy phase is resolved.
/// </summary>
public class TurnManager : MonoBehaviour
{
    [SerializeField] private int currentTurn = 1;
    [SerializeField] private Button waitTurnButton;

    public event Action<int> Ticked;
    public event Action<int> TurnEnded;
    public event Action ClockReset;
    public event Action<TurnPhase> PhaseChanged;
    public event Action<int> PlayerPhaseStarted;
    public event Action<int> PlayerPhaseEnding;
    public event Action<int> EnemyPhaseStarted;

    public int CurrentTurn => currentTurn;
    public int CurrentTick { get; private set; }
    public int TicksThisTurn { get; private set; }
    public TurnPhase CurrentPhase { get; private set; } = TurnPhase.Player;
    public TurnPhase CombatOriginPhase { get; private set; } = TurnPhase.Player;
    public bool IsPlayerPhase => CurrentPhase == TurnPhase.Player;

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

    /// <summary>Requests the end of player input. Physical movement may finish first.</summary>
    public void EndTurn()
    {
        if (CurrentPhase != TurnPhase.Player)
        {
            return;
        }

        SetPhase(TurnPhase.WaitingForPlayerMovement);
        PlayerPhaseEnding?.Invoke(currentTurn);
    }

    public bool BeginEnemyPhase()
    {
        if (CurrentPhase != TurnPhase.WaitingForPlayerMovement)
        {
            return false;
        }

        SetPhase(TurnPhase.Enemy);
        EnemyPhaseStarted?.Invoke(currentTurn);
        return true;
    }

    public bool BeginCombat()
    {
        if (CurrentPhase != TurnPhase.Player &&
            CurrentPhase != TurnPhase.WaitingForPlayerMovement &&
            CurrentPhase != TurnPhase.Enemy)
        {
            return false;
        }

        CombatOriginPhase = CurrentPhase == TurnPhase.Enemy
            ? TurnPhase.Enemy
            : TurnPhase.Player;
        SetPhase(TurnPhase.Combat);
        return true;
    }

    /// <summary>
    /// Returns player-started combat to the same player phase. Enemy-started
    /// combat completes the enemy phase and starts a fresh player turn.
    /// </summary>
    public void CompleteCombat(
        bool playerDefeated = false,
        bool endPlayerPhase = false)
    {
        if (CurrentPhase != TurnPhase.Combat)
        {
            return;
        }

        if (playerDefeated)
        {
            SetPhase(TurnPhase.Defeat);
            return;
        }

        if (CombatOriginPhase == TurnPhase.Enemy)
        {
            CompleteEnemyPhaseInternal();
        }
        else if (endPlayerPhase)
        {
            SetPhase(TurnPhase.WaitingForPlayerMovement);
            PlayerPhaseEnding?.Invoke(currentTurn);
        }
        else
        {
            SetPhase(TurnPhase.Player);
        }
    }

    public void CompleteEnemyPhase()
    {
        if (CurrentPhase == TurnPhase.Enemy)
        {
            CompleteEnemyPhaseInternal();
        }
    }

    private void CompleteEnemyPhaseInternal()
    {
        int completedTurn = currentTurn;
        TurnEnded?.Invoke(completedTurn);
        currentTurn++;
        TicksThisTurn = 0;
        SetPhase(TurnPhase.Player);
        PlayerPhaseStarted?.Invoke(currentTurn);
    }

    public void ResetClock()
    {
        currentTurn = 1;
        CurrentTick = 0;
        TicksThisTurn = 0;
        CombatOriginPhase = TurnPhase.Player;
        SetPhase(TurnPhase.Player);
        ClockReset?.Invoke();
        PlayerPhaseStarted?.Invoke(currentTurn);
    }

    public int TurnsSince(int startTurn) => currentTurn - startTurn;
    public int TicksSince(int startTick) => CurrentTick - startTick;

    private void SetPhase(TurnPhase phase)
    {
        if (CurrentPhase == phase)
        {
            return;
        }

        CurrentPhase = phase;
        PhaseChanged?.Invoke(phase);
    }
}
