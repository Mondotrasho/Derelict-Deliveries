using UnityEngine;

/// <summary>
/// Temporary IMGUI presentation for exercising CombatEncounterController.
/// It contains no combat rules and can be removed when the final UI exists.
/// </summary>
[AddComponentMenu("Derelict Deliveries/Combat/Combat Debug Panel")]
[DisallowMultipleComponent]
public class EnemyEncounterTestController : MonoBehaviour
{
    [Header("Reference")]

    [SerializeField]
    private CombatEncounterController combatController;

    [Header("Debug Panel")]

    [SerializeField]
    private bool showDebugPanel = true;

    [SerializeField]
    private string encounterTitle = "COMBAT DEBUG UI";

    [SerializeField]
    [Min(360.0f)]
    private float panelWidth = 480.0f;

    [SerializeField]
    [Min(420.0f)]
    private float panelHeight = 560.0f;

    private Rect panelRect;
    private string resolutionMessage;

    private void Reset()
    {
        combatController = GetComponent<CombatEncounterController>();
    }

    private void Awake()
    {
        ResolveController();
    }

    private void OnEnable()
    {
        ResolveController();

        if (combatController == null)
        {
            return;
        }

        combatController.EncounterStarted += HandleEncounterStarted;
        combatController.EncounterEnded += HandleEncounterEnded;
    }

    private void OnDisable()
    {
        if (combatController == null)
        {
            return;
        }

        combatController.EncounterStarted -= HandleEncounterStarted;
        combatController.EncounterEnded -= HandleEncounterEnded;
    }

    private void OnGUI()
    {
        if (!showDebugPanel || combatController == null ||
            (!combatController.IsEncounterActive &&
             string.IsNullOrEmpty(resolutionMessage)))
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
            DrawWindow,
            encounterTitle
        );
    }

    private void DrawWindow(int windowId)
    {
        GUILayout.Space(8.0f);
        GUILayout.Label(
            "Temporary tester display - final combat UI is not implemented."
        );
        GUILayout.Space(8.0f);

        if (!combatController.IsEncounterActive)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(resolutionMessage);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Continue", GUILayout.Height(36.0f)))
            {
                resolutionMessage = string.Empty;
            }

            GUILayout.Space(8.0f);
            return;
        }

        DrawCombatState();
        GUILayout.Space(10.0f);
        DrawLastRound();
        GUILayout.FlexibleSpace();
        DrawActionButtons();
        GUILayout.Space(8.0f);
    }

    private void DrawCombatState()
    {
        EnemyCombatState enemy = combatController.CurrentEnemyState;

        GUILayout.Label(
            $"PLAYER HULL: {combatController.PlayerHull:0.#} / " +
            $"{combatController.PlayerMaxHull:0.#}"
        );
        GUILayout.Label(
            $"PLAYER SHIELDS: {combatController.PlayerCurrentShields:0.#} / " +
            $"{combatController.PlayerMaxShields:0.#}"
        );

        if (enemy == null)
        {
            GUILayout.Label("ENEMY: unavailable");
            return;
        }

        GUILayout.Label($"ENEMY: {enemy.DisplayName}");
        GUILayout.Label(
            $"ENEMY HULL: {enemy.CurrentHull:0.#} / {enemy.MaxHull:0.#}"
        );
        GUILayout.Label(
            $"ENEMY SHIELDS: {enemy.CurrentShields:0.#} / " +
            $"{enemy.MaxShields:0.#}"
        );

        CombatCapabilities player = combatController.PlayerCapabilities;
        GUILayout.Label(
            $"Player - Damage {player.WeaponDamage:0.#}, " +
            $"Accuracy {player.WeaponAccuracy:0.#}, " +
            $"Armour {player.Armour:0.#}, " +
            $"Maneuverability {player.Maneuverability:0.#}"
        );

        GUILayout.Label(
            $"Enemy - Damage {enemy.Capabilities.WeaponDamage:0.#}, " +
            $"Accuracy {enemy.Capabilities.WeaponAccuracy:0.#}, " +
            $"Armour {enemy.Capabilities.Armour:0.#}, " +
            $"Maneuverability {enemy.Capabilities.Maneuverability:0.#}"
        );
    }

    private void DrawLastRound()
    {
        CombatRoundResult result = combatController.LastRoundResult;

        if (result == null)
        {
            GUILayout.Label(
                "Enemy action is hidden. Fire deals damage, Defend reliably " +
                "reduces damage, and Evade contests accuracy with maneuverability."
            );
            return;
        }

        GUILayout.Label(
            $"Last choices - Player: {result.PlayerAction}, " +
            $"Enemy: {result.EnemyAction}"
        );
        GUILayout.Label(result.Summary);

        if (result.PlayerAttackAttempted)
        {
            GUILayout.Label(
                $"Player hit roll: {result.PlayerHitRoll:P0} / " +
                $"{result.PlayerHitChance:P0} chance"
            );
        }

        if (result.EnemyAttackAttempted)
        {
            GUILayout.Label(
                $"Enemy hit roll: {result.EnemyHitRoll:P0} / " +
                $"{result.EnemyHitChance:P0} chance"
            );
        }

        if (result.EscapeAttempted)
        {
            GUILayout.Label(
                $"Escape roll: {result.EscapeRoll:P0} / " +
                $"{result.EscapeChance:P0} chance"
            );
        }

        if (result.PlayerShieldRecharge > 0.0f ||
            result.EnemyShieldRecharge > 0.0f)
        {
            GUILayout.Label(
                $"Shield recharge - Player {result.PlayerShieldRecharge:0.#}, " +
                $"Enemy {result.EnemyShieldRecharge:0.#}"
            );
        }
    }

    private void DrawActionButtons()
    {
        GUILayout.Label("SELECT ACTION");

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(
                $"Fire ({combatController.PlayerFireHitChance:P0} hit)",
                GUILayout.Height(38.0f)))
        {
            combatController.SubmitPlayerAction(CombatAction.Fire);
        }

        if (GUILayout.Button(
                $"Defend ({combatController.PlayerDefendReduction:P0} reduction)",
                GUILayout.Height(38.0f)))
        {
            combatController.SubmitPlayerAction(CombatAction.Defend);
        }

        if (GUILayout.Button(
                $"Evade ({combatController.PlayerEvadeChance:P0})",
                GUILayout.Height(38.0f)))
        {
            combatController.SubmitPlayerAction(CombatAction.Evade);
        }

        GUILayout.EndHorizontal();

        if (GUILayout.Button(
                $"Flee ({combatController.PlayerEscapeChance:P0} chance)",
                GUILayout.Height(38.0f)))
        {
            combatController.SubmitPlayerAction(CombatAction.Flee);
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = false;
        GUILayout.Button(
            "Special - requires an equipped Officer ability",
            GUILayout.Height(32.0f)
        );
        GUI.enabled = oldEnabled;
    }

    private void HandleEncounterStarted(EnemyShip enemy)
    {
        resolutionMessage = string.Empty;
    }

    private void HandleEncounterEnded(
        EnemyShip enemy,
        CombatEncounterOutcome outcome,
        int rewardValue)
    {
        switch (outcome)
        {
            case CombatEncounterOutcome.Victory:
                resolutionMessage =
                    $"VICTORY\nEnemy destroyed. Reward value {rewardValue} " +
                    "was reported, but rewards are not granted yet.";
                break;
            case CombatEncounterOutcome.Fled:
                resolutionMessage =
                    "ESCAPED\nThe enemy remains nearby on the system map.";
                break;
            case CombatEncounterOutcome.Defeat:
                resolutionMessage =
                    "DEFEAT\nPlayer hull reached zero. Crew loss and game-over " +
                    "handling are not implemented yet.";
                break;
            default:
                resolutionMessage = "Combat was interrupted.";
                break;
        }
    }

    private void ResolveController()
    {
        if (combatController == null)
        {
            combatController = GetComponent<CombatEncounterController>();
        }

        // Keeps existing authored scenes functional without requiring a scene
        // YAML migration. Add the component explicitly later for tuning.
        if (combatController == null && Application.isPlaying)
        {
            combatController = gameObject.AddComponent<CombatEncounterController>();
        }
    }
}
