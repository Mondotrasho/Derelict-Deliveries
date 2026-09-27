using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A named officer: who they are and the bonuses they bring. Officers are not
/// the crew count (crew is a resource on ShipResources); they are people.
/// Bonuses are plain stat names ("engineering", "scanning", "piloting"...) that
/// event choices can require or use to improve their odds.
/// </summary>
[CreateAssetMenu(menuName = "Derelict Deliveries/Crew/Officer", fileName = "officer.new")]
public sealed class OfficerDefinition : ScriptableObject
{
    [Serializable]
    public struct Bonus
    {
        [Tooltip("Stat name, lower case, e.g. engineering, scanning, piloting.")]
        public string stat;
        public int amount;
    }

    [SerializeField] private string id = "officer.new";
    [SerializeField] private string displayName = "New Officer";
    [Tooltip("Shown after the name, e.g. Engineer.")]
    [SerializeField] private string role = "Officer";
    [TextArea(2, 4)] [SerializeField] private string description = "";
    [Tooltip("For the ship UI later. Optional.")]
    [SerializeField] private Sprite portrait;
    [SerializeField] private List<Bonus> bonuses = new List<Bonus>();

    public string Id => id;
    public string DisplayName => displayName;
    public string Role => role;
    public string Description => description;
    public Sprite Portrait => portrait;
    public IReadOnlyList<Bonus> Bonuses => bonuses;


    public int GetBonus(string stat)
    {
        if (string.IsNullOrWhiteSpace(stat)) return 0;
        int total = 0;
        foreach (Bonus b in bonuses)
        {
            if (string.Equals(b.stat?.Trim(), stat.Trim(), StringComparison.OrdinalIgnoreCase)) total += b.amount;
        }
        return total;
    }
}
