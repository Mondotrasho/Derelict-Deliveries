using System.Collections.Generic;

/// <summary>
/// Fuel pickups near asteroid fields. Same placement, "?" marker, identify
/// reveal, sprite look (per definition: size, tint, spin) and fog lock as
/// derelicts - but the sites are Pickup category, so flying over one collects
/// it instantly with no window (EventTriggerHandler) and the ship keeps going.
///
/// Add it to the Player next to the other sources and to EventDirector's
/// Sources list, with a table of pickup definitions tagged "pickup".
/// </summary>
public class FuelPickupSource : DerelictEventSource
{
    public override EventCategory Category => EventCategory.Pickup;


    private void Reset()
    {
        sourceId = "pickup";
        rollChance = 0.06f;
        rerollCooldownTurns = 6;
        maxNewSitesPerScan = 1;
        eventTags = new List<string> { "pickup" };
    }
}
