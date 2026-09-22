using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns one map-triggered combat encounter. Enemy actions are committed and
/// hidden before player input. Presentation only submits actions and observes
/// state/events; all rolls, damage, shields and outcomes live here.
/// </summary>
[AddComponentMenu("Derelict Deliveries/Combat/Combat Encounter Controller")]
[DisallowMultipleComponent]
public class CombatEncounterController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private EnemyTurnController enemyTurnController;

    [SerializeField]
    private PlayerShipState playerShip;

    [Header("Player Combat Statistics")]

    [SerializeField]
    private CombatCapabilities playerCapabilities = new CombatCapabilities();

    [Header("Contested Rolls")]

    [Range(0.0f, 1.0f)]
    [SerializeField]
    private float minimumContestChance = 0.1f;

    [Range(0.0f, 1.0f)]
    [SerializeField]
    private float maximumContestChance = 0.9f;

    [Min(1.0f)]
    [Tooltip("Target maneuverability multiplier while actively evading.")]
    [SerializeField]
    private float evadeManeuverabilityMultiplier = 1.5f;

    [Min(0.0f)]
    [Tooltip("Added to next-round initiative after a successful Evade.")]
    [SerializeField]
    private float successfulEvadeInitiativeBonus = 20.0f;

    [Header("Damage Reduction")]

    [Range(0.0f, 0.95f)]
    [SerializeField]
    private float baseDefendReduction = 0.25f;

    [Min(0.01f)]
    [SerializeField]
    private float defendArmourScale = 50.0f;

    [Range(0.0f, 0.95f)]
    [SerializeField]
    private float maximumDefendArmourBonus = 0.35f;

    [Range(0.0f, 0.95f)]
    [SerializeField]
    private float maximumDefendReduction = 0.7f;

    [Min(0.01f)]
    [Tooltip("Higher values make passive armour reduction less aggressive.")]
    [SerializeField]
    private float passiveArmourScale = 100.0f;

    [Header("Shield Recharge")]

    [Min(1.0f)]
    [Tooltip("Recharge multiplier when Defending and not attacked that round.")]
    [SerializeField]
    private float defendRechargeMultiplier = 2.0f;

    public event Action<EnemyShip> EncounterStarted;
    public event Action<CombatRoundResult> RoundResolved;
    public event Action<EnemyShip, CombatEncounterOutcome, int> EncounterEnded;

    public bool IsEncounterActive { get; private set; }
    public EnemyShip CurrentEnemy { get; private set; }
    public EnemyCombatState CurrentEnemyState { get; private set; }
    public CombatRoundResult LastRoundResult { get; private set; }
    public CombatCapabilities PlayerCapabilities => playerCapabilities;
    public float PlayerCurrentShields { get; private set; }
    public float PlayerMaxShields => playerCapabilities?.MaxShields ?? 0.0f;

    public float PlayerHull => PlayerResources != null
        ? PlayerResources.HullIntegrity
        : 0.0f;

    public float PlayerMaxHull => PlayerResources != null
        ? PlayerResources.MaxHullIntegrity
        : 0.0f;

    public float PlayerFireHitChance => CurrentEnemyState != null
        ? CombatResolver.CalculateContestChance(
            playerCapabilities.WeaponAccuracy,
            CurrentEnemyState.Capabilities.Maneuverability,
            minimumContestChance,
            maximumContestChance)
        : 0.0f;

    public float PlayerEvadeChance => CurrentEnemyState != null
        ? 1.0f - CombatResolver.CalculateContestChance(
            CurrentEnemyState.Capabilities.WeaponAccuracy,
            playerCapabilities.Maneuverability *
            evadeManeuverabilityMultiplier,
            minimumContestChance,
            maximumContestChance)
        : 0.0f;

    public float PlayerEscapeChance => CurrentEnemyState != null
        ? CombatResolver.CalculateContestChance(
            playerCapabilities.Maneuverability,
            CurrentEnemyState.Capabilities.Maneuverability,
            minimumContestChance,
            maximumContestChance)
        : 0.0f;

    public float PlayerDefendReduction =>
        CombatResolver.CalculateDefendReduction(
            playerCapabilities.Armour,
            baseDefendReduction,
            defendArmourScale,
            maximumDefendArmourBonus,
            maximumDefendReduction
        );

    private ShipResources PlayerResources => playerShip != null
        ? playerShip.Resources
        : null;

    private CombatAction committedEnemyAction;
    private float playerInitiativeBonus;
    private float enemyInitiativeBonus;

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

        if (IsEncounterActive)
        {
            RestoreShieldsAfterCombat();
            enemyTurnController?.ReleaseEncounter();
        }

        ClearEncounterState();
    }

    private void OnValidate()
    {
        playerCapabilities ??= new CombatCapabilities();
        playerCapabilities.ClampValues();
        minimumContestChance = Mathf.Clamp01(minimumContestChance);
        maximumContestChance = Mathf.Clamp(
            maximumContestChance,
            minimumContestChance,
            1.0f
        );
        evadeManeuverabilityMultiplier =
            Mathf.Max(1.0f, evadeManeuverabilityMultiplier);
        successfulEvadeInitiativeBonus =
            Mathf.Max(0.0f, successfulEvadeInitiativeBonus);
        defendArmourScale = Mathf.Max(0.01f, defendArmourScale);
        passiveArmourScale = Mathf.Max(0.01f, passiveArmourScale);
        defendRechargeMultiplier = Mathf.Max(1.0f, defendRechargeMultiplier);
    }

    public bool SubmitPlayerAction(CombatAction playerAction)
    {
        if (!IsEncounterActive || CurrentEnemyState == null ||
            PlayerResources == null ||
            playerAction == CombatAction.Special)
        {
            return false;
        }

        if (playerAction == CombatAction.Flee)
        {
            ResolveFleeAttempt();
            return true;
        }

        ResolveStandardRound(playerAction);
        return true;
    }

    private void ResolveStandardRound(CombatAction playerAction)
    {
        CombatRoundResult result = new CombatRoundResult(
            playerAction,
            committedEnemyAction
        );
        List<string> summary = new List<string>();

        float initiativeChance = CombatResolver.CalculateContestChance(
            playerCapabilities.Maneuverability + playerInitiativeBonus,
            CurrentEnemyState.Capabilities.Maneuverability +
            enemyInitiativeBonus,
            minimumContestChance,
            maximumContestChance
        );

        result.PlayerActedFirst = UnityEngine.Random.value < initiativeChance;
        playerInitiativeBonus = 0.0f;
        enemyInitiativeBonus = 0.0f;

        if (result.PlayerActedFirst)
        {
            ResolvePlayerAction(result, summary);

            if (!CurrentEnemyState.IsDestroyed)
            {
                ResolveEnemyAction(result, summary);
            }
        }
        else
        {
            ResolveEnemyAction(result, summary);

            if (PlayerResources.HullIntegrity > 0.0f)
            {
                ResolvePlayerAction(result, summary);
            }
        }

        ApplyEvadeInitiativeBonuses(result, playerAction, committedEnemyAction);
        ApplyEndOfRoundShieldRecharge(result, playerAction, committedEnemyAction);

        result.Summary = string.Join(" ", summary);
        PublishRound(result);

        if (CurrentEnemyState.IsDestroyed)
        {
            FinishEncounter(CombatEncounterOutcome.Victory, true);
        }
        else if (PlayerResources.HullIntegrity <= 0.0f)
        {
            FinishEncounter(CombatEncounterOutcome.Defeat, false);
        }
        else
        {
            CommitNextEnemyAction();
        }
    }

    private void ResolvePlayerAction(
        CombatRoundResult result,
        List<string> summary)
    {
        if (result.PlayerAction != CombatAction.Fire)
        {
            return;
        }

        result.PlayerAttackAttempted = true;
        float targetManeuverability =
            CurrentEnemyState.Capabilities.Maneuverability;

        if (result.EnemyAction == CombatAction.Evade)
        {
            targetManeuverability *= evadeManeuverabilityMultiplier;
        }

        result.PlayerHitChance = CombatResolver.CalculateContestChance(
            playerCapabilities.WeaponAccuracy,
            targetManeuverability,
            minimumContestChance,
            maximumContestChance
        );
        result.PlayerHitRoll = UnityEngine.Random.value;
        result.PlayerAttackHit =
            result.PlayerHitRoll < result.PlayerHitChance;

        if (!result.PlayerAttackHit)
        {
            summary.Add(
                $"Player fire missed ({result.PlayerHitChance:P0} chance)."
            );
            return;
        }

        CombatResolver.DamageResult damage = CombatResolver.ResolveDamage(
            playerCapabilities.WeaponDamage,
            CurrentEnemyState.CurrentShields,
            CurrentEnemyState.Capabilities.Armour,
            result.EnemyAction == CombatAction.Defend,
            baseDefendReduction,
            defendArmourScale,
            maximumDefendArmourBonus,
            maximumDefendReduction,
            passiveArmourScale
        );

        result.DamageToEnemyShields =
            CurrentEnemyState.ApplyShieldDamage(damage.ShieldDamage);
        result.DamageToEnemyHull =
            CurrentEnemyState.ApplyHullDamage(damage.HullDamage);

        summary.Add(BuildDamageSummary(
            "Player fire hit",
            result.DamageToEnemyShields,
            result.DamageToEnemyHull,
            damage.DefendReduction
        ));
    }

    private void ResolveEnemyAction(
        CombatRoundResult result,
        List<string> summary)
    {
        if (result.EnemyAction != CombatAction.Fire)
        {
            return;
        }

        result.EnemyAttackAttempted = true;
        float targetManeuverability = playerCapabilities.Maneuverability;

        if (result.PlayerAction == CombatAction.Evade)
        {
            targetManeuverability *= evadeManeuverabilityMultiplier;
        }

        result.EnemyHitChance = CombatResolver.CalculateContestChance(
            CurrentEnemyState.Capabilities.WeaponAccuracy,
            targetManeuverability,
            minimumContestChance,
            maximumContestChance
        );
        result.EnemyHitRoll = UnityEngine.Random.value;
        result.EnemyAttackHit =
            result.EnemyHitRoll < result.EnemyHitChance;

        if (!result.EnemyAttackHit)
        {
            summary.Add(
                $"Enemy fire missed ({result.EnemyHitChance:P0} chance)."
            );
            return;
        }

        CombatResolver.DamageResult damage = CombatResolver.ResolveDamage(
            CurrentEnemyState.Capabilities.WeaponDamage,
            PlayerCurrentShields,
            playerCapabilities.Armour,
            result.PlayerAction == CombatAction.Defend,
            baseDefendReduction,
            defendArmourScale,
            maximumDefendArmourBonus,
            maximumDefendReduction,
            passiveArmourScale
        );

        result.DamageToPlayerShields = ApplyPlayerShieldDamage(
            damage.ShieldDamage
        );
        result.DamageToPlayerHull = PlayerResources.ApplyHullDamage(
            damage.HullDamage
        );

        summary.Add(BuildDamageSummary(
            "Enemy fire hit",
            result.DamageToPlayerShields,
            result.DamageToPlayerHull,
            damage.DefendReduction
        ));
    }

    private void ResolveFleeAttempt()
    {
        float escapeChance = CombatResolver.CalculateContestChance(
            playerCapabilities.Maneuverability,
            CurrentEnemyState.Capabilities.Maneuverability,
            minimumContestChance,
            maximumContestChance
        );
        float escapeRoll = UnityEngine.Random.value;
        bool escaped = escapeRoll < escapeChance;
        CombatAction reportedEnemyAction = escaped
            ? committedEnemyAction
            : CombatAction.Fire;
        CombatRoundResult result = new CombatRoundResult(
            CombatAction.Flee,
            reportedEnemyAction
        )
        {
            EscapeAttempted = true,
            EscapeSucceeded = escaped,
            EscapeChance = escapeChance,
            EscapeRoll = escapeRoll
        };

        if (escaped)
        {
            result.Summary =
                $"Escape succeeded ({escapeChance:P0} chance).";
            PublishRound(result);
            enemyTurnController?.GrantFleeGrace(CurrentEnemy);
            FinishEncounter(CombatEncounterOutcome.Fled, false);
            return;
        }

        List<string> summary = new List<string>
        {
            $"Escape failed ({escapeChance:P0} chance)."
        };

        ResolveEnemyAction(result, summary);

        if (!result.PlayerAttackAttempted)
        {
            result.EnemyShieldRecharge =
                CurrentEnemyState.RechargeShields();
        }

        result.Summary = string.Join(" ", summary);
        PublishRound(result);

        if (PlayerResources.HullIntegrity <= 0.0f)
        {
            FinishEncounter(CombatEncounterOutcome.Defeat, false);
        }
        else
        {
            CommitNextEnemyAction();
        }
    }

    private void ApplyEvadeInitiativeBonuses(
        CombatRoundResult result,
        CombatAction playerAction,
        CombatAction enemyAction)
    {
        if (playerAction == CombatAction.Evade &&
            (!result.EnemyAttackAttempted || !result.EnemyAttackHit))
        {
            playerInitiativeBonus = successfulEvadeInitiativeBonus;
        }

        if (enemyAction == CombatAction.Evade &&
            (!result.PlayerAttackAttempted || !result.PlayerAttackHit))
        {
            enemyInitiativeBonus = successfulEvadeInitiativeBonus;
        }
    }

    private void ApplyEndOfRoundShieldRecharge(
        CombatRoundResult result,
        CombatAction playerAction,
        CombatAction enemyAction)
    {
        // An attack attempt suppresses recharge for this round even when it
        // misses. Recharge resumes after a complete round without attack.
        if (!result.EnemyAttackAttempted)
        {
            float multiplier = playerAction == CombatAction.Defend
                ? defendRechargeMultiplier
                : 1.0f;
            result.PlayerShieldRecharge = RechargePlayerShields(multiplier);
        }

        if (!result.PlayerAttackAttempted)
        {
            float multiplier = enemyAction == CombatAction.Defend
                ? defendRechargeMultiplier
                : 1.0f;
            result.EnemyShieldRecharge =
                CurrentEnemyState.RechargeShields(multiplier);
        }
    }

    private void HandleEnemyContact(EnemyShip enemy, Vector3Int cell)
    {
        if (IsEncounterActive || enemy == null)
        {
            return;
        }

        if (PlayerResources == null)
        {
            Debug.LogError(
                "Combat cannot start because the player has no ShipResources.",
                this
            );
            enemyTurnController?.ReleaseEncounter();
            return;
        }

        EnemyCombatState enemyState = enemy.GetComponent<EnemyCombatState>();

        if (enemyState == null)
        {
            enemyState = enemy.gameObject.AddComponent<EnemyCombatState>();
            Debug.LogWarning(
                $"{enemy.name} had no EnemyCombatState. Runtime defaults were added.",
                enemy
            );
        }

        CurrentEnemy = enemy;
        CurrentEnemyState = enemyState;
        LastRoundResult = null;
        playerInitiativeBonus = 0.0f;
        enemyInitiativeBonus = 0.0f;
        PlayerCurrentShields = playerCapabilities.MaxShields;
        CurrentEnemyState.RestoreShieldsToFull();
        IsEncounterActive = true;

        playerShip.CancelCurrentJourney();
        CommitNextEnemyAction();
        EncounterStarted?.Invoke(enemy);

        Debug.Log(
            $"Combat started with {enemyState.DisplayName} at {cell}.",
            this
        );
    }

    private void FinishEncounter(
        CombatEncounterOutcome outcome,
        bool removeEnemy)
    {
        EnemyShip resolvedEnemy = CurrentEnemy;
        int rewardValue = CurrentEnemyState != null
            ? CurrentEnemyState.RewardValue
            : 0;

        RestoreShieldsAfterCombat();

        if (removeEnemy && resolvedEnemy != null)
        {
            resolvedEnemy.gameObject.SetActive(false);
            Destroy(resolvedEnemy.gameObject);
        }

        ClearEncounterState();
        enemyTurnController?.ReleaseEncounter(
            outcome == CombatEncounterOutcome.Defeat,
            outcome == CombatEncounterOutcome.Fled
        );
        EncounterEnded?.Invoke(resolvedEnemy, outcome, rewardValue);
        Debug.Log($"Combat ended: {outcome}.", this);
    }

    private void PublishRound(CombatRoundResult result)
    {
        LastRoundResult = result;
        RoundResolved?.Invoke(result);
        Debug.Log(result.Summary, this);
    }

    private void CommitNextEnemyAction()
    {
        committedEnemyAction = CurrentEnemyState != null
            ? CurrentEnemyState.ChooseHiddenAction(UnityEngine.Random.value)
            : CombatAction.Fire;
    }

    private float ApplyPlayerShieldDamage(float amount)
    {
        float oldValue = PlayerCurrentShields;
        PlayerCurrentShields = Mathf.Max(0.0f, PlayerCurrentShields - amount);
        return oldValue - PlayerCurrentShields;
    }

    private float RechargePlayerShields(float multiplier)
    {
        float oldValue = PlayerCurrentShields;
        PlayerCurrentShields = Mathf.Clamp(
            PlayerCurrentShields +
            playerCapabilities.ShieldRecharge * Mathf.Max(0.0f, multiplier),
            0.0f,
            playerCapabilities.MaxShields
        );
        return PlayerCurrentShields - oldValue;
    }

    private void RestoreShieldsAfterCombat()
    {
        PlayerCurrentShields = playerCapabilities?.MaxShields ?? 0.0f;
        CurrentEnemyState?.RestoreShieldsToFull();
    }

    private static string BuildDamageSummary(
        string prefix,
        float shieldDamage,
        float hullDamage,
        float defendReduction)
    {
        string defended = defendReduction > 0.0f
            ? $" Defend reduced damage by {defendReduction:P0}."
            : string.Empty;

        return $"{prefix}: {shieldDamage:0.#} shield, " +
               $"{hullDamage:0.#} hull.{defended}";
    }

    private void ClearEncounterState()
    {
        IsEncounterActive = false;
        CurrentEnemy = null;
        CurrentEnemyState = null;
        playerInitiativeBonus = 0.0f;
        enemyInitiativeBonus = 0.0f;
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
