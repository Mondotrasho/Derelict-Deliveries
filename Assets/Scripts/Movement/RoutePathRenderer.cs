using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws the route currently being planned or partway through a
/// multi-turn journey, as a pixelated dashed line - a row of small square
/// dots rather than a smooth LineRenderer.
///
/// While nothing is moving, this draws whichever of RoutePlanner's live
/// plan or MovementPlanController's queued remainder is currently
/// relevant - the same priority MovementPlanController itself uses to
/// decide what CommitSegment() would actually commit.
///
/// While a segment is actively flying, drawing switches to a different
/// mode: the currently-executing segment is consumed live from the
/// ship's position (via PlayerGridController.RemainingCommandedPath) all
/// the way to its fixed target cell (CurrentCommandTargetCell, which does
/// not move until that segment finishes), with any further queued
/// segments drawn as a fixed continuation from that target. Neither of
/// those is re-derived from RoutePlanner/MovementPlanController's own
/// priority logic while moving - re-deriving through GetPathToDraw() at
/// every intermediate step of a commit (plan cleared, cost spent, queue
/// updated) is what previously caused the drawing to flicker, blank out,
/// or visibly shift right at the moment of commit.
///
/// Each turn-segment gets its own colour, cycling through segmentColors,
/// and (if showTurnLabels is on) a small configurable segment label to
/// the side of its own dashes - reusing PlanetLabel for the pooled
/// world-space text,
/// the same lightweight TextMesh approach already used for planet names.
///
/// This renderer only converts cells into world-space dot/reticle/label
/// positions - it does not decide affordability itself (MovementAllowance
/// does), and it does not move or commit anything.
///
/// Zero scene setup required: all dot, reticle and label GameObjects are
/// created and pooled automatically, at scene root rather than as
/// children of this object - they need to stay fixed in world space
/// regardless of where this component itself lives (e.g. under the
/// moving ship, purely for scene organisation), so they're deliberately
/// not parented to anything that could move. Dash spacing is anchored
/// from the final destination backwards, so consuming cells from the
/// player end removes dots without making every remaining dot slide.
/// Dot size/spacing are given in pixels rather than world units, assuming
/// pixelsPerUnit matches how the project's 8x8 tiles map to world units
/// (1 world unit = 1 tile is the common convention - adjust pixelsPerUnit
/// if that's not the case).
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

    [Tooltip("Optional. If assigned, the route is priced/segmented by actual budget and cost rules rather than shown as one undifferentiated line.")]
    [SerializeField]
    private MovementAllowance movementAllowance;

    [Tooltip("Optional. If assigned, a queued remainder from a previous segment commit is drawn as a continuation of the route, even once RoutePlanner's own plan has been cleared.")]
    [SerializeField]
    private MovementPlanController movementPlanController;

    [Tooltip("Optional. Used only to number turn labels correctly (\"Turn 3\", \"Turn 4\", ...) starting from the actual current turn. Labels still work without this assigned, just numbered from 1.")]
    [SerializeField]
    private TurnManager turnManager;


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

    [Tooltip("Cycled per turn-segment - segment 0 gets the first colour, segment 1 the second, and so on, wrapping around if there are more segments than colours. Both the dashes and that segment's reticle/label use the same colour.")]
    [SerializeField]
    private List<Color> segmentColors = new List<Color>
    {
        new Color(0.3f, 1.0f, 0.4f),
        new Color(1.0f, 0.85f, 0.3f),
        new Color(1.0f, 0.55f, 0.3f),
        new Color(1.0f, 0.35f, 0.3f),
    };

    [Tooltip("Only used for dots this component creates - if you retint via a shared material this can be ignored.")]
    [SortingLayerName]
    [SerializeField]
    private string sortingLayerName = "Default";

    [SerializeField]
    private int sortingOrder = 10;


    [Header("Turn Labels")]

    [Tooltip("Show or hide the per-segment text labels without affecting the route dots or checkpoint reticles.")]
    [SerializeField]
    private bool showTurnLabels = true;

    [Tooltip("When enabled, the label is placed directly on the segment target/checkpoint. When disabled, it is placed beside the route using the sideways offset settings below.")]
    [SerializeField]
    private bool placeLabelOnTarget = false;

    [Tooltip("Text written before the segment/turn number. For example, \"Turn \" produces \"Turn 2\". Leave empty to show only the number.")]
    [SerializeField]
    private string turnLabelPrefix = "Turn ";

    [Tooltip("Off (default): labels keep route-relative segment numbers, so Segment 2 stays 2 after Segment 1 is completed. On: labels are based on the live TurnManager turn number and shift as game turns advance.")]
    [SerializeField]
    private bool useCurrentTurnNumbering = false;

    [Tooltip("Left null to use TextMesh's built-in default font - a bitmap Font asset here gives crisp pixel-art text matching the rest of the project instead.")]
    [SerializeField]
    private Font labelFont;

    [Tooltip("Rasterisation size of the label font, in points - higher stays crisp at a larger labelWorldSize.")]
    [SerializeField]
    private int labelFontSize = 32;

    [Tooltip("The actual on-screen size of the label, in world units - independent of labelFontSize, which only affects rasterisation crispness.")]
    [SerializeField]
    private float labelWorldSize = 0.28f;

    [Tooltip("Base distance used to place each label beside its route segment. Horizontal clearance uses the full value; vertical clearance is reduced by Label Vertical Offset Scale so labels beside horizontal routes do not sit unnecessarily high.")]
    [SerializeField]
    private float labelSidewaysOffsetUnits = 0.35f;

    [Tooltip("Scales only the vertical part of the sideways label offset. Text is much wider than it is tall, so a horizontal route normally needs less vertical clearance than a vertical route needs horizontal clearance.")]
    [Range(0.0f, 1.0f)]
    [SerializeField]
    private float labelVerticalOffsetScale = 0.45f;


    private Sprite dotSprite;

    private Transform dotsContainer;

    private readonly List<SpriteRenderer> dotPool = new List<SpriteRenderer>();

    // One reticle per turn-segment checkpoint, grown/reused the same way
    // the dot pool is. If destinationMarker is assigned, it takes over
    // the position of the LAST checkpoint each draw, and this pool is
    // used for every checkpoint before it.
    private readonly List<SpriteRenderer> reticlePool = new List<SpriteRenderer>();

    // One label per turn-segment, same pooling shape as reticlePool.
    // PlanetLabel already does exactly the pooled-world-space-TextMesh
    // job this needs, despite the name - nothing about its implementation
    // is planet-specific.
    private readonly List<PlanetLabel> labelPool = new List<PlanetLabel>();

    // Fixed numbering is route-relative rather than tied to TurnManager.
    // Once Segment 1 physically arrives, the queued route begins at
    // Segment 2 and keeps that number even if another game turn starts.
    private bool fixedNumberingRouteActive = false;
    private int completedFixedSegments = 0;


    private void Awake()
    {
        dotSprite = CreateDotSprite();

        dotsContainer = CreateContainer("RouteDots");
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

        if (playerController != null)
        {
            playerController.CellEntered += HandleCellEntered;
            playerController.RouteCompleted += HandleRouteCompleted;
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

        if (playerController != null)
        {
            playerController.CellEntered -= HandleCellEntered;
            playerController.RouteCompleted -= HandleRouteCompleted;
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
    /// change per segment without regenerating the texture.
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
    /// Redraws for the latest planned path. A non-empty route created
    /// while stationary also starts a fresh fixed-numbering sequence.
    ///
    /// RouteChanged also fires with an empty path during CommitSegment()
    /// when RoutePlanner hands the route to movement. That must NOT reset
    /// numbering, because the same journey is now simply in flight.
    /// </summary>
    private void HandleRouteChanged(List<Vector3Int> path)
    {
        bool isMoving =
            playerController != null &&
            playerController.IsMoving;

        if (!useCurrentTurnNumbering &&
            !isMoving &&
            path != null &&
            path.Count > 0)
        {
            fixedNumberingRouteActive = true;
            completedFixedSegments = 0;
        }

        if (!isMoving &&
            (path == null || path.Count == 0) &&
            (movementPlanController == null ||
             !movementPlanController.HasQueuedRemainder))
        {
            ResetFixedNumbering();
        }

        RedrawFromCurrentState();
    }


    /// <summary>
    /// Remaining movement points, or the cost rules themselves, can change
    /// without the planned route itself changing, so redraw using
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


    /// <summary>
    /// The one and only per-cell trigger while travelling - CellEntered
    /// (not the lower-level CellReached, which also fires for pause/stop
    /// re-sync and would cause a spurious redraw at exactly the wrong
    /// moment) fires once per real cell actually reached, which is what
    /// drives the currently-executing segment being visibly consumed as
    /// the ship advances.
    /// </summary>
    private void HandleCellEntered(Vector3Int cell)
    {
        RedrawFromCurrentState();
    }


    /// <summary>
    /// Physical route completion is the point at which the CURRENT
    /// segment's checkpoint and label stop being relevant.
    ///
    /// Redrawing here means they disappear on actual arrival, rather than
    /// hanging around until TurnEnded/allowance refill causes another
    /// redraw. If a queued remainder exists, it is immediately redrawn as
    /// the next segment(s).
    /// </summary>
    private void HandleRouteCompleted()
    {
        bool hasQueuedRemainder =
            movementPlanController != null &&
            movementPlanController.HasQueuedRemainder;

        if (!useCurrentTurnNumbering &&
            fixedNumberingRouteActive)
        {
            completedFixedSegments++;
        }

        if (hasQueuedRemainder)
        {
            // The current segment has arrived, but the turn may not have
            // ended/refilled yet. Draw the queued route using FUTURE-turn
            // segmentation so a depleted current allowance does not
            // temporarily change the next segment's checkpoint layout.
            DrawQueuedRemainderAfterArrival(
                new List<Vector3Int>(
                    movementPlanController.QueuedRemainder
                )
            );
        }
        else
        {
            HideAllSegments();
            ResetFixedNumbering();
        }
    }


    /// <summary>
    /// Draws only the still-queued future journey immediately after the
    /// current physical segment arrives.
    ///
    /// At this moment the current turn's movement allowance may still be
    /// depleted, so future-turn segmentation is used deliberately. This
    /// keeps the next checkpoint/label exactly where it was while the
    /// current segment's checkpoint/label disappears immediately.
    /// </summary>
    private void DrawQueuedRemainderAfterArrival(
        List<Vector3Int> queuedRemainder)
    {
        if (queuedRemainder == null ||
            queuedRemainder.Count == 0 ||
            gridMap == null ||
            playerController == null)
        {
            HideAllSegments();
            return;
        }

        List<int> breakpoints =
            movementAllowance != null
                ? movementAllowance.GetFutureTurnSegmentBreakpoints(
                    playerController.CurrentCell,
                    queuedRemainder
                )
                : new List<int> { queuedRemainder.Count };

        int startingLabelNumber;

        if (useCurrentTurnNumbering)
        {
            // The visible queued route starts NEXT turn because the current
            // segment has physically arrived but TurnEnded has not
            // necessarily happened yet.
            startingLabelNumber =
                turnManager != null
                    ? turnManager.CurrentTurn + 1
                    : 2;
        }
        else
        {
            startingLabelNumber =
                completedFixedSegments + 1;
        }

        int startingVisualSegmentIndex =
            useCurrentTurnNumbering
                ? 1
                : completedFixedSegments;

        DrawMultiSegmentPath(
            playerController.CurrentCell,
            queuedRemainder,
            breakpoints,
            startingLabelNumber,
            startingVisualSegmentIndex
        );
    }


    private void ResetFixedNumbering()
    {
        fixedNumberingRouteActive = false;
        completedFixedSegments = 0;
    }


    private void RedrawFromCurrentState()
    {
        if (playerController != null && playerController.IsMoving)
        {
            DrawWhileMoving();
        }
        else
        {
            DrawIdlePlan(GetPathToDraw());
        }
    }


    /// <summary>
    /// A freshly planned RoutePlanner route if one exists - it always
    /// takes priority, the same as MovementPlanController.CommitSegment()
    /// would treat it - otherwise MovementPlanController's queued
    /// remainder from a previous segment, otherwise nothing to draw. Only
    /// meaningful while nothing is moving - RoutePlanner refuses new plans
    /// during active movement anyway, so this is never called while
    /// DrawWhileMoving's mode is in effect.
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
    /// Idle/planning mode: draws path in full from the player's current
    /// (stationary) position, segmented by whatever budget currently
    /// applies. Used whenever nothing is actively flying.
    /// </summary>
    private void DrawIdlePlan(List<Vector3Int> path)
    {
        if (path == null || path.Count == 0)
        {
            HideAllSegments();
            return;
        }

        if (gridMap == null || playerController == null)
        {
            Debug.LogError(
                "RoutePathRenderer is missing a required reference."
            );

            return;
        }

        List<int> breakpoints =
            movementAllowance != null
                ? movementAllowance.GetTurnSegmentBreakpoints(path)
                : new List<int> { path.Count };

        int startingLabelNumber =
            GetStartingLabelNumber();

        int startingVisualSegmentIndex =
            GetStartingVisualSegmentIndex();

        DrawMultiSegmentPath(
            playerController.CurrentCell,
            path,
            breakpoints,
            startingLabelNumber,
            startingVisualSegmentIndex
        );
    }


    /// <summary>
    /// Active-flight mode: the currently-executing segment is drawn as
    /// consumed live from the ship's position through to its fixed
    /// target, with any further queued segments drawn as a fixed
    /// continuation from that target - never re-anchored to the ship's
    /// live position, so they don't shift as it travels.
    /// </summary>
    private void DrawWhileMoving()
    {
        if (gridMap == null || playerController == null)
        {
            return;
        }

        Vector3Int? targetCell = playerController.CurrentCommandTargetCell;

        if (!targetCell.HasValue)
        {
            HideAllSegments();
            return;
        }

        List<Vector3Int> remaining =
            new List<Vector3Int>(playerController.RemainingCommandedPath);

        List<Vector3Int> queuedRemainder =
            movementPlanController != null
                ? new List<Vector3Int>(movementPlanController.QueuedRemainder)
                : new List<Vector3Int>();


        List<Vector3Int> fullPath = new List<Vector3Int>(remaining);
        fullPath.AddRange(queuedRemainder);

        List<int> breakpoints = new List<int> { remaining.Count };

        if (queuedRemainder.Count > 0)
        {
            List<int> futureBreakpoints =
                movementAllowance != null
                    ? movementAllowance.GetFutureTurnSegmentBreakpoints(
                        targetCell.Value,
                        queuedRemainder
                    )
                    : new List<int> { queuedRemainder.Count };

            foreach (int breakpoint in futureBreakpoints)
            {
                breakpoints.Add(remaining.Count + breakpoint);
            }
        }

        int startingLabelNumber =
            GetStartingLabelNumber();

        int startingVisualSegmentIndex =
            GetStartingVisualSegmentIndex();

        DrawMultiSegmentPath(
            playerController.CurrentCell,
            fullPath,
            breakpoints,
            startingLabelNumber,
            startingVisualSegmentIndex
        );
    }


    /// <summary>
    /// Gets the number shown on the first visible segment.
    ///
    /// Fixed mode is route-relative: 1, 2, 3... and completed segments
    /// stay consumed. Current-turn mode uses TurnManager as the label base.
    /// </summary>
    private int GetStartingLabelNumber()
    {
        if (useCurrentTurnNumbering)
        {
            return turnManager != null
                ? turnManager.CurrentTurn
                : 1;
        }

        return completedFixedSegments + 1;
    }


    /// <summary>
    /// Keeps segment colours aligned with fixed numbering as completed
    /// route sections disappear. In current-turn mode colours retain the
    /// original behaviour and start again from the first configured colour.
    /// </summary>
    private int GetStartingVisualSegmentIndex()
    {
        return useCurrentTurnNumbering
            ? 0
            : completedFixedSegments;
    }


    /// <summary>
    /// Shared drawing core for both idle and active-flight modes: one
    /// continuous dashed line from startCell through every cell in
    /// fullPath, coloured per turn-segment as defined by breakpoints
    /// (cumulative cell counts, exactly MovementAllowance.
    /// GetTurnSegmentBreakpoints' shape), with one reticle and one turn
    /// label per segment boundary.
    /// </summary>
    private void DrawMultiSegmentPath(
        Vector3Int startCell,
        List<Vector3Int> fullPath,
        List<int> breakpoints,
        int startingLabelNumber,
        int startingVisualSegmentIndex)
    {
        if (fullPath == null || fullPath.Count == 0 ||
            breakpoints == null || breakpoints.Count == 0)
        {
            HideAllSegments();
            return;
        }


        List<Vector3> allPoints = new List<Vector3>(fullPath.Count + 1)
        {
            gridMap.CellToWorld(startCell)
        };

        foreach (Vector3Int cell in fullPath)
        {
            allPoints.Add(gridMap.CellToWorld(cell));
        }

        List<Color> colorPerEdge = new List<Color>(fullPath.Count);
        int segmentIndexForEdge = 0;

        for (int cellIndex = 0; cellIndex < fullPath.Count; cellIndex++)
        {
            while (segmentIndexForEdge < breakpoints.Count - 1 &&
                   cellIndex >= breakpoints[segmentIndexForEdge])
            {
                segmentIndexForEdge++;
            }

            colorPerEdge.Add(
                GetSegmentColor(
                    startingVisualSegmentIndex +
                    segmentIndexForEdge
                )
            );
        }

        int dotCount = PlaceDotsAlongPolyline(allPoints, colorPerEdge, dotPool);
        DeactivateFrom(dotPool, dotCount);


        int pooledReticleCount = 0;
        int previousBreak = 0;

        for (int segmentIndex = 0; segmentIndex < breakpoints.Count; segmentIndex++)
        {
            int breakIndex = breakpoints[segmentIndex];

            if (breakIndex <= previousBreak)
            {
                // Zero-length segment - e.g. DrawWhileMoving is called on
                // the one frame the current command has just been fully
                // consumed but hasn't cleared yet. Nothing to place; move
                // on without advancing previousBreak's cell reference.
                continue;
            }

            Vector3 segmentStartWorld =
                previousBreak == 0
                    ? allPoints[0]
                    : gridMap.CellToWorld(fullPath[previousBreak - 1]);

            Vector3 segmentEndWorld =
                gridMap.CellToWorld(fullPath[breakIndex - 1]);

            Color color =
                GetSegmentColor(
                    startingVisualSegmentIndex +
                    segmentIndex
                );

            bool isFinalSegment = segmentIndex == breakpoints.Count - 1;

            if (isFinalSegment && destinationMarker != null)
            {
                destinationMarker.gameObject.SetActive(true);
                destinationMarker.position = segmentEndWorld;
            }
            else
            {
                SpriteRenderer reticle = GetOrCreateReticle(pooledReticleCount);

                reticle.transform.position = segmentEndWorld;
                reticle.color = color;
                reticle.gameObject.SetActive(true);

                pooledReticleCount++;
            }

            if (showTurnLabels)
            {
                DrawTurnLabel(
                    segmentIndex,
                    segmentStartWorld,
                    segmentEndWorld,
                    startingLabelNumber + segmentIndex,
                    color
                );
            }

            previousBreak = breakIndex;
        }

        DeactivateFrom(reticlePool, pooledReticleCount);
        DeactivateLabelsFrom(showTurnLabels ? breakpoints.Count : 0);
    }


    private Color GetSegmentColor(int segmentIndex)
    {
        if (segmentColors == null || segmentColors.Count == 0)
        {
            return Color.white;
        }

        return segmentColors[segmentIndex % segmentColors.Count];
    }


    private void HideAllSegments()
    {
        DeactivateFrom(dotPool, 0);
        DeactivateFrom(reticlePool, 0);
        DeactivateLabelsFrom(0);

        if (destinationMarker != null)
        {
            destinationMarker.gameObject.SetActive(false);
        }
    }


    /// <summary>
    /// Positions one pooled "Turn N" label beside its segment.
    ///
    /// The path normal is still used so the label sits off the line, but
    /// its vertical component is reduced. A horizontal TextMesh is much
    /// wider than it is tall, so using the same full offset vertically
    /// made labels beside horizontal routes sit noticeably too high.
    /// </summary>
    private void DrawTurnLabel(
        int poolIndex,
        Vector3 segmentStartWorld,
        Vector3 segmentEndWorld,
        int turnNumber,
        Color color)
    {
        PlanetLabel label = GetOrCreateLabel(poolIndex);

        Vector3 midpoint =
            (segmentStartWorld + segmentEndWorld) *
            0.5f;

        Vector3 direction =
            segmentEndWorld - segmentStartWorld;

        Vector3 perpendicular =
            direction.sqrMagnitude > 0.0001f
                ? new Vector3(
                    -direction.y,
                    direction.x,
                    0.0f
                ).normalized
                : Vector3.right;

        Vector3 labelPosition;

        // Keep the full horizontal clearance, but reduce vertical
        // clearance so a horizontal route gets a label close beside it
        // rather than floating well above it.
        Vector3 labelOffset =
            new Vector3(
                perpendicular.x,
                perpendicular.y * Mathf.Clamp01(labelVerticalOffsetScale),
                0.0f
            ) *
            labelSidewaysOffsetUnits;

        // The toggle changes only the anchor point. Both modes use the
        // same sideways offset so the text never sits directly on the line
        // or reticle unless the configured offset is zero.
        Vector3 labelAnchor =
            placeLabelOnTarget
                ? segmentEndWorld
                : midpoint;

        labelPosition =
            labelAnchor + labelOffset;

        label.SetWorldPosition(
            labelPosition
        );

        label.SetText(
            $"{turnLabelPrefix}{turnNumber}",
            color
        );
        label.SetActive(true);
    }


    /// <summary>
    /// Reuses a pooled label if one already exists at this index,
    /// otherwise creates a new one - same growth-only pooling as
    /// GetOrCreateDot/GetOrCreateReticle. PlanetLabel creates its own
    /// GameObject/TextMesh internally, so this only needs to pass the
    /// configured font/size/scale/sorting through once, at creation.
    /// </summary>
    private PlanetLabel GetOrCreateLabel(int index)
    {
        if (index < labelPool.Count)
        {
            return labelPool[index];
        }

        PlanetLabel label =
            PlanetLabel.Create(
                dotsContainer,
                labelFont,
                labelFontSize,
                labelWorldSize,
                ResolveSortingLayerName(sortingLayerName),
                sortingOrder + 1
            );

        labelPool.Add(label);

        return label;
    }


    private void DeactivateLabelsFrom(int fromIndex)
    {
        for (int i = fromIndex; i < labelPool.Count; i++)
        {
            labelPool[i].SetActive(false);
        }
    }


    /// <summary>
    /// Walks the polyline BACKWARDS from the final destination, dropping
    /// an evenly spaced dot every (dotSize + gap) pixels.
    ///
    /// Anchoring the dash phase at the destination is important while the
    /// ship is moving. The player-side start of the visible route keeps
    /// being consumed, but the destination stays fixed. Drawing backwards
    /// therefore makes existing dots stay in the same world positions and
    /// simply disappear from the player end as they are passed.
    ///
    /// Leftover distance is carried across corners and colour boundaries,
    /// so spacing remains continuous over the complete remaining route.
    /// colorPerEdge[i] is still the colour for the edge between points[i]
    /// and points[i + 1].
    /// </summary>
    private int PlaceDotsAlongPolyline(
        List<Vector3> points,
        List<Color> colorPerEdge,
        List<SpriteRenderer> pool)
    {
        if (points.Count < 2)
        {
            return 0;
        }


        float unitSize =
            1.0f / pixelsPerUnit;

        float spacing =
            (dotSizePixels + gapPixels) *
            unitSize;

        float dotSize =
            dotSizePixels *
            unitSize;


        // This phase now starts at the FINAL destination instead of the
        // player-side start. As the route is consumed from the front, the
        // phase of every remaining dot therefore stays fixed.
        float distanceSinceLastDot = 0.0f;
        int dotIndex = 0;

        for (int i = points.Count - 2; i >= 0; i--)
        {
            // Traverse this edge from its destination-side endpoint back
            // toward the player-side endpoint.
            Vector3 segmentStart = points[i + 1];
            Vector3 segmentEnd = points[i];

            Color color =
                i < colorPerEdge.Count
                    ? colorPerEdge[i]
                    : Color.white;

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
                        dotIndex
                    );

                dot.transform.position = dotPosition;
                dot.transform.localScale =
                    new Vector3(
                        dotSize,
                        dotSize,
                        1.0f
                    );

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
        int index)
    {
        if (index < pool.Count)
        {
            return pool[index];
        }


        GameObject dotObject =
            new GameObject($"Dot_{index}");

        dotObject.transform.SetParent(
            dotsContainer,
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


    /// <summary>
    /// Reuses a pooled reticle if one already exists at this index,
    /// otherwise creates a new one - same growth-only pooling as
    /// GetOrCreateDot.
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