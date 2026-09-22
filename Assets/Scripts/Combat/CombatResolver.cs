using UnityEngine;

/// <summary>
/// Stateless combat mathematics. Random rolls are supplied by the encounter
/// controller so calculations can be inspected and tested independently.
/// </summary>
public static class CombatResolver
{
    public readonly struct DamageResult
    {
        public float ShieldDamage { get; }
        public float HullDamage { get; }
        public float DefendReduction { get; }
        public float ArmourReduction { get; }

        public DamageResult(
            float shieldDamage,
            float hullDamage,
            float defendReduction,
            float armourReduction)
        {
            ShieldDamage = shieldDamage;
            HullDamage = hullDamage;
            DefendReduction = defendReduction;
            ArmourReduction = armourReduction;
        }
    }

    public static float CalculateContestChance(
        float actingValue,
        float opposingValue,
        float minimumChance,
        float maximumChance)
    {
        actingValue = Mathf.Max(0.0f, actingValue);
        opposingValue = Mathf.Max(0.0f, opposingValue);

        float total = actingValue + opposingValue;
        float chance = total > 0.0f ? actingValue / total : 0.5f;

        return Mathf.Clamp(
            chance,
            Mathf.Clamp01(minimumChance),
            Mathf.Clamp01(maximumChance)
        );
    }

    public static float CalculateDefendReduction(
        float armour,
        float baseReduction,
        float armourScale,
        float maximumArmourBonus,
        float maximumTotalReduction)
    {
        armour = Mathf.Max(0.0f, armour);
        armourScale = Mathf.Max(0.0001f, armourScale);

        float armourRatio = armour / (armour + armourScale);
        float reduction =
            Mathf.Max(0.0f, baseReduction) +
            armourRatio * Mathf.Max(0.0f, maximumArmourBonus);

        return Mathf.Clamp(
            reduction,
            0.0f,
            Mathf.Clamp(maximumTotalReduction, 0.0f, 0.95f)
        );
    }

    public static float CalculateArmourReduction(
        float armour,
        float armourScale)
    {
        armour = Mathf.Max(0.0f, armour);
        armourScale = Mathf.Max(0.0001f, armourScale);
        return armour / (armour + armourScale);
    }

    public static DamageResult ResolveDamage(
        float rawDamage,
        float currentShields,
        float armour,
        bool defending,
        float baseDefendReduction,
        float defendArmourScale,
        float maximumDefendArmourBonus,
        float maximumDefendReduction,
        float passiveArmourScale)
    {
        rawDamage = Mathf.Max(0.0f, rawDamage);
        currentShields = Mathf.Max(0.0f, currentShields);

        float defendReduction = defending
            ? CalculateDefendReduction(
                armour,
                baseDefendReduction,
                defendArmourScale,
                maximumDefendArmourBonus,
                maximumDefendReduction)
            : 0.0f;

        float damageAfterDefend = rawDamage * (1.0f - defendReduction);
        float shieldDamage = Mathf.Min(currentShields, damageAfterDefend);
        float overflow = Mathf.Max(0.0f, damageAfterDefend - shieldDamage);
        float armourReduction = CalculateArmourReduction(
            armour,
            passiveArmourScale
        );
        float hullDamage = overflow * (1.0f - armourReduction);

        return new DamageResult(
            shieldDamage,
            hullDamage,
            defendReduction,
            armourReduction
        );
    }
}
