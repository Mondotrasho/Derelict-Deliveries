/// <summary>
/// How well the player currently knows a planet - separate from Discovered/
/// RememberLocation on PlanetVisibilityState, which continue to drive
/// FogOfWar lock behaviour exactly as before.
///
/// Unknown
///     No known location. No label.
///
/// Detected
///     Location known - a label can show something. Exact identity is
///     not known, so the label shows a garbled or generic name.
///
/// Identified
///     Real name known. Full metadata available.
///
/// This is a one-way ratchet: see PlanetVisibilityState.RaiseKnowledgeState.
/// Once identified, a planet is never silently forgotten back to Detected
/// or Unknown just because the player looked away.
/// </summary>
public enum PlanetKnowledgeState
{
    Unknown,
    Detected,
    Identified
}