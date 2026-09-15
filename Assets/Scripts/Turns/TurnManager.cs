using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal turn tracker for the grid game.
///
/// A turn elapses in two ways:
/// - Automatically, once for every cell the player moves into (see
///   PlayerGridController.CellReached) - so moving already counts as taking your turn(s).
/// - Manually, via waitTurnButton - assign any UI Button here and TurnManager subscribes to
///   its click itself, for the case where a turn should pass without the player moving.
/// EndTurn() also stays public, for anything else that wants to elapse a turn in code.
///
/// Other systems that care about turns passing (fog decay, enemy moves, random events)
/// subscribe to TurnEnded rather than this script needing to know what they are.
/// </summary>
public class TurnManager : MonoBehaviour
{
    [Tooltip("Which turn number the game starts on.")]
    [SerializeField]
    private int currentTurn = 1;

    [Tooltip("Optional. Every cell this reports the player reaching counts as one turn automatically.")]
    [SerializeField]
    private PlayerGridController playerController;

    [Tooltip("Optional. Clicking this button elapses a turn without the player moving - for waiting/passing in place.")]
    [SerializeField]
    private Button waitTurnButton;


    /// <summary>Raised after the turn number has been incremented, however that happened.</summary>
    public event Action<int> TurnEnded;


    /// <summary>The turn number currently in progress.</summary>
    public int CurrentTurn
    {
        get
        {
            return currentTurn;
        }
    }


    private void OnEnable()
    {
        if (playerController != null)
        {
            playerController.CellReached += HandlePlayerCellReached;
        }

        if (waitTurnButton != null)
        {
            waitTurnButton.onClick.AddListener(EndTurn);
        }
    }


    private void OnDisable()
    {
        if (playerController != null)
        {
            playerController.CellReached -= HandlePlayerCellReached;
        }

        if (waitTurnButton != null)
        {
            waitTurnButton.onClick.RemoveListener(EndTurn);
        }
    }


    /// <summary>Every cell the player moves into elapses its own turn.</summary>
    private void HandlePlayerCellReached(Vector3Int cell)
    {
        EndTurn();
    }


    /// <summary>
    /// Advances the turn counter and notifies anything listening for a turn to have ended.
    /// Wired automatically to waitTurnButton's click if one is assigned; movement calls this
    /// too via the CellReached subscription above. Also fine to call directly from other code.
    /// </summary>
    public void EndTurn()
    {
        currentTurn++;

        TurnEnded?.Invoke(currentTurn);
    }
}