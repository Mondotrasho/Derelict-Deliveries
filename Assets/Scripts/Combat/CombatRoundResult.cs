/// <summary>Read-only report of one fully resolved combat round.</summary>
public sealed class CombatRoundResult
{
    public CombatAction PlayerAction { get; }
    public CombatAction EnemyAction { get; }
    public bool PlayerActedFirst { get; internal set; }

    public bool PlayerAttackAttempted { get; internal set; }
    public bool PlayerAttackHit { get; internal set; }
    public float PlayerHitChance { get; internal set; }
    public float PlayerHitRoll { get; internal set; }

    public bool EnemyAttackAttempted { get; internal set; }
    public bool EnemyAttackHit { get; internal set; }
    public float EnemyHitChance { get; internal set; }
    public float EnemyHitRoll { get; internal set; }

    public float DamageToPlayerShields { get; internal set; }
    public float DamageToPlayerHull { get; internal set; }
    public float DamageToEnemyShields { get; internal set; }
    public float DamageToEnemyHull { get; internal set; }
    public float PlayerShieldRecharge { get; internal set; }
    public float EnemyShieldRecharge { get; internal set; }

    public bool EscapeAttempted { get; internal set; }
    public bool EscapeSucceeded { get; internal set; }
    public float EscapeChance { get; internal set; }
    public float EscapeRoll { get; internal set; }

    public string Summary { get; internal set; }

    public CombatRoundResult(
        CombatAction playerAction,
        CombatAction enemyAction)
    {
        PlayerAction = playerAction;
        EnemyAction = enemyAction;
        Summary = string.Empty;
    }
}
