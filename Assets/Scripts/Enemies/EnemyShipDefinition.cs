using UnityEngine;

/// <summary>
/// Reusable authored identity, overworld behaviour and combat capabilities for
/// an enemy type. Runtime position, hull and shields remain on scene instances.
/// </summary>
[CreateAssetMenu(
    fileName = "EnemyShipDefinition",
    menuName = "Derelict Deliveries/Enemies/Ship Definition")]
public sealed class EnemyShipDefinition : ScriptableObject
{
    [Header("Identity and Presentation")]
    [SerializeField] private string displayName = "Pursuit Ship";
    [SerializeField] private Sprite sprite;

    [Tooltip("Size of the ship on the map (1 = as the prefab). E.g. 1.5 for a big monster.")]
    [Min(0.05f)]
    [SerializeField] private float mapScale = 1f;

    [Tooltip("Exists only for a fight: not drawn on the map, and removed when its fight ends however it ends (fled included). E.g. the life capsule.")]
    [SerializeField] private bool combatOnly = false;

    [Tooltip("Turn the map sprite to face where it is heading. Off = it always keeps Fixed Map Rotation (e.g. the eldritch monster).")]
    [SerializeField] private bool rotateToHeading = true;

    [Tooltip("Map sprite rotation in degrees when Rotate To Heading is off.")]
    [SerializeField] private float fixedMapRotation = 0f;

    [Tooltip("Ship sprite in the combat screen. Empty = the map Sprite above.")]
    [SerializeField] private Sprite combatSprite;

    [Tooltip("Shield drawn over the ship in the combat screen. Empty = keep the one placed in the combat view.")]
    [SerializeField] private Sprite combatShieldSprite;

    [Tooltip("Where this enemy sits in the combat view, in pixels from the EnemyShip's placed position (e.g. a monster coming out of a corner).")]
    [SerializeField] private Vector2 combatOffset = Vector2.zero;

    [Tooltip("Size of this enemy in the combat view (1 = as placed).")]
    [Min(0.05f)]
    [SerializeField] private float combatScale = 1f;
    [SerializeField] private FogOfWar.VisibilityTier fogRevealTier =
        FogOfWar.VisibilityTier.Partial;

    [Header("Overworld Movement")]
    [Min(0)]
    [SerializeField] private int movementBudget = 4;
    [Min(0.01f)]
    [SerializeField] private float movementSpeed = 4.0f;

    [Header("Combat")]
    [Min(1.0f)]
    [SerializeField] private float maxHull = 60.0f;
    [SerializeField] private CombatCapabilities capabilities =
        new CombatCapabilities();

    [Header("Hidden Action Weights")]
    [Min(0.0f)]
    [SerializeField] private float fireWeight = 60.0f;
    [Min(0.0f)]
    [SerializeField] private float defendWeight = 25.0f;
    [Min(0.0f)]
    [SerializeField] private float evadeWeight = 15.0f;

    [Header("Reward Hook")]
    [Min(0)]
    [SerializeField] private int rewardValue = 10;

    public string DisplayName => string.IsNullOrWhiteSpace(displayName)
        ? name
        : displayName.Trim();
    public Sprite Sprite => sprite;
    public float MapScale => mapScale;
    public bool CombatOnly => combatOnly;
    public bool RotateToHeading => rotateToHeading;
    public float FixedMapRotation => fixedMapRotation;
    public Sprite CombatSprite => combatSprite;
    public Sprite CombatShieldSprite => combatShieldSprite;
    public Vector2 CombatOffset => combatOffset;
    public float CombatScale => combatScale;
    public FogOfWar.VisibilityTier FogRevealTier => fogRevealTier;
    public int MovementBudget => movementBudget;
    public float MovementSpeed => movementSpeed;
    public float MaxHull => maxHull;
    public float FireWeight => fireWeight;
    public float DefendWeight => defendWeight;
    public float EvadeWeight => evadeWeight;
    public int RewardValue => rewardValue;

    public CombatCapabilities CreateCombatCapabilities()
    {
        capabilities ??= new CombatCapabilities();

        return new CombatCapabilities(
            capabilities.WeaponDamage,
            capabilities.WeaponAccuracy,
            capabilities.Armour,
            capabilities.MaxShields,
            capabilities.ShieldRecharge,
            capabilities.Maneuverability
        );
    }

    private void OnValidate()
    {
        movementBudget = Mathf.Max(0, movementBudget);
        movementSpeed = Mathf.Max(0.01f, movementSpeed);
        maxHull = Mathf.Max(1.0f, maxHull);
        fireWeight = Mathf.Max(0.0f, fireWeight);
        defendWeight = Mathf.Max(0.0f, defendWeight);
        evadeWeight = Mathf.Max(0.0f, evadeWeight);
        rewardValue = Mathf.Max(0, rewardValue);
        capabilities ??= new CombatCapabilities();
        capabilities.ClampValues();
    }
}
