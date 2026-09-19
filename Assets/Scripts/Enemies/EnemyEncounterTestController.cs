using System;
using UnityEngine;

/// <summary>
/// Temporary encounter harness for validating the overworld-to-combat handoff.
/// Replace this component when the real combat scene/controller is ready.
/// </summary>
[AddComponentMenu("Derelict Deliveries/Enemies/Enemy Encounter Test Controller")]
[DisallowMultipleComponent]
public class EnemyEncounterTestController : MonoBehaviour
{
    public enum TestOutcome
    {
        Victory,
        Fled,
        Defeat
    }

    [Header("References")]

    [SerializeField]
    private EnemyTurnController enemyTurnController;

    [SerializeField]
    private PlayerShipState playerShip;

    [Header("Test Panel")]

    [SerializeField]
    private string encounterTitle = "ENEMY ENCOUNTER";

    [SerializeField]
    [Min(280.0f)]
    private float panelWidth = 420.0f;

    [SerializeField]
    [Min(180.0f)]
    private float panelHeight = 245.0f;

    private EnemyShip encounteredEnemy;
    private Vector3Int encounterCell;
    private Rect panelRect;
    private bool isShowingEncounter;

    /// <summary>
    /// Lets a future integration test or UI observe the selected test result.
    /// </summary>
    public event Action<EnemyShip, TestOutcome> EncounterResolved;

    public bool IsShowingEncounter
    {
        get { return isShowingEncounter; }
    }

    private void Reset()
    {
        ResolveReferences();
    }

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();

        if (enemyTurnController != null)
        {
            enemyTurnController.PlayerContactedEnemy += HandleEnemyContact;
        }
    }

    private void OnDisable()
    {
        if (enemyTurnController != null)
        {
            enemyTurnController.PlayerContactedEnemy -= HandleEnemyContact;
        }

        // Never leave movement locked if this temporary harness is removed
        // or disabled while its panel is open.
        if (isShowingEncounter && enemyTurnController != null)
        {
            enemyTurnController.ReleaseEncounter();
        }

        ClearEncounter();
    }

    private void OnGUI()
    {
        if (!isShowingEncounter)
        {
            return;
        }

        panelRect = new Rect(
            (Screen.width - panelWidth) * 0.5f,
            (Screen.height - panelHeight) * 0.5f,
            panelWidth,
            panelHeight
        );

        panelRect = GUI.ModalWindow(
            GetInstanceID(),
            panelRect,
            DrawEncounterWindow,
            encounterTitle
        );
    }

    private void DrawEncounterWindow(int windowId)
    {
        GUILayout.Space(10.0f);
        GUILayout.Label(
            encounteredEnemy != null
                ? $"Contact with {encounteredEnemy.name} at {encounterCell}."
                : $"Enemy contact at {encounterCell}."
        );
        GUILayout.Space(6.0f);
        GUILayout.Label(
            "Temporary combat test: choose an outcome to verify that the " +
            "overworld pauses and resumes correctly."
        );
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Victory — remove enemy", GUILayout.Height(32.0f)))
        {
            ResolveVictory();
        }

        if (GUILayout.Button("Flee — leave enemy nearby", GUILayout.Height(32.0f)))
        {
            ResolveFlee();
        }

        if (GUILayout.Button("Defeat — set hull to zero", GUILayout.Height(32.0f)))
        {
            ResolveDefeat();
        }

        GUILayout.Space(8.0f);
    }

    private void HandleEnemyContact(EnemyShip enemy, Vector3Int cell)
    {
        encounteredEnemy = enemy;
        encounterCell = cell;
        isShowingEncounter = true;
    }

    private void ResolveVictory()
    {
        EnemyShip resolvedEnemy = encounteredEnemy;

        CancelOldPlayerJourney();

        if (resolvedEnemy != null)
        {
            // Disabling first immediately unregisters the ship and removes its
            // fog reveal; Destroy completes safely at the end of the frame.
            resolvedEnemy.gameObject.SetActive(false);
            Destroy(resolvedEnemy.gameObject);
        }

        FinishEncounter(resolvedEnemy, TestOutcome.Victory);
    }

    private void ResolveFlee()
    {
        EnemyShip resolvedEnemy = encounteredEnemy;

        CancelOldPlayerJourney();
        FinishEncounter(resolvedEnemy, TestOutcome.Fled);
    }

    private void ResolveDefeat()
    {
        EnemyShip resolvedEnemy = encounteredEnemy;

        CancelOldPlayerJourney();

        if (playerShip != null && playerShip.Resources != null)
        {
            playerShip.Resources.SetHullIntegrity(0.0f);
        }

        FinishEncounter(resolvedEnemy, TestOutcome.Defeat);
    }

    private void FinishEncounter(EnemyShip enemy, TestOutcome outcome)
    {
        ClearEncounter();

        if (enemyTurnController != null)
        {
            enemyTurnController.ReleaseEncounter();
        }

        EncounterResolved?.Invoke(enemy, outcome);
    }

    private void CancelOldPlayerJourney()
    {
        if (playerShip != null)
        {
            playerShip.CancelCurrentJourney();
        }
    }

    private void ClearEncounter()
    {
        encounteredEnemy = null;
        isShowingEncounter = false;
    }

    private void ResolveReferences()
    {
        if (enemyTurnController == null)
        {
            enemyTurnController = GetComponent<EnemyTurnController>();
        }

        if (enemyTurnController == null)
        {
            enemyTurnController = FindFirstObjectByType<EnemyTurnController>();
        }

        if (playerShip == null)
        {
            playerShip = FindFirstObjectByType<PlayerShipState>();
        }
    }
}
