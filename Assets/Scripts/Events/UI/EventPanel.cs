using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The event UI. Presents any triggered event - asteroid and derelict marks,
/// hazards (random events) and planet event options - in a BannerChoiceView:
/// banner, name, description and the definition's authored choices.
///
/// A choice rolls one of its weighted outcomes, which is applied straight away
/// (EventChoiceResolver) and shown as result text. Then, depending on the
/// choice: the dialogue UI opens, a follow-up event is shown in the same
/// window, the event ends, or the choices come back re-checked against the
/// new state. Definitions with a root dialogue still show their event card first,
/// then open the conversation from one explicit interaction button. Definitions
/// with no choices get placeholder Resolve / Leave.
///
/// Presentation plus choice outcomes only: the controllers still own movement
/// interruption, the definition's On Resolve writes, site removal and route
/// resumption.
/// </summary>
public sealed class EventPanel : MonoBehaviour, IEventPresenter
{
    private const string ResolveId = "__resolve";
    private const string LeaveId = "__leave";
    private const string ContinueId = "__continue";
    private const string DialogueId = "__dialogue";

    [Header("References")]
    [Tooltip("The window this panel drives. Empty = a BannerChoiceView on this object or its children.")]
    [SerializeField] private BannerChoiceView view;
    [Tooltip("Picks the banner. Empty = no banner (the banner area collapses).")]
    [SerializeField] private BannerLibrary banners;

    [Header("Outcome Targets (found automatically if empty)")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private AsteroidFieldPainter asteroidField;
    [Tooltip("Opened by choice dialogues and root event dialogues. Assign it: the panel starts inactive, so it cannot always be found.")]
    [SerializeField] private DialoguePanelController dialoguePanel;
    [Tooltip("Used by outcomes with Start Combat With.")]
    [SerializeField] private EnemySpawnController enemySpawner;
    [SerializeField] private EnemyTurnController enemyTurnController;
    [Tooltip("Used by outcomes with Schedule Event (quests).")]
    [SerializeField] private QuestScheduler questScheduler;
    [Tooltip("Officers aboard (N4): bonus odds, requirements, recruit / lose.")]
    [SerializeField] private OfficerRoster roster;

    [Header("Header Text")]
    [SerializeField] private string titlePrefix = "SYS://";
    [SerializeField] private bool upperCaseTitle = true;
    [Tooltip("Show the planet's name as the status for events on a planet.")]
    [SerializeField] private bool planetNameAsStatus = true;
    [SerializeField] private string asteroidStatus = "SIGNAL";
    [SerializeField] private string derelictStatus = "WRECK SIGNAL";
    [SerializeField] private string planetStatus = "LOCAL SIGNAL";
    [SerializeField] private string hazardStatus = "WARNING";

    [Header("Button Labels")]
    [SerializeField] private string resolveLabel = "RESOLVE";
    [SerializeField] private string leaveLabel = "LEAVE FOR LATER";
    [SerializeField] private string continueLabel = "CONTINUE";

    public bool IsOpen => current != null;

    private Sprite shownBanner;   // banner currently in the window (for CRT follow-up swaps)
    private readonly List<BannerChoiceView.Choice> buttons = new List<BannerChoiceView.Choice>();
    private readonly HashSet<string> usedChoices = new HashSet<string>();
    private readonly System.Random rng = new System.Random();
    private EventSite current;
    private bool cancelled;


    private void Awake()
    {
        if (view == null) view = GetComponentInChildren<BannerChoiceView>(true);
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();
        if (dialoguePanel == null) dialoguePanel = FindFirstObjectByType<DialoguePanelController>(FindObjectsInactive.Include);
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
        if (enemyTurnController == null) enemyTurnController = FindFirstObjectByType<EnemyTurnController>();
        if (questScheduler == null) questScheduler = FindFirstObjectByType<QuestScheduler>();
        if (roster == null) roster = FindFirstObjectByType<OfficerRoster>();
        if (view == null) Debug.LogWarning("EventPanel has no BannerChoiceView; events will be left for later.", this);
    }


    public IEnumerator Present(EventSite site, EventContext context, Action<EventOutcome> done)
    {
        if (view == null || site == null || site.Definition == null)
        {
            done?.Invoke(EventOutcome.LeaveForLater);
            yield break;
        }

        current = site;
        cancelled = false;

        bool hazard = site.Category == EventCategory.Hazard;
        Planet planet = context.PlanetData != null ? context.PlanetData : site.Planet;
        EventDefinition shown = site.Definition;
        bool somethingHappened = false;   // an outcome was applied: leaving now still resolves the site
        bool resolved = false;
        EnemyShipDefinition combatAfter = null;
        EnemyShipDefinition spawnAtEdgeAfterClose = null;

        ShowDefinition(shown, site, planet, hazard);

        while (true)
        {
            // Root dialogue events still begin as normal event cards. The player explicitly
            // enters the conversation from the banner window, then the dialogue owns the detailed
            // decisions. JSON options reference EventChoice ids through their "action" field, so
            // gameplay effects and conditions remain in the EventDefinition rather than narrative text.
            if (shown.DialogueJson != null && dialoguePanel != null)
            {
                bool canLeaveDialogueEvent = !hazard && shown.AllowLeave;

                buttons.Clear();
                buttons.Add(new BannerChoiceView.Choice(DialogueId, shown.DialogueButtonLabel));
                if (canLeaveDialogueEvent) buttons.Add(new BannerChoiceView.Choice(LeaveId, leaveLabel));
                view.SetChoices(buttons);

                string dialoguePick = null;
                yield return view.WaitForChoice(id => dialoguePick = id);
                if (cancelled || dialoguePick == null) break;

                if (dialoguePick == LeaveId)
                {
                    resolved = somethingHappened;
                    break;
                }

                if (dialoguePick != DialogueId) continue;

                bool dialogueResolved = false;
                EventDefinition followUpAfterDialogue = null;

                dialoguePanel.SetChoiceActionResolver(
                    actionId =>
                    {
                        if (dialogueResolved) return new DialogueActionAvailability(false, false);

                        EventChoice actionChoice = shown.FindChoice(actionId);
                        if (actionChoice == null) return new DialogueActionAvailability(false, false);

                        bool available = IsChoiceAvailable(actionChoice, context, out string reason);
                        return new DialogueActionAvailability(
                            available || actionChoice.showWhenLocked,
                            available,
                            reason);
                    },
                    actionId =>
                    {
                        if (dialogueResolved) return new DialogueActionResult(false);

                        EventChoice actionChoice = shown.FindChoice(actionId);
                        if (actionChoice == null || !IsChoiceAvailable(actionChoice, context, out _))
                        {
                            return new DialogueActionResult(false);
                        }

                        ChoiceResolution action = ResolveChoice(actionChoice, site, context);
                        somethingHappened = true;

                        if (actionChoice.hideAfterUse) usedChoices.Add(actionChoice.id);
                        if (action.outcome != null && action.outcome.startCombatWith != null)
                            combatAfter = action.outcome.startCombatWith;
                        if (action.outcome != null && action.outcome.spawnOnMapEdgeAfterClose != null)
                            spawnAtEdgeAfterClose = action.outcome.spawnOnMapEdgeAfterClose;
                        if (action.outcome != null && action.outcome.followUp != null)
                            followUpAfterDialogue = action.outcome.followUp;
                        if (actionChoice.endsEvent) dialogueResolved = true;

                        return new DialogueActionResult(true, action.outcomeIndex);
                    });

                // The event card is deliberately visible until the player chooses to interact.
                // Once dialogue starts, hide it so the two UIs never overlap or flash through.
                view.Hide();
                yield return dialoguePanel.OpenDialogueAndWait(shown.DialogueJson);
                dialoguePanel.ClearChoiceActionResolver();
                if (cancelled) break;

                if (followUpAfterDialogue != null)
                {
                    shown = followUpAfterDialogue;
                    usedChoices.Clear();
                    ShowDefinition(shown, site, planet, hazard, crtBannerSwap: true);
                    continue;
                }

                if (combatAfter != null)
                {
                    resolved = true;
                    break;
                }

                resolved = dialogueResolved;
                break;
            }

            bool canLeave = !hazard && shown.AllowLeave;
            bool hasChoices = shown.Choices != null && shown.Choices.Count > 0;

            buttons.Clear();
            if (hasChoices) AddChoiceButtons(shown, context);
            else buttons.Add(new BannerChoiceView.Choice(ResolveId, resolveLabel));
            if (canLeave || buttons.Count == 0) buttons.Add(new BannerChoiceView.Choice(LeaveId, leaveLabel));
            view.SetChoices(buttons);

            string picked = null;
            yield return view.WaitForChoice(id => picked = id);
            if (cancelled || picked == null) break;

            if (picked == LeaveId)
            {
                resolved = somethingHappened;   // e.g. walked away from a capsule already cut out of the rock
                break;
            }

            if (picked == ResolveId)
            {
                resolved = true;
                break;
            }

            EventChoice choice = shown.FindChoice(picked);
            if (choice == null) continue;

            // Roll and apply one outcome now, so later choices see its writes.
            ChoiceResolution resolution = ResolveChoice(choice, site, context);
            ChoiceOutcome outcome = resolution.outcome;
            string summary = resolution.summary;
            somethingHappened = true;

            if (outcome != null && outcome.spawnOnMapEdgeAfterClose != null)
                spawnAtEdgeAfterClose = outcome.spawnOnMapEdgeAfterClose;

            if (outcome != null && outcome.showBannerOf != null && banners != null)
            {
                shownBanner = banners.Resolve(outcome.showBannerOf, planet);
                view.SetBanner(shownBanner);
            }

            if (!choice.skipResultStep)
            {
                view.ShowResult(JoinResult(outcome != null ? outcome.resultText : "", summary));
                view.SetChoices(new[] { new BannerChoiceView.Choice(ContinueId, continueLabel) });
                yield return view.WaitForChoice(id => picked = id);
                if (cancelled || picked == null) break;
            }

            if (choice.dialogue != null && dialoguePanel != null)
            {
                view.Hide();
                yield return dialoguePanel.OpenDialogueAndWait(choice.dialogue);
                if (cancelled) break;
                view.ShowAgain();
            }

            if (outcome != null && outcome.startCombatWith != null)
            {
                combatAfter = outcome.startCombatWith;
                resolved = true;
                break;
            }

            if (outcome != null && outcome.followUp != null)
            {
                shown = outcome.followUp;
                usedChoices.Clear();
                ShowDefinition(shown, site, planet, hazard, crtBannerSwap: true);
                continue;
            }

            if (choice.endsEvent)
            {
                resolved = true;
                break;
            }

            if (choice.hideAfterUse) usedChoices.Add(choice.id);
            view.ShowResult(null);
        }

        view.Hide();
        current = null;
        usedChoices.Clear();

        // Some reveals deliberately stage the map contact only once the banner is gone.
        // Waiting one frame makes the transition visually unambiguous: close event -> map -> contact appears.
        if (spawnAtEdgeAfterClose != null && !cancelled)
        {
            yield return null;
            SpawnAtMapEdge(spawnAtEdgeAfterClose);
        }

        done?.Invoke(resolved && !cancelled
            ? new EventOutcome { resolved = true, stopJourney = false }
            : EventOutcome.LeaveForLater);

        // Started here, straight after reporting, so the controller that ran this
        // event already sees the Combat phase (a planet visit then ends instead of
        // reopening its list).
        if (combatAfter != null && !cancelled) StartCombat(combatAfter);
    }


    private void StartCombat(EnemyShipDefinition definition)
    {
        if (enemySpawner == null || enemyTurnController == null || player == null)
        {
            Debug.LogWarning("EventPanel: Start Combat With needs EnemySpawnController, EnemyTurnController and PlayerShipState.", this);
            return;
        }

        Vector3Int centre = player.CurrentCell;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector3Int cell = new Vector3Int(centre.x + dx, centre.y + dy, centre.z);
                if (!enemySpawner.TrySpawnEnemyAtCell(cell, out EnemyShip enemy)) continue;

                enemy.SetDefinition(definition);
                if (!enemyTurnController.TryStartEncounter(enemy))
                {
                    Debug.LogWarning($"EventPanel: could not start combat with {definition.DisplayName}.", this);
                }
                return;
            }
        }

        Debug.LogWarning("EventPanel: no free cell (or enemy cap reached) to spawn the combat opponent.", this);
    }


    /// <summary>Close without resolving (e.g. combat started). Present then finishes with LeaveForLater.</summary>
    public void Cancel()
    {
        if (current == null) return;
        cancelled = true;
        if (view != null) view.Hide();
    }


    /// <summary>
    /// Fills the window with a definition. With crtBannerSwap (follow-ups in the
    /// same window), text updates at once but a changed banner plays the
    /// BannerChoiceView CRT redraw instead of cutting - e.g. ramp lowering ->
    /// cultists on the gangplank, detection -> eldritch contact, -> airlock.
    /// Same sprite (or no banner either side) just cuts, as before.
    /// </summary>
    private void ShowDefinition(EventDefinition definition, EventSite site, Planet planet, bool hazard,
                                bool crtBannerSwap = false)
    {
        Sprite next = banners != null ? banners.Resolve(definition, planet) : null;
        bool swap = crtBannerSwap && shownBanner != null && next != null && next != shownBanner;

        view.Show(
            swap ? shownBanner : next,      // keep the old image up; SetBanner redraws it
            Title(definition),
            Status(definition, planet),
            definition.BodyText,
            hazard);

        if (swap) view.SetBanner(next);
        shownBanner = next;
    }


    private sealed class ChoiceResolution
    {
        public int outcomeIndex = -1;
        public ChoiceOutcome outcome;
        public string summary = "";
    }


    private bool IsChoiceAvailable(EventChoice choice, EventContext context, out string reason)
    {
        reason = "";
        if (choice == null || string.IsNullOrEmpty(choice.id) || usedChoices.Contains(choice.id)) return false;

        bool crewOk = choice.minCrew <= 0 ||
            (player != null && player.Resources != null && player.Resources.Crew >= choice.minCrew);
        bool hasStat = !string.IsNullOrWhiteSpace(choice.bonusStat);
        bool bonusOk = !hasStat || choice.minBonus <= 0 ||
            EventChoiceResolver.BonusPoints(choice, context.Player) >= choice.minBonus;
        bool conditionsOk = choice.availability == null || choice.availability.IsMet(context);

        if (crewOk && bonusOk && conditionsOk) return true;

        reason = choice.lockedReason;
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (!crewOk) reason = $"needs {choice.minCrew} crew";
            else if (!bonusOk) reason = $"needs {Title(choice.bonusStat)} {choice.minBonus}";
        }

        return false;
    }


    private ChoiceResolution ResolveChoice(EventChoice choice, EventSite site, EventContext context)
    {
        ChoiceResolution result = new ChoiceResolution();
        if (choice == null) return result;

        result.outcomeIndex = EventChoiceResolver.RollOutcome(choice, rng, context.Player);
        result.outcome = result.outcomeIndex >= 0 && choice.outcomes != null && result.outcomeIndex < choice.outcomes.Count
            ? choice.outcomes[result.outcomeIndex]
            : null;

        ChoiceOutcome outcome = result.outcome;
        result.summary = EventChoiceResolver.Apply(outcome, site, context, player, asteroidField);
        if (outcome != null)
            result.summary = EventChoiceResolver.Join(result.summary, EventChoiceResolver.ApplyDetection(outcome.detectionTurns, enemySpawner));
        if (outcome != null)
            result.summary = EventChoiceResolver.Join(result.summary, EventChoiceResolver.ApplyOfficers(outcome, roster));

        if (outcome != null && !string.IsNullOrWhiteSpace(outcome.returnCrewFromSiteCounter) && context.Site != null)
        {
            int back = Mathf.Max(0, context.Site.GetCounter(outcome.returnCrewFromSiteCounter));
            context.Site.SetCounter(outcome.returnCrewFromSiteCounter, 0);
            if (back > 0 && player != null && player.Resources != null) player.Resources.AddCrew(back);
            result.summary = EventChoiceResolver.Join(
                result.summary,
                $"CREW +{back} BACK ABOARD ({(player != null && player.Resources != null ? player.Resources.Crew : 0)})");
        }

        if (outcome != null && outcome.scheduleEvent != null)
        {
            if (questScheduler != null) questScheduler.Schedule(outcome.scheduleEvent, outcome.scheduleInTurns);
            else Debug.LogWarning($"EventPanel: no QuestScheduler in the scene for {outcome.scheduleEvent.Id}.", this);
        }

        if (outcome != null && outcome.delayedWrites != null && !outcome.delayedWrites.IsEmpty)
        {
            if (questScheduler != null) questScheduler.ScheduleWrites(outcome.delayedWrites, outcome.delayedWritesInTurns);
            else Debug.LogWarning("EventPanel: no QuestScheduler in the scene for delayed quest state.", this);
        }

        if (outcome != null && outcome.spawnOnMapEdge != null)
        {
            if (outcome.spawnDelayTurns > 0 && enemySpawner != null)
                enemySpawner.ScheduleMapEdgeSpawn(outcome.spawnOnMapEdge, outcome.spawnDelayTurns);
            else
                SpawnAtMapEdge(outcome.spawnOnMapEdge);
        }

        return result;
    }


    private void SpawnAtMapEdge(EnemyShipDefinition definition)
    {
        if (definition == null) return;

        if (enemySpawner == null || !enemySpawner.TrySpawnAtMapEdge(definition, out _))
        {
            Debug.LogWarning($"EventPanel: could not spawn {definition.DisplayName} at the map edge (no free entry cell?).", this);
        }
    }


    private void AddChoiceButtons(EventDefinition definition, EventContext context)
    {
        foreach (EventChoice choice in definition.Choices)
        {
            if (choice == null || string.IsNullOrEmpty(choice.id)) continue;
            if (usedChoices.Contains(choice.id)) continue;

            bool met = IsChoiceAvailable(choice, context, out string reason);
            if (!met && !choice.showWhenLocked) continue;

            bool hasStat = !string.IsNullOrWhiteSpace(choice.bonusStat);
            string statName = hasStat ? Title(choice.bonusStat) : "";
            string label = (hasStat ? $"[{statName}] " : "") + choice.text;
            if (met && choice.showOdds && choice.outcomes != null && choice.outcomes.Count > 1)
            {
                int percent = Mathf.RoundToInt(EventChoiceResolver.EffectiveFirstChance(choice, context.Player) * 100f);
                label += $" ({percent}%)";
            }
            if (!met && !string.IsNullOrWhiteSpace(reason)) label += $" [{reason}]";

            buttons.Add(new BannerChoiceView.Choice(choice.id, label, met));
        }
    }


    private static string Title(string stat)
    {
        stat = stat.Trim();
        return stat.Length == 0 ? stat : char.ToUpperInvariant(stat[0]) + stat.Substring(1).ToLowerInvariant();
    }


    private static string JoinResult(string text, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return text;
        if (string.IsNullOrWhiteSpace(text)) return summary;
        return text + "\n" + summary;
    }


    private string Title(EventDefinition definition)
    {
        string name = definition.DisplayName;
        if (upperCaseTitle) name = name.ToUpperInvariant();
        return titlePrefix + name;
    }


    private string Status(EventDefinition definition, Planet planet)
    {
        if (planetNameAsStatus && planet != null && !string.IsNullOrWhiteSpace(planet.displayName))
        {
            return planet.displayName.ToUpperInvariant();
        }

        switch (definition.Category)
        {
            case EventCategory.Hazard: return hazardStatus;
            case EventCategory.Derelict: return derelictStatus;
            case EventCategory.Planet: return planetStatus;
            default: return asteroidStatus;
        }
    }
}
