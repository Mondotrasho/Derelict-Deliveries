using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Prevents more than one route-input script from driving the same
/// RoutePlanner at once.
///
/// GridPlayerInput and RouteInputController both react to the same mouse
/// input and both call into RoutePlanner - if both were left enabled on
/// the same RoutePlanner they would fight over its state every held-mouse
/// frame. Each input script claims ownership of its RoutePlanner in
/// OnEnable and releases it in OnDisable. Whichever one enables second
/// finds the claim already taken, logs why, and disables itself instead
/// of running.
///
/// Deliberately keyed by RoutePlanner rather than by GameObject, so this
/// still works correctly even if the two input scripts ever end up on
/// different objects pointing at the same RoutePlanner.
/// </summary>
public static class RouteInputOwnership
{
    private static readonly Dictionary<RoutePlanner, Behaviour> owners =
        new Dictionary<RoutePlanner, Behaviour>();


    /// <summary>
    /// Attempts to claim exclusive input ownership of routePlanner for
    /// claimant.
    ///
    /// Returns true (and sets currentOwner to claimant) if nothing else
    /// currently holds the claim, if claimant already holds it, or if
    /// routePlanner itself is unassigned - there is nothing to protect
    /// against a conflict over, and the calling script's own existing
    /// "no RoutePlanner assigned" warning (raised when it actually tries
    /// to use it) is the more useful message in that case.
    /// Returns false (and sets currentOwner to whoever does hold it) if
    /// a different script has already claimed this RoutePlanner - the
    /// caller should not proceed to enable itself in that case.
    /// </summary>
    public static bool TryClaim(
        RoutePlanner routePlanner,
        Behaviour claimant,
        out Behaviour currentOwner)
    {
        if (claimant == null)
        {
            currentOwner = null;
            return false;
        }

        if (routePlanner == null)
        {
            currentOwner = claimant;
            return true;
        }

        if (owners.TryGetValue(routePlanner, out Behaviour existingOwner) &&
            existingOwner != null &&
            existingOwner != claimant)
        {
            currentOwner = existingOwner;
            return false;
        }

        owners[routePlanner] = claimant;
        currentOwner = claimant;
        return true;
    }


    /// <summary>
    /// Releases claimant's ownership of routePlanner, if it currently
    /// holds it. Safe to call even if claimant never successfully
    /// claimed it (e.g. it backed off in OnEnable) - OnDisable can always
    /// call this unconditionally.
    /// </summary>
    public static void Release(
        RoutePlanner routePlanner,
        Behaviour claimant)
    {
        if (routePlanner == null || claimant == null)
        {
            return;
        }

        if (owners.TryGetValue(routePlanner, out Behaviour existingOwner) &&
            existingOwner == claimant)
        {
            owners.Remove(routePlanner);
        }
    }
}
