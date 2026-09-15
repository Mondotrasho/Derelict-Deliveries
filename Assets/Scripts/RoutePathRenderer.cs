using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws the route currently owned by RoutePlanner as a pixelated dashed
/// line - a row of small square dots rather than a smooth LineRenderer -
/// split into the affordable prefix and any "over budget" tail according
/// to MovementAllowance.
///
/// This renderer only converts cells into world-space dot positions - it
/// does not decide affordability itself (MovementAllowance does), and it
/// does not move or commit anything.
///
/// Zero scene setup required: all dot GameObjects are created and pooled
/// automatically as children of this object. Dot size/spacing are given
/// in pixels rather than world units, assuming pixelsPerUnit matches how
/// the project's 8x8 tiles map to world units (1 world unit = 1 tile is
/// the common convention - adjust pixelsPerUnit if that's not the case).
/// </summary>
public class RoutePathRenderer : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private RoutePlanner routePlanner;

    [SerializeField]
    private PlayerGridController playerController;

    [SerializeField]
    private GridMap gridMap;

    [Tooltip("Optional. If assigned, the route is split into affordable/excess segments.")]
    [SerializeField]
    private MovementAllowance movementAllowance;

    [Tooltip("Moved to the final cell of the route while a route is planned. Can be left empty - no marker is auto-created.")]
    [SerializeField]
    private Transform destinationMarker;


    [Header("Pixel Grid")]

    [Tooltip("How many physical pixels make up one world unit. Defaults to 8, assuming 1 world unit = 1 tile = 8x8 pixels.")]
    [SerializeField]
    private int pixelsPerUnit = 8;

    [Tooltip("Size of each dash dot, in pixels.")]
    [SerializeField]
    private int dotSizePixels = 2;

    [Tooltip("Gap between dash dots, in pixels.")]
    [SerializeField]
    private int gapPixels = 2;


    [Header("Colour")]

    [SerializeField]
    private Color affordableColor = new Color(0.3f, 1.0f, 0.4f);

    [SerializeField]
    private Color excessColor = new Color(1.0f, 0.35f, 0.3f);

    [Tooltip("Only used for dots this component creates - if you retint via a shared material this can be ignored.")]
    [SortingLayerName]
    [SerializeField]
    private string sortingLayerName = "Default";

    [SerializeField]
    private int sortingOrder = 10;


    private Sprite dotSprite;

    private Transform affordableDotsContainer;
    private Transform excessDotsContainer;

    private readonly List<SpriteRenderer> affordablePool = new List<SpriteRenderer>();
    private readonly List<SpriteRenderer> excessPool = new List<SpriteRenderer>();


    private void Awake()
    {
        dotSprite = CreateDotSprite();

        affordableDotsContainer =
            CreateContainer("AffordableRouteDots");

        excessDotsContainer =
            CreateContainer("ExcessRouteDots");
    }


    private void OnEnable()
    {
        if (routePlanner != null)
        {
            routePlanner.RouteChanged += HandleRouteChanged;
        }

        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged += HandleAllowanceChanged;
        }
    }


    private void OnDisable()
    {
        if (routePlanner != null)
        {
            routePlanner.RouteChanged -= HandleRouteChanged;
        }

        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged -= HandleAllowanceChanged;
        }
    }


    /// <summary>
    /// A single white 1x1 pixel texture shared by every dot. Point-filtered
    /// so scaling it up into a dot never softens the edges.
    /// </summary>
    private Sprite CreateDotSprite()
    {
        Texture2D texture = new Texture2D(1, 1);

        texture.filterMode = FilterMode.Point;
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();

        // pixelsPerUnit: 1 here so the sprite is exactly 1 world unit wide
        // at scale 1 - actual on-screen size is then just localScale.
        return Sprite.Create(
            texture,
            new Rect(0, 0, 1, 1),
            new Vector2(0.5f, 0.5f),
            1.0f
        );
    }


    private Transform CreateContainer(string containerName)
    {
        GameObject container = new GameObject(containerName);

        container.transform.SetParent(
            transform,
            false
        );

        return container.transform;
    }


    /// <summary>
    /// Redraws for the latest planned path.
    /// </summary>
    private void HandleRouteChanged(List<Vector3Int> path)
    {
        DrawPath(path);
    }


    /// <summary>
    /// Remaining movement points can change (e.g. spent by a previous
    /// commit) without the planned route itself changing, so redraw using
    /// whatever RoutePlanner currently has planned.
    /// </summary>
    private void HandleAllowanceChanged()
    {
        if (routePlanner != null)
        {
            DrawPath(new List<Vector3Int>(routePlanner.PlannedPath));
        }
    }


    /// <summary>
    /// Rebuilds the dashed dots from the player's current position through
    /// every cell in the planned path - split at the point where the
    /// route stops being affordable.
    /// </summary>
    private void DrawPath(List<Vector3Int> path)
    {
        if (gridMap == null || playerController == null)
        {
            Debug.LogError(
                "RoutePathRenderer is missing a required reference."
            );

            return;
        }


        if (path == null || path.Count == 0)
        {
            DeactivateFrom(affordablePool, 0);
            DeactivateFrom(excessPool, 0);

            if (destinationMarker != null)
            {
                destinationMarker.gameObject.SetActive(false);
            }

            return;
        }


        int affordableCellCount =
            movementAllowance != null
                ? movementAllowance.GetAffordableCellCount(path)
                : path.Count;


        List<Vector3> affordablePoints = new List<Vector3>();

        affordablePoints.Add(
            gridMap.CellToWorld(playerController.CurrentCell)
        );

        for (int i = 0; i < affordableCellCount; i++)
        {
            affordablePoints.Add(
                gridMap.CellToWorld(path[i])
            );
        }


        List<Vector3> excessPoints = new List<Vector3>();

        bool hasExcess =
            affordableCellCount < path.Count;

        if (hasExcess)
        {
            // Start from wherever the affordable section ended, so the
            // excess dashes read as a continuation rather than a gap.
            excessPoints.Add(
                affordablePoints[affordablePoints.Count - 1]
            );

            for (int i = affordableCellCount; i < path.Count; i++)
            {
                excessPoints.Add(
                    gridMap.CellToWorld(path[i])
                );
            }
        }


        int affordableDotCount =
            PlaceDotsAlongPolyline(
                affordablePoints,
                affordableDotsContainer,
                affordablePool,
                affordableColor
            );

        int excessDotCount =
            PlaceDotsAlongPolyline(
                excessPoints,
                excessDotsContainer,
                excessPool,
                excessColor
            );

        DeactivateFrom(affordablePool, affordableDotCount);
        DeactivateFrom(excessPool, excessDotCount);


        if (destinationMarker != null)
        {
            destinationMarker.gameObject.SetActive(true);

            destinationMarker.position =
                gridMap.CellToWorld(path[path.Count - 1]);
        }
    }


    /// <summary>
    /// Walks the polyline formed by consecutive points, dropping an evenly
    /// spaced dot every (dotSize + gap) pixels - carrying leftover distance
    /// across segment boundaries so dashes stay evenly spaced through
    /// turns rather than resetting at every corner.
    /// </summary>
    private int PlaceDotsAlongPolyline(
        List<Vector3> points,
        Transform container,
        List<SpriteRenderer> pool,
        Color color)
    {
        if (points.Count < 2)
        {
            return 0;
        }


        float unitSize = 1.0f / pixelsPerUnit;

        float spacing =
            (dotSizePixels + gapPixels) *
            unitSize;

        float dotSize =
            dotSizePixels *
            unitSize;


        float distanceSinceLastDot = 0.0f;
        int dotIndex = 0;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 segmentStart = points[i];
            Vector3 segmentEnd = points[i + 1];

            float segmentLength =
                Vector3.Distance(
                    segmentStart,
                    segmentEnd
                );

            if (segmentLength < 0.0001f)
            {
                continue;
            }

            Vector3 direction =
                (segmentEnd - segmentStart) /
                segmentLength;

            float traveled = 0.0f;

            while (true)
            {
                float distanceToNextDot =
                    spacing - distanceSinceLastDot;

                if (traveled + distanceToNextDot > segmentLength)
                {
                    distanceSinceLastDot +=
                        segmentLength - traveled;

                    break;
                }

                traveled += distanceToNextDot;
                distanceSinceLastDot = 0.0f;

                Vector3 dotPosition =
                    SnapToPixelGrid(
                        segmentStart +
                        direction * traveled
                    );

                SpriteRenderer dot =
                    GetOrCreateDot(
                        pool,
                        dotIndex,
                        container
                    );

                dot.transform.position = dotPosition;
                dot.transform.localScale =
                    new Vector3(dotSize, dotSize, 1.0f);

                dot.color = color;
                dot.gameObject.SetActive(true);

                dotIndex++;
            }
        }

        return dotIndex;
    }


    /// <summary>
    /// Snaps a world position onto the pixel grid implied by
    /// pixelsPerUnit, so dots stay crisp instead of landing on a
    /// sub-pixel boundary.
    /// </summary>
    private Vector3 SnapToPixelGrid(Vector3 worldPosition)
    {
        float unitSize = 1.0f / pixelsPerUnit;

        return new Vector3(
            Mathf.Round(worldPosition.x / unitSize) * unitSize,
            Mathf.Round(worldPosition.y / unitSize) * unitSize,
            worldPosition.z
        );
    }


    /// <summary>
    /// Reuses a pooled dot if one already exists at this index, otherwise
    /// creates a new one. Pools only ever grow - existing dots beyond
    /// what's needed for the current path are deactivated, not destroyed.
    /// </summary>
    private SpriteRenderer GetOrCreateDot(
        List<SpriteRenderer> pool,
        int index,
        Transform container)
    {
        if (index < pool.Count)
        {
            return pool[index];
        }


        GameObject dotObject =
            new GameObject($"Dot_{index}");

        dotObject.transform.SetParent(
            container,
            false
        );

        SpriteRenderer dot =
            dotObject.AddComponent<SpriteRenderer>();

        dot.sprite = dotSprite;

        dot.sortingLayerName =
            ResolveSortingLayerName(
                sortingLayerName
            );

        dot.sortingOrder = sortingOrder;

        pool.Add(dot);

        return dot;
    }


    private void DeactivateFrom(List<SpriteRenderer> pool, int fromIndex)
    {
        for (int i = fromIndex; i < pool.Count; i++)
        {
            pool[i].gameObject.SetActive(false);
        }
    }


    /// <summary>
    /// Falls back to Default if the configured sorting layer no longer
    /// exists (e.g. renamed/removed in Project Settings), matching the
    /// convention used by FogOfWar/PlanetManager/StarfieldPainter.
    /// </summary>
    private string ResolveSortingLayerName(string requestedName)
    {
        foreach (SortingLayer layer in SortingLayer.layers)
        {
            if (layer.name == requestedName)
            {
                return requestedName;
            }
        }

        Debug.LogWarning(
            $"{name}: Sorting Layer \"{requestedName}\" does not exist. Falling back to Default.",
            this
        );

        return "Default";
    }
}