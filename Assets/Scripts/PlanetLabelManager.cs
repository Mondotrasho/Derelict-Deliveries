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

    [SerializeField]
    private Vector3 labelOffset =
        new Vector3(0.0f, 0.6f, 0.0f);

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

    private Grid planetGrid;

    private string activePlanetId;
    private string lastDebugState;

    private float garbleTimer;


    private void Awake()
    {
        ResolveReferences();
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


        Vector3 labelWorldPosition =
            planetManager.GetPlanetWorldPosition(
                planet
            ) +
            labelOffset;

        label.SetWorldPosition(
            labelWorldPosition
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


        PlanetLabel label =
            PlanetLabel.Create(
                transform,
                labelFont,
                fontSize,
                labelScale,
                ResolveSortingLayerName(
                    sortingLayerName
                ),
                sortingOrder
            );


        labelsByPlanetId.Add(
            safeId,
            label
        );


        DebugState(
            $"CREATED PlanetLabel object for \"{safeId}\"."
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


        activePlanetId =
            null;
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
