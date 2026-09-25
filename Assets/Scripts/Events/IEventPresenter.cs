using System;
using System.Collections;

/// <summary>
/// Shows one triggered event and reports what happened. Swapping the debug
/// window for the real event UI is replacing the component that implements this.
/// </summary>
public interface IEventPresenter
{
    /// <summary>Yield until the player is done, then call done exactly once.</summary>
    IEnumerator Present(EventSite site, EventContext context, Action<EventOutcome> done);

    /// <summary>Close immediately without resolving (e.g. combat started). Present must then finish.</summary>
    void Cancel();
}


public struct EventOutcome
{
    /// <summary>Apply the definition's resolve writes and remove the site.</summary>
    public bool resolved;

    /// <summary>Cancel the rest of the journey instead of resuming the paused route.</summary>
    public bool stopJourney;

    public static EventOutcome LeaveForLater => new EventOutcome { resolved = false, stopJourney = false };
}
