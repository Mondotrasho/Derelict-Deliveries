using System;
using UnityEngine;

/// <summary>Runtime combat state and authored capabilities for one enemy.</summary>
[AddComponentMenu("Derelict Deliveries/Combat/Enemy Combat State")]
[DisallowMultipleComponent]
[RequireComponent(typeof(EnemyShip))]
public class EnemyCombatState : MonoBehaviour
{
    [Header("Identity")]

    [Tooltip("Optional display name. The GameObject name is used when empty.")]
    [SerializeField]
    private string displayName = "Pursuit Ship";

    [Header("Hull")]

    [Min(1.0f)]
    [SerializeField]
    private float maxHull = 60.0f;

    [Header("Stance Strengths")]

    [SerializeField]
    private CombatCapabilities capabilities = new CombatCapabilities(
        weaponDamage: 15.0f,
        weaponAccuracy: 42.0f,
        armour: 10.0f,
        maxShields: 25.0f,
        shieldRecharge: 5.0f,
        maneuverability: 42.0f
    );

    [Header("Hidden Action Weights")]

    [Min(0.0f)]
    [SerializeField]
    private float fireWeight = 60.0f;

    [Min(0.0f)]
    [SerializeField]
    private float defendWeight = 25.0f;

    [Min(0.0f)]
    [SerializeField]
    private float evadeWeight = 15.0f;

    [Header("Future Reward Hook")]

    [Min(0)]
    [Tooltip("Reported on victory. No inventory resource is granted yet.")]
    [SerializeField]
    private int rewardValue = 10;

    private float currentHull;
    private float currentShields;
    private bool isInitialised;

    public event Action<float, float> HullChanged;

    public string DisplayName => string.IsNullOrWhiteSpace(displayName)
        ? gameObject.name
        : displayName.Trim();

    public float CurrentHull => currentHull;
    public float MaxHull => maxHull;
    public float HullFraction => maxHull > 0.0f ? currentHull / maxHull : 0.0f;
    public bool IsDestroyed => currentHull <= 0.0f;
    public float CurrentShields => currentShields;
    public float MaxShields => capabilities?.MaxShields ?? 0.0f;
    public float ShieldFraction => MaxShields > 0.0f
        ? currentShields / MaxShields
        : 0.0f;
    public CombatCapabilities Capabilities => capabilities;
    public int RewardValue => rewardValue;

    private void Awake()
    {
        ApplyAttachedDefinition();
        InitialiseIfNeeded();
    }

    private void ApplyAttachedDefinition()
    {
        EnemyShip enemyShip = GetComponent<EnemyShip>();
        EnemyShipDefinition definition = enemyShip != null
            ? enemyShip.Definition
            : null;

        ApplyDefinition(definition);
    }

    public void ApplyDefinition(EnemyShipDefinition definition)
    {
        if (definition == null)
        {
            return;
        }

        displayName = definition.DisplayName;
        maxHull = definition.MaxHull;
        capabilities = definition.CreateCombatCapabilities();
        fireWeight = definition.FireWeight;
        defendWeight = definition.DefendWeight;
        evadeWeight = definition.EvadeWeight;
        rewardValue = definition.RewardValue;
        isInitialised = false;
        InitialiseIfNeeded();
    }

    private void OnEnable()
    {
        InitialiseIfNeeded();
    }

    private void OnValidate()
    {
        maxHull = Mathf.Max(1.0f, maxHull);
        rewardValue = Mathf.Max(0, rewardValue);
        fireWeight = Mathf.Max(0.0f, fireWeight);
        defendWeight = Mathf.Max(0.0f, defendWeight);
        evadeWeight = Mathf.Max(0.0f, evadeWeight);
        capabilities ??= new CombatCapabilities();
        capabilities.ClampValues();
    }

    public float ApplyHullDamage(float amount)
    {
        InitialiseIfNeeded();

        if (amount <= 0.0f || IsDestroyed)
        {
            return 0.0f;
        }

        float oldHull = currentHull;
        currentHull = Mathf.Clamp(currentHull - amount, 0.0f, maxHull);
        float applied = oldHull - currentHull;

        if (applied > 0.0f)
        {
            HullChanged?.Invoke(currentHull, maxHull);
        }

        return applied;
    }

    public float ApplyShieldDamage(float amount)
    {
        InitialiseIfNeeded();

        if (amount <= 0.0f || currentShields <= 0.0f)
        {
            return 0.0f;
        }

        float oldShields = currentShields;
        currentShields = Mathf.Max(0.0f, currentShields - amount);
        return oldShields - currentShields;
    }

    public float RechargeShields(float multiplier = 1.0f)
    {
        InitialiseIfNeeded();

        float amount = capabilities.ShieldRecharge *
                       Mathf.Max(0.0f, multiplier);
        float oldShields = currentShields;
        currentShields = Mathf.Clamp(
            currentShields + amount,
            0.0f,
            capabilities.MaxShields
        );
        return currentShields - oldShields;
    }

    public void RestoreShieldsToFull()
    {
        InitialiseIfNeeded();
        currentShields = capabilities.MaxShields;
    }

    public CombatAction ChooseHiddenAction(float roll01)
    {
        float fire = Mathf.Max(0.0f, fireWeight);
        float defend = Mathf.Max(0.0f, defendWeight);
        float evade = Mathf.Max(0.0f, evadeWeight);
        float total = fire + defend + evade;

        if (total <= 0.0f)
        {
            return CombatAction.Fire;
        }

        float roll = Mathf.Clamp01(roll01) * total;

        if (roll < fire)
        {
            return CombatAction.Fire;
        }

        return roll < fire + defend
            ? CombatAction.Defend
            : CombatAction.Evade;
    }

    private void InitialiseIfNeeded()
    {
        if (isInitialised)
        {
            return;
        }

        maxHull = Mathf.Max(1.0f, maxHull);
        capabilities ??= new CombatCapabilities();
        capabilities.ClampValues();
        currentHull = maxHull;
        currentShields = capabilities.MaxShields;
        isInitialised = true;
    }
}
