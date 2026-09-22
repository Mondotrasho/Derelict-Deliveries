using System;
using UnityEngine;

/// <summary>
/// Authored combat statistics for one ship. Runtime hull and shield values
/// live elsewhere so upgrades can modify these capabilities independently.
/// </summary>
[Serializable]
public sealed class CombatCapabilities
{
    [Min(0.0f)]
    [SerializeField]
    private float weaponDamage = 20.0f;

    [Min(0.0f)]
    [SerializeField]
    private float weaponAccuracy = 50.0f;

    [Min(0.0f)]
    [SerializeField]
    private float armour = 20.0f;

    [Min(0.0f)]
    [SerializeField]
    private float maxShields = 40.0f;

    [Min(0.0f)]
    [SerializeField]
    private float shieldRecharge = 10.0f;

    [Min(0.0f)]
    [SerializeField]
    private float maneuverability = 50.0f;

    public float WeaponDamage => weaponDamage;
    public float WeaponAccuracy => weaponAccuracy;
    public float Armour => armour;
    public float MaxShields => maxShields;
    public float ShieldRecharge => shieldRecharge;
    public float Maneuverability => maneuverability;

    public CombatCapabilities()
    {
    }

    public CombatCapabilities(
        float weaponDamage,
        float weaponAccuracy,
        float armour,
        float maxShields,
        float shieldRecharge,
        float maneuverability)
    {
        this.weaponDamage = weaponDamage;
        this.weaponAccuracy = weaponAccuracy;
        this.armour = armour;
        this.maxShields = maxShields;
        this.shieldRecharge = shieldRecharge;
        this.maneuverability = maneuverability;
        ClampValues();
    }

    public void ClampValues()
    {
        weaponDamage = Mathf.Max(0.0f, weaponDamage);
        weaponAccuracy = Mathf.Max(0.0f, weaponAccuracy);
        armour = Mathf.Max(0.0f, armour);
        maxShields = Mathf.Max(0.0f, maxShields);
        shieldRecharge = Mathf.Max(0.0f, shieldRecharge);
        maneuverability = Mathf.Max(0.0f, maneuverability);
    }
}
