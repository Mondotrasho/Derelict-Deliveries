using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shows one small world-space planet label while the mouse is over a planet.
///
/// Hover rules:
/// - no planet under the mouse       -> no label
/// - effective fog visibility Hidden -> no label
/// - knowledgeState Unknown          -> fully garbled text using garbleCharacters
/// - knowledgeState Detected         -> partially garbled real name
/// - knowledgeState Identified       -> real display name
///
/// Partial vision normally raises knowledge to Detected, so a partially
/// visible planet uses the existing corrupt/garbled text effect rather than
/// the fixed Unknown placeholder.
///
/// "Effective" visibility means what FogOfWar is actually rendering after
/// combining live player vision, remembered fog and starting/locked reveals.
///
/// Planets are Tilemap data rather than individual GameObjects, so this manager
/// converts the mouse position to the same Grid cell used by Planet.cell.
/// No planet colliders are required.
/// </summary>
public class PlanetLabelManager : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlanetManager planetManager;

    [SerializeField]
    private VisionManager visionManager;

    [Tooltip("Camera used for mouse-to-world conversion. Leave empty to use Camera.main.")]
    [SerializeField]
    private Camera mainCamera;


    [Header("Label Look")]

    [Tooltip("Leave empty to use TextMesh's built-in default font.")]
    [SerializeField]
    private Font labelFont;

    [SerializeField]
    private int fontSize = 12;

    [Tooltip("TextMesh characters are large by default, so this scales the label down for the game world.")]
    [SerializeField]
    private float labelScale = 0.02f;

    [Header("Callout Layout")]

    [Tooltip("World-space distance from the planet to the label anchor. The manager automatically chooses the diagonal quadrant that best fits on screen.")]
    [SerializeField]
    private Vector2 labelDiagonalOffset =
        new Vector2(1.15f, 0.75f);

    [Tooltip("Minimum screen-space gap kept between the rendered label and the camera edges.")]
    [SerializeField]
    private float screenPaddingPixels = 8.0f;

    [Tooltip("Pixel density used by the generated 8x8 planet reticle and connector thickness. 8 matches the project's 8x8 tile art.")]
    [SerializeField]
    private int calloutPixelsPerUnit = 8;

    [Tooltip("Thickness of the connector line in source pixels.")]
    [SerializeField]
    private int connectorThicknessPixels = 1;

    [Tooltip("Sorting order used by the connector. The reticle and text are drawn above this.")]
    [SerializeField]
    private int calloutSortingOrder = 100;

    [SerializeField]
    private Color connectorColor =
        Color.white;

    [SerializeField]
    private Color reticleColor =
        Color.white;

    [Tooltip("Uniform scale applied to the 8x8 planet reticle around its centre. 1 = native size, 0.5 = half size, 2 = double size.")]
    [Min(0.05f)]
    [SerializeField]
    private float reticleScale = 1.0f;


    [SerializeField]
    private Color identifiedColor =
        Color.white;

    [SerializeField]
    private Color garbledColor =
        new Color(0.6f, 0.85f, 1.0f);

    [SortingLayerName]
    [SerializeField]
    private string sortingLayerName =
        "Default";

    [SerializeField]
    private int sortingOrder = 20;


    [Header("Garble")]

    [Tooltip("Chance each character remains correct for a Detected planet at Partial visibility.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float partialRevealChance = 0.35f;

    [Tooltip("Chance each character remains correct for a Detected planet at Full visibility.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float fullRevealChance = 0.85f;

    [Tooltip("How often a Detected planet's corrupted name changes.")]
    [SerializeField]
    private float garbleRefreshInterval = 0.35f;

    [SerializeField]
    private string garbleCharacters =
        "!@#$%^&*-_=+?<>01";

    [Tooltip("Fallback length used for Unknown text if the planet has no display name or id.")]
    [SerializeField]
    private int unknownFallbackLength = 5;


    [Header("Debug")]

    [Tooltip("Prints hover, visibility and draw decisions when the state changes.")]
    [SerializeField]
    private bool debugHoverLabels = false;

    [Tooltip("Also print cells that contain no planet. Useful for checking mouse-to-grid conversion.")]
    [SerializeField]
    private bool debugEmptyCells = false;


    private readonly Dictionary<string, PlanetLabel>
        labelsByPlanetId =
            new Dictionary<string, PlanetLabel>();

    // Presentation objects created alongside each PlanetLabel. Keeping
    // these separate means PlanetLabel can stay the small text wrapper it
    // already is while this manager owns the callout reticle/connector.
    private readonly Dictionary<string, Renderer>
        labelRenderersByPlanetId =
            new Dictionary<string, Renderer>();

    private readonly Dictionary<string, SpriteRenderer>
        reticlesByPlanetId =
            new Dictionary<string, SpriteRenderer>();

    private readonly Dictionary<string, SpriteRenderer>
        connectorsByPlanetId =
            new Dictionary<string, SpriteRenderer>();

    private Sprite planetReticleSprite;
    private Sprite connectorSprite;

    private Grid planetGrid;

    private string activePlanetId;
    private string lastDebugState;

    private float garbleTimer;


    private void Awake()
    {
        ResolveReferences();

        planetReticleSprite =
            CreatePlanetReticleSprite();

        connectorSprite =
            CreateConnectorSprite();
    }


    private void Start()
    {
        ResolveReferences();

        DebugState(
            "PlanetLabelManager STARTED."
        );
    }


    private void Update()
    {
        garbleTimer +=
            Time.deltaTime;

        bool refreshGarble =
            garbleTimer >=
            Mathf.Max(
                0.01f,
                garbleRefreshInterval
            );

        if (refreshGarble)
        {
            garbleTimer = 0.0f;
        }


        if (!ResolveReferences())
        {
            DebugState(
                "NOT READY - " +
                GetMissingReferenceDescription()
            );

            HideActiveLabel();
            return;
        }


        Planet hoveredPlanet =
            GetHoveredPlanet(
                out Vector3Int hoveredCell,
                out Vector3 mouseWorldPosition,
                out string hoverReason
            );


        if (hoveredPlanet == null)
        {
            if (debugEmptyCells)
            {
                DebugState(
                    $"NO LABEL - screen/world hover maps to cell {hoveredCell}, " +
                    $"world {mouseWorldPosition}: {hoverReason}"
                );
            }
            else
            {
                // Reset the state key when leaving a planet so hovering the
                // same planet again produces fresh diagnostics.
                lastDebugState =
                    $"EMPTY:{hoveredCell}";
            }

            HideActiveLabel();
            return;
        }


        FogOfWar.VisibilityTier liveTier =
            visionManager.GetLiveVisibility(
                hoveredPlanet.cell
            );

        FogOfWar.VisibilityTier effectiveTier =
            visionManager.GetEffectiveVisibility(
                hoveredPlanet.cell
            );

        PlanetKnowledgeState knowledgeState =
            hoveredPlanet.visibility != null
                ? hoveredPlanet.visibility.knowledgeState
                : PlanetKnowledgeState.Unknown;


        DebugState(
            $"PLANET HIT - cell {hoveredCell}, " +
            $"planet \"{hoveredPlanet.id}\", " +
            $"live {liveTier}, effective {effectiveTier}, " +
            $"knowledge {knowledgeState}."
        );


        // Effective visibility is what is actually visible through FogOfWar.
        // This includes authored starting reveals and remembered/locked areas.
        if (effectiveTier ==
            FogOfWar.VisibilityTier.Hidden)
        {
            DebugState(
                $"NO LABEL - planet \"{hoveredPlanet.id}\" is effectively Hidden."
            );

            HideActiveLabel();
            return;
        }


        ShowLabel(
            hoveredPlanet,
            liveTier,
            effectiveTier,
            refreshGarble
        );
    }


    /// <summary>
    /// Fills missing scene references automatically.
    /// </summary>
    private bool ResolveReferences()
    {
        if (planetManager == null)
        {
            planetManager =
                FindFirstObjectByType<PlanetManager>();
        }

        if (visionManager == null)
        {
            visionManager =
                FindFirstObjectByType<VisionManager>();
        }

        if (mainCamera == null)
        {
            mainCamera =
                Camera.main;
        }

        if (planetGrid == null &&
            planetManager != null)
        {
            planetGrid =
                planetManager.GetComponentInParent<Grid>();
        }


        return
            planetManager != null &&
            visionManager != null &&
            mainCamera != null &&
            planetGrid != null;
    }


    /// <summary>
    /// Finds the planet under the mouse by converting the mouse ray to the
    /// planet Grid plane, then comparing the resulting cell with Planet.cell.
    ///
    /// This deliberately does NOT use EventSystem.IsPointerOverGameObject().
    /// A full-screen Canvas/Image can report the pointer as "over UI" across
    /// the whole game view even when it is only decorative, which previously
    /// prevented hover processing from ever reaching the grid conversion.
    /// </summary>
    private Planet GetHoveredPlanet(
        out Vector3Int hoveredCell,
        out Vector3 mouseWorldPosition,
        out string reason)
    {
        hoveredCell =
            new Vector3Int(
                int.MinValue,
                int.MinValue,
                int.MinValue
            );

        mouseWorldPosition =
            Vector3.zero;

        reason =
            string.Empty;


        if (Mouse.current == null)
        {
            reason =
                "Mouse.current is null.";

            return null;
        }


        Vector2 mouseScreenPosition =
            Mouse.current.position.ReadValue();


        Ray mouseRay =
            mainCamera.ScreenPointToRay(
                new Vector3(
                    mouseScreenPosition.x,
                    mouseScreenPosition.y,
                    0.0f
                )
            );


        // The generated planet Tilemap is a child of PlanetManager and lives
        // on the same XY grid plane. Using the manager's Z makes this robust if
        // the whole map hierarchy is moved away from world Z = 0.
        Plane gridPlane =
            new Plane(
                Vector3.forward,
                new Vector3(
                    0.0f,
                    0.0f,
                    planetManager.transform.position.z
                )
            );


        if (!gridPlane.Raycast(
                mouseRay,
                out float rayDistance))
        {
            reason =
                "Mouse ray did not intersect the planet grid plane.";

            return null;
        }


        mouseWorldPosition =
            mouseRay.GetPoint(
                rayDistance
            );


        hoveredCell =
            planetGrid.WorldToCell(
                mouseWorldPosition
            );


        IReadOnlyList<Planet> planets =
            planetManager.Planets;

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
            {
                continue;
            }

            if (planet.cell ==
                hoveredCell)
            {
                reason =
                    $"Found planet \"{planet.id}\".";

                return planet;
            }
        }


        reason =
            "No planet in hovered cell.";

        return null;
    }


    /// <summary>
    /// Shows the correct text for the hovered planet.
    ///
    /// Live Full is allowed to display the identity immediately so label
    /// presentation cannot lag one frame behind VisionManager's knowledge
    /// update. Otherwise only an already-Identified planet shows its real name.
    /// </summary>
    private void ShowLabel(
        Planet planet,
        FogOfWar.VisibilityTier liveTier,
        FogOfWar.VisibilityTier effectiveTier,
        bool refreshGarble)
    {
        bool changedPlanet =
            activePlanetId !=
            planet.id;

        if (changedPlanet)
        {
            HideActiveLabel();
        }


        PlanetLabel label =
            GetOrCreateLabel(
                planet.id
            );


        PlanetKnowledgeState knowledgeState =
            planet.visibility != null
                ? planet.visibility.knowledgeState
                : PlanetKnowledgeState.Unknown;


        string textToDraw = null;
        Color colorToDraw =
            garbledColor;


        switch (knowledgeState)
        {
            case PlanetKnowledgeState.Identified:

                textToDraw =
                    GetDisplayName(
                        planet
                    );

                colorToDraw =
                    identifiedColor;

                break;


            case PlanetKnowledgeState.Detected:

                // Keep the current corrupted text until the refresh timer
                // expires. A newly hovered label always generates immediately.
                if (changedPlanet ||
                    refreshGarble ||
                    !label.HasText)
                {
                    float revealChance =
                        effectiveTier ==
                            FogOfWar.VisibilityTier.Full
                            ? fullRevealChance
                            : partialRevealChance;

                    textToDraw =
                        GenerateGarbledText(
                            GetDisplayName(
                                planet
                            ),
                            revealChance
                        );

                    label.SetText(
                        textToDraw,
                        garbledColor
                    );
                }

                break;


            case PlanetKnowledgeState.Unknown:
            default:

                // Unknown must not reveal any real-name characters, but it
                // should still use the same corruption character set as the
                // rest of the label system. Preserve spaces/length only.
                if (changedPlanet ||
                    refreshGarble ||
                    !label.HasText)
                {
                    textToDraw =
                        GenerateUnknownText(
                            GetDisplayName(
                                planet
                            )
                        );

                    label.SetText(
                        textToDraw,
                        garbledColor
                    );
                }

                break;
        }


        if (knowledgeState ==
            PlanetKnowledgeState.Identified)
        {
            label.SetText(
                textToDraw,
                colorToDraw
            );
        }


        label.SetActive(
            true
        );

        Vector3 labelWorldPosition =
            LayoutCallout(
                planet,
                label
            );

        activePlanetId =
            planet.id;


        DebugState(
            $"DRAW LABEL - planet \"{planet.id}\" | " +
            $"knowledge {knowledgeState} | " +
            $"live {liveTier} | effective {effectiveTier} | " +
            $"text \"{(textToDraw ?? "(unchanged garble)")}\" | " +
            $"world {labelWorldPosition}."
        );
    }


    /// <summary>
    /// Builds the corrupted version of a detected planet's real display name.
    /// Spaces stay intact. Every other character either survives or is replaced
    /// with a random corruption character according to revealChance.
    /// </summary>
    private string GenerateGarbledText(
        string realName,
        float revealChance)
    {
        if (string.IsNullOrEmpty(
                realName))
        {
            return GenerateUnknownText(
                string.Empty
            );
        }


        if (string.IsNullOrEmpty(
                garbleCharacters))
        {
            return realName;
        }


        StringBuilder builder =
            new StringBuilder(
                realName.Length
            );


        foreach (char character in realName)
        {
            if (character == ' ')
            {
                builder.Append(' ');
                continue;
            }


            if (Random.value <
                revealChance)
            {
                builder.Append(
                    character
                );
            }
            else
            {
                builder.Append(
                    garbleCharacters[
                        Random.Range(
                            0,
                            garbleCharacters.Length
                        )
                    ]
                );
            }
        }


        return builder.ToString();
    }


    /// <summary>
    /// Creates fully corrupted text for an Unknown planet.
    ///
    /// No real letters are revealed. The output uses only garbleCharacters,
    /// while preserving spaces and approximately preserving the real label
    /// length so the unknown label still has the same visual footprint.
    /// </summary>
    private string GenerateUnknownText(
        string realName)
    {
        int fallbackLength =
            Mathf.Max(
                1,
                unknownFallbackLength
            );


        if (string.IsNullOrEmpty(
                garbleCharacters))
        {
            return new string(
                '?',
                string.IsNullOrEmpty(realName)
                    ? fallbackLength
                    : realName.Length
            );
        }


        if (string.IsNullOrEmpty(
                realName))
        {
            StringBuilder fallback =
                new StringBuilder(
                    fallbackLength
                );

            for (int i = 0;
                 i < fallbackLength;
                 i++)
            {
                fallback.Append(
                    garbleCharacters[
                        Random.Range(
                            0,
                            garbleCharacters.Length
                        )
                    ]
                );
            }

            return fallback.ToString();
        }


        StringBuilder builder =
            new StringBuilder(
                realName.Length
            );


        foreach (char character in realName)
        {
            if (character == ' ')
            {
                builder.Append(' ');
                continue;
            }


            builder.Append(
                garbleCharacters[
                    Random.Range(
                        0,
                        garbleCharacters.Length
                    )
                ]
            );
        }


        return builder.ToString();
    }


    private string GetDisplayName(
        Planet planet)
    {
        return string.IsNullOrWhiteSpace(
            planet.displayName
        )
            ? planet.id
            : planet.displayName;
    }


    private PlanetLabel GetOrCreateLabel(
        string planetId)
    {
        string safeId =
            string.IsNullOrWhiteSpace(planetId)
                ? "(no-id)"
                : planetId;


        if (labelsByPlanetId.TryGetValue(
                safeId,
                out PlanetLabel existing))
        {
            return existing;
        }


        // Give every planet callout its own small runtime root. PlanetLabel
        // still creates/owns its TextMesh underneath this root, while the
        // manager adds the reticle and connector beside it.
        GameObject calloutRootObject =
            new GameObject(
                $"PlanetCallout_{safeId}"
            );

        calloutRootObject.transform.SetParent(
            transform,
            false
        );


        PlanetLabel label =
            PlanetLabel.Create(
                calloutRootObject.transform,
                labelFont,
                fontSize,
                labelScale,
                ResolveSortingLayerName(
                    sortingLayerName
                ),
                calloutSortingOrder + 2
            );


        Renderer labelRenderer =
            calloutRootObject.GetComponentInChildren<Renderer>(
                true
            );

        SpriteRenderer reticle =
            CreateCalloutRenderer(
                $"PlanetReticle_{safeId}",
                planetReticleSprite,
                calloutSortingOrder + 1
            );

        reticle.color =
            reticleColor;

        SpriteRenderer connector =
            CreateCalloutRenderer(
                $"PlanetConnector_{safeId}",
                connectorSprite,
                calloutSortingOrder
            );

        connector.color =
            connectorColor;


        labelsByPlanetId.Add(
            safeId,
            label
        );

        labelRenderersByPlanetId.Add(
            safeId,
            labelRenderer
        );

        reticlesByPlanetId.Add(
            safeId,
            reticle
        );

        connectorsByPlanetId.Add(
            safeId,
            connector
        );


        DebugState(
            $"CREATED PlanetLabel callout for \"{safeId}\"."
        );


        return label;
    }


    private void HideActiveLabel()
    {
        if (string.IsNullOrEmpty(
                activePlanetId))
        {
            return;
        }


        string safeId =
            string.IsNullOrWhiteSpace(activePlanetId)
                ? "(no-id)"
                : activePlanetId;


        if (labelsByPlanetId.TryGetValue(
                safeId,
                out PlanetLabel label))
        {
            label.SetActive(
                false
            );
        }

        if (reticlesByPlanetId.TryGetValue(
                safeId,
                out SpriteRenderer reticle))
        {
            reticle.gameObject.SetActive(
                false
            );
        }

        if (connectorsByPlanetId.TryGetValue(
                safeId,
                out SpriteRenderer connector))
        {
            connector.gameObject.SetActive(
                false
            );
        }


        activePlanetId =
            null;
    }


    /// <summary>
    /// Positions the label diagonally from the planet.
    ///
    /// The label-side end of the connector is treated as a FIXED anchor.
    /// The TextMesh is shifted so the appropriate corner of its rendered
    /// bounds always lands on that anchor. This means changing from a short
    /// garbled name to a longer identified name does not move the point
    /// where the connector meets the text - the text simply grows away
    /// from that fixed corner.
    /// </summary>
    private Vector3 LayoutCallout(
        Planet planet,
        PlanetLabel label)
    {
        string safeId =
            string.IsNullOrWhiteSpace(planet.id)
                ? "(no-id)"
                : planet.id;

        Vector3 planetWorld =
            planetManager.GetPlanetWorldPosition(
                planet
            );

        if (!labelRenderersByPlanetId.TryGetValue(
                safeId,
                out Renderer labelRenderer) ||
            labelRenderer == null ||
            !reticlesByPlanetId.TryGetValue(
                safeId,
                out SpriteRenderer reticle) ||
            !connectorsByPlanetId.TryGetValue(
                safeId,
                out SpriteRenderer connector))
        {
            label.SetWorldPosition(
                planetWorld
            );

            return planetWorld;
        }


        reticle.transform.position =
            SnapToCalloutPixelGrid(
                planetWorld
            );

        // The sprite pivot is centred, so uniform scaling keeps the
        // reticle centred on the planet tile while making it larger or
        // smaller. Connector placement uses reticle.bounds afterwards,
        // so it automatically follows the scaled size.
        float appliedReticleScale =
            Mathf.Max(
                0.05f,
                reticleScale
            );

        reticle.transform.localScale =
            new Vector3(
                appliedReticleScale,
                appliedReticleScale,
                1.0f
            );

        reticle.gameObject.SetActive(
            true
        );


        Vector2 preferred =
            GetPreferredScreenDirection(
                planetWorld
            );

        Vector2[] candidates =
        {
            preferred,
            new Vector2(-preferred.x, preferred.y),
            new Vector2(preferred.x, -preferred.y),
            new Vector2(-preferred.x, -preferred.y)
        };


        Vector3 bestLabelPosition =
            planetWorld;

        Vector3 bestAnchorPoint =
            planetWorld;

        Vector2 bestDirection =
            preferred;

        float bestPenalty =
            float.PositiveInfinity;

        float horizontalOffset =
            Mathf.Abs(
                labelDiagonalOffset.x
            );

        float verticalOffset =
            Mathf.Abs(
                labelDiagonalOffset.y
            );


        for (int i = 0;
             i < candidates.Length;
             i++)
        {
            Vector2 direction =
                candidates[i];

            // This is the fixed point where the connector meets the text.
            // It stays at the same diagonal distance from the planet no
            // matter how wide the current name becomes.
            Vector3 anchorPoint =
                SnapToCalloutPixelGrid(
                    planetWorld +
                    new Vector3(
                        direction.x *
                            horizontalOffset,
                        direction.y *
                            verticalOffset,
                        0.0f
                    )
                );


            // Start at the anchor, then shift the label so the correct
            // rendered corner lands exactly on it.
            label.SetWorldPosition(
                anchorPoint
            );

            Vector3 candidateLabelPosition =
                AlignLabelCornerToAnchor(
                    label,
                    labelRenderer,
                    anchorPoint,
                    direction
                );

            float penalty =
                CalculateScreenOverflowPenalty(
                    labelRenderer
                );

            if (penalty <
                bestPenalty)
            {
                bestPenalty =
                    penalty;

                bestLabelPosition =
                    candidateLabelPosition;

                bestAnchorPoint =
                    anchorPoint;

                bestDirection =
                    direction;
            }
        }


        label.SetWorldPosition(
            bestLabelPosition
        );

        // Re-align after restoring the chosen position so the renderer's
        // chosen connection corner is guaranteed to land on the exact
        // fixed anchor even after the trial placements above.
        bestLabelPosition =
            AlignLabelCornerToAnchor(
                label,
                labelRenderer,
                bestAnchorPoint,
                bestDirection
            );


        // If the name is so long that the chosen diagonal cannot fit, move
        // the WHOLE anchored callout inward as one unit. The text corner and
        // connector endpoint remain locked together.
        Vector3 anchorShift =
            GetScreenClampWorldShift(
                labelRenderer,
                bestLabelPosition
            );

        if (anchorShift.sqrMagnitude >
            0.000001f)
        {
            bestAnchorPoint +=
                anchorShift;

            bestLabelPosition +=
                anchorShift;

            label.SetWorldPosition(
                bestLabelPosition
            );
        }


        UpdateConnectorToFixedAnchor(
            reticle,
            connector,
            bestAnchorPoint
        );

        return bestLabelPosition;
    }


    /// <summary>
    /// Moves the label so the corner nearest the planet sits exactly on
    /// the supplied callout anchor.
    ///
    /// Upper-right label  -> lower-left text corner is anchored.
    /// Upper-left label   -> lower-right text corner is anchored.
    /// Lower-right label  -> upper-left text corner is anchored.
    /// Lower-left label   -> upper-right text corner is anchored.
    /// </summary>
    private Vector3 AlignLabelCornerToAnchor(
        PlanetLabel label,
        Renderer labelRenderer,
        Vector3 anchorPoint,
        Vector2 labelDirection)
    {
        Bounds bounds =
            labelRenderer.bounds;

        Vector3 connectionCorner =
            GetLabelConnectionCorner(
                bounds,
                labelDirection
            );

        Vector3 shift =
            anchorPoint -
            connectionCorner;

        Vector3 currentPosition =
            labelRenderer.transform.position;

        // PlanetLabel owns the actual TextMesh GameObject, so move through
        // PlanetLabel's public positioning API rather than assuming its
        // internal transform hierarchy.
        Vector3 newPosition =
            currentPosition +
            shift;

        label.SetWorldPosition(
            newPosition
        );

        return newPosition;
    }


    /// <summary>
    /// Returns the text-bounds corner facing back toward the planet.
    /// That is the corner kept permanently attached to the connector.
    /// </summary>
    private Vector3 GetLabelConnectionCorner(
        Bounds bounds,
        Vector2 labelDirection)
    {
        float x =
            labelDirection.x >= 0.0f
                ? bounds.min.x
                : bounds.max.x;

        float y =
            labelDirection.y >= 0.0f
                ? bounds.min.y
                : bounds.max.y;

        return new Vector3(
            x,
            y,
            bounds.center.z
        );
    }


    /// <summary>
    /// Calculates the world-space shift required to keep the rendered
    /// label inside the camera. The caller applies this same shift to the
    /// label and its fixed connector anchor, preserving their attachment.
    /// </summary>
    private Vector3 GetScreenClampWorldShift(
        Renderer labelRenderer,
        Vector3 currentWorldPosition)
    {
        Rect labelScreenBounds =
            GetRendererScreenBounds(
                labelRenderer
            );

        Rect cameraRect =
            mainCamera.pixelRect;

        float left =
            cameraRect.xMin +
            screenPaddingPixels;

        float right =
            cameraRect.xMax -
            screenPaddingPixels;

        float bottom =
            cameraRect.yMin +
            screenPaddingPixels;

        float top =
            cameraRect.yMax -
            screenPaddingPixels;


        float shiftX = 0.0f;
        float shiftY = 0.0f;

        if (labelScreenBounds.xMin <
            left)
        {
            shiftX =
                left -
                labelScreenBounds.xMin;
        }
        else if (labelScreenBounds.xMax >
                 right)
        {
            shiftX =
                right -
                labelScreenBounds.xMax;
        }

        if (labelScreenBounds.yMin <
            bottom)
        {
            shiftY =
                bottom -
                labelScreenBounds.yMin;
        }
        else if (labelScreenBounds.yMax >
                 top)
        {
            shiftY =
                top -
                labelScreenBounds.yMax;
        }


        if (Mathf.Abs(shiftX) <
                0.01f &&
            Mathf.Abs(shiftY) <
                0.01f)
        {
            return Vector3.zero;
        }


        Vector3 currentScreen =
            mainCamera.WorldToScreenPoint(
                currentWorldPosition
            );

        Vector3 shiftedScreen =
            currentScreen +
            new Vector3(
                shiftX,
                shiftY,
                0.0f
            );

        Vector3 shiftedWorld =
            mainCamera.ScreenToWorldPoint(
                shiftedScreen
            );

        shiftedWorld.z =
            currentWorldPosition.z;

        return shiftedWorld -
               currentWorldPosition;
    }


    /// <summary>
    /// Prefers the diagonal pointing toward the larger amount of visible
    /// screen space. The actual text bounds are still tested afterwards,
    /// so this is only the first-choice quadrant.
    /// </summary>
    private Vector2 GetPreferredScreenDirection(
        Vector3 worldPosition)
    {
        Vector3 screen =
            mainCamera.WorldToScreenPoint(
                worldPosition
            );

        float centreX =
            mainCamera.pixelRect.xMin +
            mainCamera.pixelRect.width *
            0.5f;

        float centreY =
            mainCamera.pixelRect.yMin +
            mainCamera.pixelRect.height *
            0.5f;

        float xDirection =
            screen.x < centreX
                ? 1.0f
                : -1.0f;

        float yDirection =
            screen.y < centreY
                ? 1.0f
                : -1.0f;

        return new Vector2(
            xDirection,
            yDirection
        );
    }


    /// <summary>
    /// Scores how far the label renderer falls outside the camera's pixel
    /// rectangle. Zero means the full rendered text fits inside the
    /// configured screen padding.
    /// </summary>
    private float CalculateScreenOverflowPenalty(
        Renderer labelRenderer)
    {
        Rect labelScreenBounds =
            GetRendererScreenBounds(
                labelRenderer
            );

        Rect cameraRect =
            mainCamera.pixelRect;

        float left =
            cameraRect.xMin +
            screenPaddingPixels;

        float right =
            cameraRect.xMax -
            screenPaddingPixels;

        float bottom =
            cameraRect.yMin +
            screenPaddingPixels;

        float top =
            cameraRect.yMax -
            screenPaddingPixels;


        float overflow = 0.0f;

        if (labelScreenBounds.xMin <
            left)
        {
            overflow +=
                left -
                labelScreenBounds.xMin;
        }

        if (labelScreenBounds.xMax >
            right)
        {
            overflow +=
                labelScreenBounds.xMax -
                right;
        }

        if (labelScreenBounds.yMin <
            bottom)
        {
            overflow +=
                bottom -
                labelScreenBounds.yMin;
        }

        if (labelScreenBounds.yMax >
            top)
        {
            overflow +=
                labelScreenBounds.yMax -
                top;
        }

        return overflow;
    }


    /// <summary>
    /// Converts the eight corners of a renderer's world bounds into a
    /// screen-space rectangle.
    /// </summary>
    private Rect GetRendererScreenBounds(
        Renderer targetRenderer)
    {
        Bounds bounds =
            targetRenderer.bounds;

        Vector3 min =
            bounds.min;

        Vector3 max =
            bounds.max;

        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z)
        };


        Vector3 first =
            mainCamera.WorldToScreenPoint(
                corners[0]
            );

        float minX = first.x;
        float maxX = first.x;
        float minY = first.y;
        float maxY = first.y;


        for (int i = 1;
             i < corners.Length;
             i++)
        {
            Vector3 screen =
                mainCamera.WorldToScreenPoint(
                    corners[i]
                );

            minX =
                Mathf.Min(
                    minX,
                    screen.x
                );

            maxX =
                Mathf.Max(
                    maxX,
                    screen.x
                );

            minY =
                Mathf.Min(
                    minY,
                    screen.y
                );

            maxY =
                Mathf.Max(
                    maxY,
                    screen.y
                );
        }


        return Rect.MinMaxRect(
            minX,
            minY,
            maxX,
            maxY
        );
    }


    /// <summary>
    /// Draws the connector from the nearest reticle corner to the already
    /// fixed text anchor. The text end is no longer derived from the current
    /// text bounds, so changing name width cannot move the connection point.
    /// </summary>
    private void UpdateConnectorToFixedAnchor(
        SpriteRenderer reticle,
        SpriteRenderer connector,
        Vector3 labelAnchor)
    {
        Vector3[] reticleCorners =
            GetBoundsCorners2D(
                reticle.bounds
            );

        Vector3 bestReticleCorner =
            reticleCorners[0];

        float bestDistance =
            float.PositiveInfinity;


        for (int i = 0;
             i < reticleCorners.Length;
             i++)
        {
            float distance =
                (
                    labelAnchor -
                    reticleCorners[i]
                ).sqrMagnitude;

            if (distance <
                bestDistance)
            {
                bestDistance =
                    distance;

                bestReticleCorner =
                    reticleCorners[i];
            }
        }


        DrawConnector(
            connector,
            bestReticleCorner,
            labelAnchor
        );
    }


    private Vector3[] GetBoundsCorners2D(
        Bounds bounds)
    {
        Vector3 min =
            bounds.min;

        Vector3 max =
            bounds.max;

        float z =
            bounds.center.z;

        return new[]
        {
            new Vector3(min.x, min.y, z),
            new Vector3(min.x, max.y, z),
            new Vector3(max.x, min.y, z),
            new Vector3(max.x, max.y, z)
        };
    }


    private void DrawConnector(
        SpriteRenderer connector,
        Vector3 start,
        Vector3 end)
    {
        start =
            SnapToCalloutPixelGrid(
                start
            );

        end =
            SnapToCalloutPixelGrid(
                end
            );

        Vector3 delta =
            end - start;

        float length =
            delta.magnitude;

        if (length <
            0.0001f)
        {
            connector.gameObject.SetActive(
                false
            );

            return;
        }


        connector.transform.position =
            (start + end) *
            0.5f;

        float angle =
            Mathf.Atan2(
                delta.y,
                delta.x
            ) *
            Mathf.Rad2Deg;

        connector.transform.rotation =
            Quaternion.Euler(
                0.0f,
                0.0f,
                angle
            );

        float thickness =
            Mathf.Max(
                1,
                connectorThicknessPixels
            ) /
            (float)Mathf.Max(
                1,
                calloutPixelsPerUnit
            );

        connector.transform.localScale =
            new Vector3(
                length,
                thickness,
                1.0f
            );

        connector.color =
            connectorColor;

        connector.gameObject.SetActive(
            true
        );
    }


    private SpriteRenderer CreateCalloutRenderer(
        string objectName,
        Sprite sprite,
        int order)
    {
        GameObject calloutObject =
            new GameObject(
                objectName
            );

        calloutObject.transform.SetParent(
            transform,
            false
        );

        SpriteRenderer renderer =
            calloutObject.AddComponent<SpriteRenderer>();

        renderer.sprite =
            sprite;

        renderer.sortingLayerName =
            ResolveSortingLayerName(
                sortingLayerName
            );

        renderer.sortingOrder =
            order;

        renderer.gameObject.SetActive(
            false
        );

        return renderer;
    }


    /// <summary>
    /// Creates an 8x8 pixel targeting frame for a planet tile. It uses
    /// broken corner brackets plus small centre-edge ticks so it reads
    /// differently from the route-segment checkpoint reticle while keeping
    /// the same tiny pixel-art language.
    /// </summary>
    private Sprite CreatePlanetReticleSprite()
    {
        const int size = 8;

        Texture2D texture =
            new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false
            );

        texture.filterMode =
            FilterMode.Point;

        texture.wrapMode =
            TextureWrapMode.Clamp;


        Color clear =
            new Color(
                0.0f,
                0.0f,
                0.0f,
                0.0f
            );

        Color[] pixels =
            new Color[
                size *
                size
            ];

        for (int i = 0;
             i < pixels.Length;
             i++)
        {
            pixels[i] =
                clear;
        }

        texture.SetPixels(
            pixels
        );


        // Two-pixel broken corner brackets.
        SetCalloutPixel(texture, 0, 0);
        SetCalloutPixel(texture, 1, 0);
        SetCalloutPixel(texture, 0, 1);

        SetCalloutPixel(texture, 6, 0);
        SetCalloutPixel(texture, 7, 0);
        SetCalloutPixel(texture, 7, 1);

        SetCalloutPixel(texture, 0, 6);
        SetCalloutPixel(texture, 0, 7);
        SetCalloutPixel(texture, 1, 7);

        SetCalloutPixel(texture, 7, 6);
        SetCalloutPixel(texture, 6, 7);
        SetCalloutPixel(texture, 7, 7);

        // Tiny edge ticks distinguish this from the route checkpoint style.
        SetCalloutPixel(texture, 3, 0);
        SetCalloutPixel(texture, 4, 7);
        SetCalloutPixel(texture, 0, 4);
        SetCalloutPixel(texture, 7, 3);


        texture.Apply();


        return Sprite.Create(
            texture,
            new Rect(
                0,
                0,
                size,
                size
            ),
            new Vector2(
                0.5f,
                0.5f
            ),
            Mathf.Max(
                1,
                calloutPixelsPerUnit
            )
        );
    }


    private void SetCalloutPixel(
        Texture2D texture,
        int x,
        int y)
    {
        texture.SetPixel(
            x,
            y,
            Color.white
        );
    }


    private Sprite CreateConnectorSprite()
    {
        Texture2D texture =
            new Texture2D(
                1,
                1
            );

        texture.filterMode =
            FilterMode.Point;

        texture.wrapMode =
            TextureWrapMode.Clamp;

        texture.SetPixel(
            0,
            0,
            Color.white
        );

        texture.Apply();


        // One world unit at scale 1. DrawConnector controls the final
        // world-space length/thickness through transform scale.
        return Sprite.Create(
            texture,
            new Rect(
                0,
                0,
                1,
                1
            ),
            new Vector2(
                0.5f,
                0.5f
            ),
            1.0f
        );
    }


    private Vector3 SnapToCalloutPixelGrid(
        Vector3 worldPosition)
    {
        float unitSize =
            1.0f /
            Mathf.Max(
                1,
                calloutPixelsPerUnit
            );

        return new Vector3(
            Mathf.Round(
                worldPosition.x /
                unitSize
            ) *
            unitSize,
            Mathf.Round(
                worldPosition.y /
                unitSize
            ) *
            unitSize,
            worldPosition.z
        );
    }


    private void DebugState(
        string message)
    {
        if (!debugHoverLabels)
        {
            return;
        }

        if (lastDebugState ==
            message)
        {
            return;
        }


        lastDebugState =
            message;

        Debug.Log(
            $"{name}: {message}",
            this
        );
    }


    private string GetMissingReferenceDescription()
    {
        List<string> missing =
            new List<string>();

        if (planetManager == null)
        {
            missing.Add(
                "PlanetManager"
            );
        }

        if (visionManager == null)
        {
            missing.Add(
                "VisionManager"
            );
        }

        if (mainCamera == null)
        {
            missing.Add(
                "Camera.main - give the gameplay camera the MainCamera tag"
            );
        }

        if (planetGrid == null)
        {
            missing.Add(
                "Grid - PlanetManager must be underneath the map Grid"
            );
        }


        return missing.Count == 0
            ? "unknown reference problem"
            : "missing " +
              string.Join(
                  ", ",
                  missing
              );
    }


    private string ResolveSortingLayerName(
        string requestedName)
    {
        foreach (SortingLayer layer in SortingLayer.layers)
        {
            if (layer.name ==
                requestedName)
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
