using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws the route currently being planned or partway through a
/// multi-turn journey, as a pixelated dashed line - a row of small square
/// dots rather than a smooth LineRenderer - split into the affordable
/// prefix and any "over budget" tail according to MovementAllowance, plus
/// one reticle at the end of every turn-sized segment
/// (MovementAllowance.GetTurnSegmentBreakpoints) rather than only at the
/// final destination.
///
/// Draws whichever of RoutePlanner's live plan or
/// MovementPlanController's queued remainder is currently relevant - the
/// same priority MovementPlanController itself uses to decide what
/// CommitSegment() would actually commit - so the remaining legs of a
/// multi-turn journey stay visible even after the first segment has
/// already been committed and cleared from RoutePlanner.
///
/// This renderer only converts cells into world-space dot/reticle
/// positions - it does not decide affordability itself (MovementAllowance
/// does), and it does not move or commit anything.
///
/// Zero scene setup required: all dot and reticle GameObjects are created
/// and pooled automatically, at scene root rather than as children of
/// this object - they need to stay fixed in world space regardless of
/// where this component itself lives (e.g. under the moving ship, purely
/// for scene organisation), so they're deliberately not parented to
/// anything that could move. Dot size/spacing are given in pixels rather
/// than world units, assuming pixelsPerUnit matches how the project's
/// 8x8 tiles map to world units (1 world unit = 1 tile is the common
/// convention - adjust pixelsPerUnit if that's not the case).
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

    [Tooltip("Optional. If assigned, the route is split into affordable/excess segments and into turn-sized reticle checkpoints.")]
    [SerializeField]
    private MovementAllowance movementAllowance;

    [Tooltip("Optional. If assigned, a queued remainder from a previous segment commit is drawn as a continuation of the route, even once RoutePlanner's own plan has been cleared.")]
    [SerializeField]
    private MovementPlanController movementPlanController;

    [Header("Destination Reticle")]

    [Tooltip("Optional. If assigned, this is moved to the FINAL destination instead of the auto-generated reticle pool - use this if you want your own themed marker art for the end of the route. Every earlier turn-segment checkpoint always uses the auto-generated pool regardless.")]
    [SerializeField]
    private Transform destinationMarker;

    [Tooltip("Size of the auto-generated reticle, in pixels. Slightly larger than pixelsPerUnit frames the destination tile with a bit of overhang.")]
    [SerializeField]
    private int reticleSizePixels = 10;

    [Tooltip("Length of each corner bracket on the auto-generated reticle, in pixels.")]
    [SerializeField]
    private int reticleBracketLengthPixels = 3;


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

    // One reticle per turn-segment checkpoint, grown/reused the same way
    // the dot pools are. If destinationMarker is assigned, it takes over
    // the position of the LAST checkpoint each draw, and this pool is
    // used for every checkpoint before it.
    private readonly List<SpriteRenderer> reticlePool = new List<SpriteRenderer>();


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
            movementAllowance.MovementCostRulesChanged += HandleAllowanceChanged;
        }

        if (movementPlanController != null)
        {
            movementPlanController.QueuedRemainderChanged += HandleQueuedRemainderChanged;
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
            movementAllowance.MovementCostRulesChanged -= HandleAllowanceChanged;
        }

        if (movementPlanController != null)
        {
            movementPlanController.QueuedRemainderChanged -= HandleQueuedRemainderChanged;
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


    /// <summary>
    /// Deliberately NOT parented to this component's own transform - this
    /// object may live under the ship (e.g. as a child used purely for
    /// scene organisation), and dots/reticles need to stay fixed in world
    /// space as the ship flies past them, not travel along with it. Every
    /// position is set explicitly every redraw regardless, so there's no
    /// benefit to parenting them under anything that moves.
    /// </summary>
    private Transform CreateContainer(string containerName)
    {
        GameObject container =
            new GameObject($"{name}_{containerName}");

        return container.transform;
    }


    /// <summary>
    /// Builds one auto-generated reticle marker: a small square texture
    /// with four corner brackets, like a targeting reticle, point-filtered
    /// so it stays crisp at any scale. Used both for the pooled per-segment
    /// checkpoints and, when destinationMarker is left unassigned, for the
    /// final destination too.
    ///
    /// Deliberately not parented to this component's own transform, for
    /// the same reason as CreateContainer above - it needs to stay fixed
    /// in world space, not travel with whatever GameObject this script
    /// happens to live on.
    /// </summary>
    private SpriteRenderer CreateReticle(string objectName)
    {
        GameObject reticleObject =
            new GameObject($"{name}_{objectName}");

        SpriteRenderer reticle =
            reticleObject.AddComponent<SpriteRenderer>();

        reticle.sprite = CreateReticleSprite();

        reticle.sortingLayerName =
            ResolveSortingLayerName(
                sortingLayerName
            );

        // One above the dashes, so the reticle never gets hidden behind
        // a dot that happens to land on the same cell.
        reticle.sortingOrder = sortingOrder + 1;

        reticle.gameObject.SetActive(false);

        return reticle;
    }


    /// <summary>
    /// Procedurally draws an NxN transparent texture with a short opaque
    /// bracket in each corner - i.e. a targeting reticle - so no marker
    /// art needs to be authored by hand. Pixels are plain white; the
    /// actual colour is applied later via SpriteRenderer.color so it can
    /// change (affordable vs excess) without regenerating the texture.
    /// </summary>
    private Sprite CreateReticleSprite()
    {
        int size = Mathf.Max(2, reticleSizePixels);

        int bracketLength =
            Mathf.Clamp(
                reticleBracketLengthPixels,
                1,
                size / 2
            );


        Texture2D texture =
            new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false
            );

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;


        Color clear = new Color(0.0f, 0.0f, 0.0f, 0.0f);
        Color[] pixels = new Color[size * size];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = clear;
        }

        texture.SetPixels(pixels);


        for (int i = 0; i < bracketLength; i++)
        {
            // Bottom-left corner
            SetReticlePixel(texture, size, i, 0);
            SetReticlePixel(texture, size, 0, i);

            // Bottom-right corner
            SetReticlePixel(texture, size, size - 1 - i, 0);
            SetReticlePixel(texture, size, size - 1, i);

            // Top-left corner
            SetReticlePixel(texture, size, i, size - 1);
            SetReticlePixel(texture, size, 0, size - 1 - i);

            // Top-right corner
            SetReticlePixel(texture, size, size - 1 - i, size - 1);
            SetReticlePixel(texture, size, size - 1, size - 1 - i);
        }

        texture.Apply();


        return Sprite.Create(
            texture,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit
        );
    }


    private void SetReticlePixel(Texture2D texture, int size, int x, int y)
    {
        if (x < 0 || x >= size || y < 0 || y >= size)
        {
            return;
        }

        texture.SetPixel(x, y, Color.white);
    }


    /// <summary>
    /// Redraws for the latest planned path.
    /// </summary>
    private void HandleRouteChanged(List<Vector3Int> path)
    {
        // Deliberately ignore the path parameter and re-derive it fresh -
        // RouteChanged fires with an empty path the instant
        // MovementPlanController.CommitSegment() clears RoutePlanner as
        // part of committing, and drawing that literally would blank out
        // a queued remainder that should still be visible.
        RedrawFromCurrentState();
    }


    /// <summary>
    /// Remaining movement points can change (e.g. spent by a previous
    /// commit) without the planned route itself changing, so redraw using
    /// whatever is currently relevant.
    /// </summary>
    private void HandleAllowanceChanged()
    {
        RedrawFromCurrentState();
    }


    /// <summary>
    /// A segment commit can leave a new queued remainder, or clear the
    /// last one, without RoutePlanner's own plan changing at all - redraw
    /// to match either way.
    /// </summary>
    private void HandleQueuedRemainderChanged()
    {
        RedrawFromCurrentState();
    }


    private void RedrawFromCurrentState()
    {
        DrawPath(GetPathToDraw());
    }


    /// <summary>
    /// A freshly planned RoutePlanner route if one exists - it always
    /// takes priority, the same as MovementPlanController.CommitSegment()
    /// would treat it - otherwise MovementPlanController's queued
    /// remainder from a previous segment, otherwise nothing to draw.
    /// </summary>
    private List<Vector3Int> GetPathToDraw()
    {
        if (routePlanner != null && routePlanner.HasPlannedRoute)
        {
            return new List<Vector3Int>(routePlanner.PlannedPath);
        }

        if (movementPlanController != null &&
            movementPlanController.HasQueuedRemainder)
        {
            return new List<Vector3Int>(movementPlanController.QueuedRemainder);
        }

        return null;
    }


    /// <summary>
    /// Rebuilds the dashed dots from the player's current position through
    /// every cell in the planned path - split at the point where the
    /// route stops being affordable - and places one reticle at the end
    /// of every turn-sized segment.
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

            HideAllReticles();

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


        List<int> breakpoints =
            movementAllowance != null
                ? movementAllowance.GetTurnSegmentBreakpoints(path)
                : new List<int> { path.Count };

        DrawSegmentReticles(path, breakpoints);
    }


    /// <summary>
    /// Places one reticle at the end of every turn-segment breakpoint -
    /// the first (this-turn) checkpoint in affordableColor, every later
    /// one in excessColor, the same two-colour vocabulary the dashes
    /// already use. If destinationMarker is assigned, it takes over the
    /// position of only the LAST checkpoint (the true final destination);
    /// every earlier checkpoint always uses the auto-generated pool
    /// regardless, since a single custom Transform can't stand in for
    /// more than one position at once.
    /// </summary>
    private void DrawSegmentReticles(List<Vector3Int> path, List<int> breakpoints)
    {
        int pooledReticleCount = 0;

        for (int segmentIndex = 0; segmentIndex < breakpoints.Count; segmentIndex++)
        {
            int cellIndex = breakpoints[segmentIndex] - 1;

            if (cellIndex < 0 || cellIndex >= path.Count)
            {
                continue;
            }

            Vector3 worldPosition =
                gridMap.CellToWorld(path[cellIndex]);

            bool isFinalSegment =
                segmentIndex == breakpoints.Count - 1;

            if (isFinalSegment && destinationMarker != null)
            {
                destinationMarker.gameObject.SetActive(true);
                destinationMarker.position = worldPosition;

                continue;
            }

            Color color =
                segmentIndex == 0
                    ? affordableColor
                    : excessColor;

            SpriteRenderer reticle =
                GetOrCreateReticle(pooledReticleCount);

            reticle.transform.position = worldPosition;
            reticle.color = color;
            reticle.gameObject.SetActive(true);

            pooledReticleCount++;
        }

        DeactivateFrom(reticlePool, pooledReticleCount);
    }


    private void HideAllReticles()
    {
        DeactivateFrom(reticlePool, 0);

        if (destinationMarker != null)
        {
            destinationMarker.gameObject.SetActive(false);
        }
    }


    /// <summary>
    /// Reuses a pooled reticle if one already exists at this index,
    /// otherwise creates a new one - same growth-only pooling as
    /// GetOrCreateDot below.
    /// </summary>
    private SpriteRenderer GetOrCreateReticle(int index)
    {
        if (index < reticlePool.Count)
        {
            return reticlePool[index];
        }

        SpriteRenderer reticle =
            CreateReticle($"SegmentReticle_{index}");

        reticlePool.Add(reticle);

        return reticle;
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