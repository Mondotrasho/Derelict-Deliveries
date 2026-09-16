using UnityEngine;

/// <summary>
/// Optional extension point for features that add extra cost to player movement.
///
/// A feature owns its own implementation and registers it with MovementAllowance.
/// MovementAllowance remains responsible for the final player movement price.
///
/// Examples include asteroid fields, clouds, temporary event hazards or ship
/// conditions. Implementations should return only an additional non-negative
/// surcharge. Base orthogonal/diagonal cost is already supplied by
/// MovementAllowance.
///
/// This interface changes cost only. It does not make a cell impassable; hard
/// walkability remains a GridMap/GridPathfinder responsibility.
/// </summary>
public interface IMovementCostModifier
{
    int GetAdditionalMovementCost(
        Vector3Int fromCell,
        Vector3Int toCell);
}
