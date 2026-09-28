using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Who sits where on the ship. 12 slots matching ShipUI.psd:
///   0 helm (wheel)   1 gunnery (left pod)   2 navigation (right pod)   3 engineering (wrench)
///   4-11 passenger berths (2 x 4 grid, left-to-right, top-to-bottom)
///
/// Rules:
///   - A newly recruited officer takes the first free berth; if all berths are
///     full, a random free station. Officers aboard at the start take stations.
///   - Passengers (the cult group, while Player flag cult:passengers is on) fill
///     free berths and leave when the flag goes off.
///   - Officers may be moved to ANY free slot, passengers only to free berths.
/// Purely presentational for now: stations give no bonuses. Not saved.
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipCrewManifest : MonoBehaviour
{
    public const int StationCount = 4;
    public const int BerthCount = 8;
    public const int SlotCount = StationCount + BerthCount;

    public static bool IsStation(int slot) => slot >= 0 && slot < StationCount;
    public static bool IsBerth(int slot) => slot >= StationCount && slot < SlotCount;

    public sealed class Occupant
    {
        public OfficerDefinition officer;   // null for passengers
        public int passengerIndex = -1;     // 0 = the group's leader
        public bool IsOfficer => officer != null;
    }

    [Header("References (found automatically if empty)")]
    [SerializeField] private OfficerRoster roster;
    [SerializeField] private PlayerShipState player;

    [Header("Cult passengers")]
    [Tooltip("Player flag that means the cult group is aboard.")]
    [SerializeField] private string passengerFlag = "cult:passengers";
    [Tooltip("How many come aboard: the cultist plus six robed figures.")]
    [Range(1, BerthCount)] [SerializeField] private int passengerCount = 7;
    [SerializeField] private string leaderName = "The Cultist";
    [SerializeField] private Sprite leaderPortrait;
    [SerializeField] private string followerName = "Robed figure";
    [Tooltip("Followers cycle through these.")]
    [SerializeField] private List<Sprite> followerPortraits = new List<Sprite>();
    [SerializeField] private string passengerRole = "Passenger";

    [Tooltip("Optional: once this Player flag is on, every passenger shows the Corrupted Portrait instead. Empty = off.")]
    [SerializeField] private string corruptedFlag = "";
    [SerializeField] private Sprite corruptedPortrait;

    public event Action Changed;

    private readonly Occupant[] slots = new Occupant[SlotCount];
    private bool firstSync = true;
    private bool passengersAboard;

    public Occupant Get(int slot) => slot >= 0 && slot < SlotCount ? slots[slot] : null;


    private void Awake()
    {
        if (player == null) player = GetComponent<PlayerShipState>();
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (roster == null) roster = GetComponent<OfficerRoster>();
        if (roster == null) roster = FindFirstObjectByType<OfficerRoster>();
    }


    private void OnEnable()
    {
        if (roster != null) roster.RosterChanged += SyncOfficers;
    }


    private void OnDisable()
    {
        if (roster != null) roster.RosterChanged -= SyncOfficers;
    }


    private void Update()
    {
        // No change event exists for flags, so check (cheap) each frame. Also
        // catches officers aboard from the start, whatever the Start order.
        if (firstSync) SyncOfficers();

        bool aboard = player != null && player.EventState != null &&
                      !string.IsNullOrEmpty(passengerFlag) && player.EventState.GetFlag(passengerFlag);
        if (aboard != passengersAboard)
        {
            passengersAboard = aboard;
            if (aboard) BoardPassengers(); else RemovePassengers();
        }
    }


    // ---------------------------------------------------------------- officers
    private void SyncOfficers()
    {
        IReadOnlyList<OfficerDefinition> list = roster != null ? roster.Aboard : null;
        bool changed = false;

        // officers who left
        for (int i = 0; i < SlotCount; i++)
        {
            if (slots[i] != null && slots[i].IsOfficer && (list == null || !Contains(list, slots[i].officer)))
            {
                slots[i] = null;
                changed = true;
            }
        }

        // officers who arrived
        if (list != null)
        {
            foreach (OfficerDefinition o in list)
            {
                if (o == null || IndexOf(o) >= 0) continue;
                int slot = firstSync ? FirstFree(true, false) : FirstFree(false, true);
                if (slot < 0) slot = firstSync ? FirstFree(false, true) : RandomFreeStation();
                if (slot < 0) continue;   // ship completely full
                slots[slot] = new Occupant { officer = o };
                changed = true;
            }
        }

        firstSync = false;
        if (changed) Changed?.Invoke();
    }


    private static bool Contains(IReadOnlyList<OfficerDefinition> list, OfficerDefinition o)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == o) return true;
        return false;
    }


    private int IndexOf(OfficerDefinition o)
    {
        for (int i = 0; i < SlotCount; i++) if (slots[i] != null && slots[i].officer == o) return i;
        return -1;
    }


    private int FirstFree(bool stations, bool berths)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (slots[i] != null) continue;
            if ((stations && IsStation(i)) || (berths && IsBerth(i))) return i;
        }
        return -1;
    }


    private int RandomFreeStation()
    {
        var free = new List<int>();
        for (int i = 0; i < StationCount; i++) if (slots[i] == null) free.Add(i);
        return free.Count > 0 ? free[UnityEngine.Random.Range(0, free.Count)] : -1;
    }


    // -------------------------------------------------------------- passengers
    private void BoardPassengers()
    {
        for (int n = 0; n < passengerCount; n++)
        {
            int slot = FirstFree(false, true);
            if (slot < 0)
            {
                Debug.LogWarning($"ShipCrewManifest: no free berth for passenger {n + 1}/{passengerCount}.", this);
                break;
            }
            slots[slot] = new Occupant { passengerIndex = n };
        }
        Changed?.Invoke();
    }


    private void RemovePassengers()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (slots[i] != null && !slots[i].IsOfficer) slots[i] = null;
        }
        Changed?.Invoke();
    }


    // ------------------------------------------------------------- moving people
    /// <summary>Officers may go to any empty slot, passengers only to empty berths.</summary>
    public bool CanMove(int from, int to)
    {
        Occupant o = Get(from);
        if (o == null || from == to || to < 0 || to >= SlotCount || slots[to] != null) return false;
        return o.IsOfficer || IsBerth(to);
    }


    public bool Move(int from, int to)
    {
        if (!CanMove(from, to)) return false;
        slots[to] = slots[from];
        slots[from] = null;
        Changed?.Invoke();
        return true;
    }


    // ------------------------------------------------------------ presentation
    public Sprite PortraitOf(Occupant o)
    {
        if (o == null) return null;
        if (o.IsOfficer) return o.officer.ManifestPortrait;
        if (corruptedPortrait != null && !string.IsNullOrEmpty(corruptedFlag) &&
            player != null && player.EventState != null && player.EventState.GetFlag(corruptedFlag))
            return corruptedPortrait;
        if (o.passengerIndex == 0 && leaderPortrait != null) return leaderPortrait;
        if (followerPortraits.Count == 0) return leaderPortrait;
        Sprite s = followerPortraits[(Mathf.Max(0, o.passengerIndex - 1)) % followerPortraits.Count];
        return s != null ? s : leaderPortrait;
    }


    public string NameOf(Occupant o)
    {
        if (o == null) return "";
        if (o.IsOfficer) return o.officer.DisplayName;
        return o.passengerIndex == 0 ? leaderName : followerName;
    }


    public string RoleOf(Occupant o)
    {
        if (o == null) return "";
        return o.IsOfficer ? o.officer.Role : passengerRole;
    }
}
