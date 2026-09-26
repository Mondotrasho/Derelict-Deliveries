using System;
using UnityEngine;

/// <summary>
/// Reusable ship resource state.
///
/// This component owns values only. It does not decide when fuel is spent,
/// when hull damage happens, how crew are recruited, or how any of those
/// values are displayed. Those systems call the controlled mutation methods
/// here and UI reads/subscribes to the public state.
///
/// That keeps TurnManager, combat code, event code and UI from each storing
/// their own copy of the same ship values.
/// </summary>
[DisallowMultipleComponent]
public class ShipResources : MonoBehaviour
{
    [Header("Hull")]

    [Min(0.0f)]
    [SerializeField]
    private float maxHullIntegrity = 100.0f;

    [Min(0.0f)]
    [SerializeField]
    private float hullIntegrity = 100.0f;


    [Header("Fuel")]

    [Min(0.0f)]
    [SerializeField]
    private float maxFuel = 100.0f;

    [Min(0.0f)]
    [SerializeField]
    private float fuel = 100.0f;


    [Header("Shields")]

    [Tooltip("Kept in sync with the player's combat Max Shields by CombatEncounterController.")]
    [Min(0.0f)]
    [SerializeField]
    private float maxShields = 40.0f;

    [Min(0.0f)]
    [SerializeField]
    private float shields = 40.0f;


    [Header("Crew")]

    [Min(0)]
    [SerializeField]
    private int maxCrew = 10;

    [Min(0)]
    [SerializeField]
    private int crew = 1;


    /// <summary>Raised whenever any resource value changes.</summary>
    public event Action ResourcesChanged;

    /// <summary>Raised after fuel changes. Arguments are current, maximum.</summary>
    public event Action<float, float> FuelChanged;

    /// <summary>Raised after hull changes. Arguments are current, maximum.</summary>
    public event Action<float, float> HullChanged;

    /// <summary>Raised after crew changes. Arguments are current, maximum.</summary>
    public event Action<int, int> CrewChanged;

    /// <summary>Raised after shields change. Arguments are current, maximum.</summary>
    public event Action<float, float> ShieldsChanged;


    public float HullIntegrity
    {
        get { return hullIntegrity; }
    }


    public float MaxHullIntegrity
    {
        get { return maxHullIntegrity; }
    }


    public float HullFraction
    {
        get
        {
            return maxHullIntegrity > 0.0f
                ? hullIntegrity / maxHullIntegrity
                : 0.0f;
        }
    }


    public float Fuel
    {
        get { return fuel; }
    }


    public float MaxFuel
    {
        get { return maxFuel; }
    }


    public float FuelFraction
    {
        get
        {
            return maxFuel > 0.0f
                ? fuel / maxFuel
                : 0.0f;
        }
    }


    public float Shields
    {
        get { return shields; }
    }


    public float MaxShields
    {
        get { return maxShields; }
    }


    public float ShieldFraction
    {
        get
        {
            return maxShields > 0.0f
                ? shields / maxShields
                : 0.0f;
        }
    }


    /// <summary>Adds shields up to MaxShields. Returns the amount actually added.</summary>
    public float AddShields(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = shields;
        shields = Mathf.Clamp(shields + amount, 0.0f, maxShields);
        if (!Mathf.Approximately(oldValue, shields))
        {
            RaiseShieldsChanged();
        }
        return shields - oldValue;
    }


    /// <summary>Removes shields down to zero. Returns the amount actually removed.</summary>
    public float ApplyShieldDamage(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = shields;
        shields = Mathf.Max(0.0f, shields - amount);
        if (!Mathf.Approximately(oldValue, shields))
        {
            RaiseShieldsChanged();
        }
        return oldValue - shields;
    }


    public void SetShields(float value)
    {
        float clamped = Mathf.Clamp(value, 0.0f, maxShields);
        if (Mathf.Approximately(clamped, shields))
        {
            return;
        }

        shields = clamped;
        RaiseShieldsChanged();
    }


    /// <summary>Changes the maximum (e.g. an upgrade). Current shields are clamped, not refilled.</summary>
    public void SetMaxShields(float value)
    {
        float newMax = Mathf.Max(0.0f, value);
        if (Mathf.Approximately(newMax, maxShields))
        {
            return;
        }

        maxShields = newMax;
        shields = Mathf.Clamp(shields, 0.0f, maxShields);
        RaiseShieldsChanged();
    }


    public int Crew
    {
        get { return crew; }
    }


    public int MaxCrew
    {
        get { return maxCrew; }
    }


    public float CrewFraction
    {
        get
        {
            return maxCrew > 0
                ? (float)crew / maxCrew
                : 0.0f;
        }
    }


    private void Awake()
    {
        ClampValues();
    }


    private void OnValidate()
    {
        ClampValues();
    }


    /// <summary>
    /// Adds fuel up to MaxFuel.
    /// Returns the amount actually added.
    /// </summary>
    public float AddFuel(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = fuel;
        fuel = Mathf.Clamp(fuel + amount, 0.0f, maxFuel);

        if (Mathf.Approximately(oldValue, fuel))
        {
            return 0.0f;
        }

        RaiseFuelChanged();
        return fuel - oldValue;
    }


    /// <summary>
    /// Consumes as much fuel as is available, up to amount.
    /// Returns the amount actually consumed.
    /// </summary>
    public float ConsumeFuel(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = fuel;
        fuel = Mathf.Clamp(fuel - amount, 0.0f, maxFuel);

        if (Mathf.Approximately(oldValue, fuel))
        {
            return 0.0f;
        }

        RaiseFuelChanged();
        return oldValue - fuel;
    }


    /// <summary>
    /// All-or-nothing fuel spend. Nothing changes if there is not enough.
    /// Useful for actions that must be rejected rather than partially paid.
    /// </summary>
    public bool TryConsumeFuel(float amount)
    {
        if (amount < 0.0f)
        {
            return false;
        }

        if (amount == 0.0f)
        {
            return true;
        }

        if (fuel < amount)
        {
            return false;
        }

        ConsumeFuel(amount);
        return true;
    }


    /// <summary>
    /// Controlled absolute setter, useful for save/load and debug tools.
    /// Normal gameplay can normally prefer AddFuel/ConsumeFuel.
    /// </summary>
    public void SetFuel(float value)
    {
        float clamped = Mathf.Clamp(value, 0.0f, maxFuel);

        if (Mathf.Approximately(clamped, fuel))
        {
            return;
        }

        fuel = clamped;
        RaiseFuelChanged();
    }


    /// <summary>
    /// Applies hull damage, clamped at zero.
    /// Returns the amount of integrity actually removed.
    /// </summary>
    public float ApplyHullDamage(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = hullIntegrity;
        hullIntegrity = Mathf.Clamp(hullIntegrity - amount, 0.0f, maxHullIntegrity);

        if (Mathf.Approximately(oldValue, hullIntegrity))
        {
            return 0.0f;
        }

        RaiseHullChanged();
        return oldValue - hullIntegrity;
    }


    /// <summary>
    /// Repairs hull integrity up to MaxHullIntegrity.
    /// Returns the amount actually repaired.
    /// </summary>
    public float RepairHull(float amount)
    {
        if (amount <= 0.0f)
        {
            return 0.0f;
        }

        float oldValue = hullIntegrity;
        hullIntegrity = Mathf.Clamp(hullIntegrity + amount, 0.0f, maxHullIntegrity);

        if (Mathf.Approximately(oldValue, hullIntegrity))
        {
            return 0.0f;
        }

        RaiseHullChanged();
        return hullIntegrity - oldValue;
    }


    public void SetHullIntegrity(float value)
    {
        float clamped = Mathf.Clamp(value, 0.0f, maxHullIntegrity);

        if (Mathf.Approximately(clamped, hullIntegrity))
        {
            return;
        }

        hullIntegrity = clamped;
        RaiseHullChanged();
    }


    /// <summary>Adds crew up to MaxCrew and returns the amount actually added.</summary>
    public int AddCrew(int amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        int oldValue = crew;
        crew = Mathf.Clamp(crew + amount, 0, maxCrew);

        if (oldValue == crew)
        {
            return 0;
        }

        RaiseCrewChanged();
        return crew - oldValue;
    }


    /// <summary>Removes crew down to zero and returns the amount actually removed.</summary>
    public int RemoveCrew(int amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        int oldValue = crew;
        crew = Mathf.Clamp(crew - amount, 0, maxCrew);

        if (oldValue == crew)
        {
            return 0;
        }

        RaiseCrewChanged();
        return oldValue - crew;
    }


    public void SetCrew(int value)
    {
        int clamped = Mathf.Clamp(value, 0, maxCrew);

        if (clamped == crew)
        {
            return;
        }

        crew = clamped;
        RaiseCrewChanged();
    }


    private void ClampValues()
    {
        maxHullIntegrity = Mathf.Max(0.0f, maxHullIntegrity);
        maxFuel = Mathf.Max(0.0f, maxFuel);
        maxCrew = Mathf.Max(0, maxCrew);
        maxShields = Mathf.Max(0.0f, maxShields);

        hullIntegrity = Mathf.Clamp(hullIntegrity, 0.0f, maxHullIntegrity);
        fuel = Mathf.Clamp(fuel, 0.0f, maxFuel);
        crew = Mathf.Clamp(crew, 0, maxCrew);
        shields = Mathf.Clamp(shields, 0.0f, maxShields);
    }


    private void RaiseFuelChanged()
    {
        FuelChanged?.Invoke(fuel, maxFuel);
        ResourcesChanged?.Invoke();
    }


    private void RaiseHullChanged()
    {
        HullChanged?.Invoke(hullIntegrity, maxHullIntegrity);
        ResourcesChanged?.Invoke();
    }


    private void RaiseShieldsChanged()
    {
        ShieldsChanged?.Invoke(shields, maxShields);
        ResourcesChanged?.Invoke();
    }


    private void RaiseCrewChanged()
    {
        CrewChanged?.Invoke(crew, maxCrew);
        ResourcesChanged?.Invoke();
    }
}
