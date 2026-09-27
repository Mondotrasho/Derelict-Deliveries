using System;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Shows the list of things to do at a Point of Interest and reports the pick
/// (or null for Leave). Replace the debug picker with real UI by implementing this.
/// </summary>
public interface IPoiPicker
{
    IEnumerator Pick(Planet planet, IReadOnlyList<PoiChoice> choices, Action<PoiChoice> picked);
    void Cancel();
}
