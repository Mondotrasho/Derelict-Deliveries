using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple test bridge between planet arrival and the dialogue UI.
///
/// Add this to any always-active GameObject in the scene, then assign:
/// - PlayerShipState
/// - PlanetManager
/// - DialoguePanelController
/// - one dialogue JSON for each test planet
///
/// The script listens to the player's real CellEntered event, looks up the
/// planet at that cell, pauses travel through the movement interruption API,
/// opens that planet's dialogue, then releases travel when the dialogue closes.
/// </summary>
public class PlanetDialogueTestTrigger : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private PlanetManager planetManager;
    [SerializeField] private DialoguePanelController dialoguePanel;

    [Header("Dialogue Behaviour")]
    [Tooltip("Run the dialogue terminal boot sequence each time a planet conversation opens.")]
    [SerializeField] private bool runBootSequence;

    [Tooltip("If enabled, each planet only triggers once for this component's lifetime.")]
    [SerializeField] private bool triggerEachPlanetOnlyOnce;

    [Header("Optional Input Lock")]
    [Tooltip(
        "Optional gameplay input components to disable while dialogue is open. " +
        "Movement itself is already paused through PlayerShipState.")]
    [SerializeField] private Behaviour[] disableWhileDialogueOpen;

    [Header("Yellow Planet")]
    [SerializeField] private string yellowPlanetId = "Yellow_Planet";
    [SerializeField] private TextAsset yellowPlanetDialogue;

    [Header("Grey Planet")]
    [SerializeField] private string greyPlanetId = "Grey_Planet";
    [SerializeField] private TextAsset greyPlanetDialogue;

    [Header("Red Planet")]
    [SerializeField] private string redPlanetId = "Red_planet";
    [SerializeField] private TextAsset redPlanetDialogue;

    [Header("Red Planet Moon")]
    [SerializeField] private string redMoonId = "Red_planet_moon";
    [SerializeField] private TextAsset redMoonDialogue;

    [Header("Blue / Fourth Planet")]
    [Tooltip(
        "Your current Inspector screenshot shows this planet ID as Green_Planet. " +
        "Change this to Blue_Planet here if you rename the planet data.")]
    [SerializeField] private string bluePlanetId = "Green_Planet";
    [SerializeField] private TextAsset bluePlanetDialogue;

    private readonly HashSet<string> triggeredPlanetIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Behaviour, bool> previousBehaviourStates =
        new Dictionary<Behaviour, bool>();

    private MovementInterruptionHandle activeInterruption;
    private Coroutine activeDialogueRoutine;

    private void OnEnable()
    {
        if (player != null)
        {
            player.CellEntered += HandlePlayerEnteredCell;
        }
    }

    private void OnDisable()
    {
        if (player != null)
        {
            player.CellEntered -= HandlePlayerEnteredCell;
        }

        if (activeDialogueRoutine != null)
        {
            StopCoroutine(activeDialogueRoutine);
            activeDialogueRoutine = null;
        }

        RestoreInputBehaviours();
        ReleaseMovementInterruption();

        if (dialoguePanel != null && dialoguePanel.IsDialogueOpen)
        {
            dialoguePanel.CloseDialogue();
        }
    }

    private void HandlePlayerEnteredCell(Vector3Int cell)
    {
        // Do not stack another conversation over one already running.
        if (activeDialogueRoutine != null)
        {
            return;
        }

        if (planetManager == null)
        {
            Debug.LogWarning(
                "PlanetDialogueTestTrigger has no PlanetManager assigned.",
                this);
            return;
        }

        if (!planetManager.TryGetPlanetAtCell(cell, out Planet planet) || planet == null)
        {
            return;
        }

        TextAsset dialogue = GetDialogueForPlanet(planet.id);

        if (dialogue == null)
        {
            // This is useful while testing because it confirms the arrival
            // API worked even if a JSON has not been assigned yet.
            Debug.Log(
                $"Arrived at planet '{planet.id}', but no test dialogue is assigned.",
                this);
            return;
        }

        if (triggerEachPlanetOnlyOnce && triggeredPlanetIds.Contains(planet.id))
        {
            return;
        }

        activeDialogueRoutine = StartCoroutine(
            RunPlanetDialogue(planet.id, dialogue));
    }

    private IEnumerator RunPlanetDialogue(string planetId, TextAsset dialogue)
    {
        if (player == null || dialoguePanel == null || dialogue == null)
        {
            Debug.LogWarning(
                "Planet dialogue test is missing PlayerShipState, DialoguePanelController or dialogue JSON.",
                this);

            activeDialogueRoutine = null;
            yield break;
        }

        if (triggerEachPlanetOnlyOnce)
        {
            triggeredPlanetIds.Add(planetId);
        }

        // This is the core travel interruption API. The existing physical
        // route is paused and can resume when this handle is released.
        activeInterruption =
            player.AcquireMovementInterruption($"Planet dialogue: {planetId}");

        DisableInputBehaviours();

        Debug.Log($"Opening test dialogue for planet '{planetId}'.", this);

        // DialoguePanelController handles activating itself even if its
        // GameObject started disabled. This waits until QUIT/CLOSE is pressed.
        yield return dialoguePanel.OpenDialogueAndWait(
            dialogue,
            runBootSequence);

        RestoreInputBehaviours();
        ReleaseMovementInterruption();

        Debug.Log($"Closed test dialogue for planet '{planetId}'.", this);

        activeDialogueRoutine = null;
    }

    private TextAsset GetDialogueForPlanet(string planetId)
    {
        if (IdMatches(planetId, yellowPlanetId))
        {
            return yellowPlanetDialogue;
        }

        if (IdMatches(planetId, greyPlanetId))
        {
            return greyPlanetDialogue;
        }

        if (IdMatches(planetId, redPlanetId))
        {
            return redPlanetDialogue;
        }

        if (IdMatches(planetId, redMoonId))
        {
            return redMoonDialogue;
        }

        if (IdMatches(planetId, bluePlanetId))
        {
            return bluePlanetDialogue;
        }

        return null;
    }

    private bool IdMatches(string actualId, string configuredId)
    {
        return !string.IsNullOrWhiteSpace(actualId) &&
               !string.IsNullOrWhiteSpace(configuredId) &&
               string.Equals(
                   actualId,
                   configuredId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void DisableInputBehaviours()
    {
        previousBehaviourStates.Clear();

        if (disableWhileDialogueOpen == null)
        {
            return;
        }

        foreach (Behaviour behaviour in disableWhileDialogueOpen)
        {
            if (behaviour == null)
            {
                continue;
            }

            previousBehaviourStates[behaviour] = behaviour.enabled;
            behaviour.enabled = false;
        }
    }

    private void RestoreInputBehaviours()
    {
        foreach (KeyValuePair<Behaviour, bool> entry in previousBehaviourStates)
        {
            if (entry.Key != null)
            {
                entry.Key.enabled = entry.Value;
            }
        }

        previousBehaviourStates.Clear();
    }

    private void ReleaseMovementInterruption()
    {
        if (activeInterruption == null)
        {
            return;
        }

        activeInterruption.Release();
        activeInterruption = null;
    }

    // ---------------------------------------------------------------------
    // Manual Inspector / UnityEvent test calls
    // ---------------------------------------------------------------------

    public void TestYellowPlanet()
    {
        StartManualDialogue(yellowPlanetId, yellowPlanetDialogue);
    }

    public void TestGreyPlanet()
    {
        StartManualDialogue(greyPlanetId, greyPlanetDialogue);
    }

    public void TestRedPlanet()
    {
        StartManualDialogue(redPlanetId, redPlanetDialogue);
    }

    public void TestRedMoon()
    {
        StartManualDialogue(redMoonId, redMoonDialogue);
    }

    public void TestBluePlanet()
    {
        StartManualDialogue(bluePlanetId, bluePlanetDialogue);
    }

    private void StartManualDialogue(string planetId, TextAsset dialogue)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning(
                "Planet dialogue tests only run in Play Mode.",
                this);
            return;
        }

        if (activeDialogueRoutine != null)
        {
            Debug.LogWarning(
                "A planet dialogue is already open.",
                this);
            return;
        }

        if (dialogue == null)
        {
            Debug.LogWarning(
                $"No dialogue JSON is assigned for '{planetId}'.",
                this);
            return;
        }

        activeDialogueRoutine = StartCoroutine(
            RunPlanetDialogue(planetId, dialogue));
    }

    [ContextMenu("Test Dialogue/Yellow Planet")]
    private void ContextTestYellowPlanet()
    {
        TestYellowPlanet();
    }

    [ContextMenu("Test Dialogue/Grey Planet")]
    private void ContextTestGreyPlanet()
    {
        TestGreyPlanet();
    }

    [ContextMenu("Test Dialogue/Red Planet")]
    private void ContextTestRedPlanet()
    {
        TestRedPlanet();
    }

    [ContextMenu("Test Dialogue/Red Planet Moon")]
    private void ContextTestRedMoon()
    {
        TestRedMoon();
    }

    [ContextMenu("Test Dialogue/Blue / Fourth Planet")]
    private void ContextTestBluePlanet()
    {
        TestBluePlanet();
    }
}
