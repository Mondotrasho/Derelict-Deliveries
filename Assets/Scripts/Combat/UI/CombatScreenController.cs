using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum CombatSide
{
    Player,
    Enemy
}


/// <summary>
/// The combat screen (U2). Listens to CombatEncounterController and shows the
/// fight in the framed combat panel: dialogue-style action buttons with odds,
/// a terminal stats/log panel, both ships with shields and damage lines
/// (their Indicator children), sprite bars, the AI face and a radar sweep.
/// Every screen area (under its Frame) gets the same CRT scanlines as the
/// dialogue UI, and the combat view clips anything that leaves it (fleeing).
///
/// The controller resolves a whole round (and sometimes ends the fight) the
/// instant an action is submitted, so this screen snapshots the fight when it
/// starts and then PLAYS BACK each round in initiative order: the acting ship
/// moves, the hit or miss lands, shields flicker or pop, bars drain, ships
/// shake, explode or flee. It holds its own movement interruption until the
/// player presses Continue at the end.
///
/// Every step raises an event (ActionShown, HitShown, ShieldsPopped, ...) so
/// small extra animations and hologram callouts can hook in later without
/// changing the playback.
///
/// Scene layout (children found by name under Window if not assigned):
///   Window/StatsPanel/Content, Window/ButtonBar/Buttons,
///   Window/CombatView/PlayerShip, Window/CombatView/EnemyShip,
///   Window/FacePanel/Face, Window/RadarPanel/Overlay, and any SpriteBar
///   anywhere under Window. CombatView/Divider is decoration only.
/// </summary>
public sealed class CombatScreenController : MonoBehaviour
{
    [Header("Core")]
    [SerializeField] private CombatEncounterController combat;
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TerminalStyle style;

    [Header("Layout (found by name under Window if empty)")]
    [Tooltip("Everything visible. Shown during a fight, hidden otherwise.")]
    [SerializeField] private GameObject window;
    [SerializeField] private RectTransform statsArea;
    [SerializeField] private RectTransform buttonArea;
    [SerializeField] private CombatShipView playerShip;
    [SerializeField] private CombatShipView enemyShip;
    [SerializeField] private CombatFace face;
    [SerializeField] private RectTransform radarOverlay;

    [Header("Screens")]
    [Tooltip("Panels that get CRT scanlines under their Frame (same look as the dialogue UI, from Terminal Style).")]
    [SerializeField] private List<string> crtPanels = new List<string> { "StatsPanel", "CombatView", "RadarPanel", "FacePanel" };
    [Range(0f, 0.5f)] [SerializeField] private float crtFlicker = 0.08f;
    [Tooltip("Clip ships to the combat view, so a fleeing ship disappears under the frame instead of poking out.")]
    [SerializeField] private bool clipCombatView = true;

    [Header("Text")]
    [Min(1f)] [SerializeField] private float buttonFontSize = 26f;
    [Min(1f)] [SerializeField] private float statsFontSize = 18f;
    [Min(1)] [SerializeField] private int logLines = 9;
    [SerializeField] private string offensiveLabel = "OFFENSIVE";
    [SerializeField] private string defensiveLabel = "DEFENSIVE";
    [SerializeField] private string evasiveLabel = "EVASIVE";
    [SerializeField] private string fleeLabel = "FLEE";
    [SerializeField] private string continueLabel = "CONTINUE";

    [Header("Feel")]
    [Tooltip("Pause between steps of a round (seconds).")]
    [Min(0f)] [SerializeField] private float stepPause = 0.2f;
    [Range(0f, 1f)] [SerializeField] private float lowHullFraction = 0.3f;
    [Tooltip("Damage lines (ship Indicators) flash this colour when their ship is hit.")]
    [SerializeField] private Color damageFlashColour = new Color(1f, 0.3f, 0.3f, 1f);
    [Min(0f)] [SerializeField] private float damageFlashSeconds = 0.25f;
    [SerializeField] private float radarDegreesPerSecond = 90f;
    [Tooltip("Width of the radar sweep bar in pixels.")]
    [Min(1f)] [SerializeField] private float radarSweepWidth = 10f;

    // Hooks for extra animation / hologram callouts. Nothing depends on them.
    public event Action<CombatSide, CombatAction> ActionShown;
    public event Action<CombatSide, float, float> HitShown;          // target, hull damage, shield damage
    public event Action<CombatSide> Missed;                           // side that was missed
    public event Action<CombatSide> ShieldsPopped;
    public event Action<CombatSide> Destroyed;
    public event Action<bool> FleeShown;                              // success
    public event Action<CombatEncounterOutcome> EncounterShownEnded;

    public bool IsOpen { get; private set; }

    private struct Snapshot
    {
        public string enemyName;
        public float playerHull, playerMaxHull, playerShields, playerMaxShields;
        public float enemyHull, enemyMaxHull, enemyShields, enemyMaxShields;
    }

    private sealed class QueueItem
    {
        public CombatRoundResult round;
        public bool isEnd;
        public CombatEncounterOutcome outcome;
    }

    private readonly Queue<QueueItem> queue = new Queue<QueueItem>();
    private readonly List<string> log = new List<string>();
    private readonly List<SpriteBar> bars = new List<SpriteBar>();
    private readonly List<RawImage> crtImages = new List<RawImage>();
    private Texture2D crtTexture;
    private float crtTimer;
    private readonly Button[] actionButtons = new Button[4];
    private readonly TextMeshProUGUI[] actionLabels = new TextMeshProUGUI[4];
    private static readonly CombatAction[] Actions = { CombatAction.Fire, CombatAction.Defend, CombatAction.Evade, CombatAction.Flee };

    private Snapshot snap;
    private TextMeshProUGUI statsText;
    private Button continueButton;
    private RectTransform radarSweep;
    private Image radarBlip;
    private float radarBlipTimer;
    private MovementInterruptionHandle hold;
    private bool playing;
    private bool waitingForContinue;
    private bool built;


    // ================================================================ lifecycle

    private void Awake()
    {
        if (combat == null) combat = FindFirstObjectByType<CombatEncounterController>();
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (window == null)
        {
            Transform w = transform.Find("Window");
            window = w != null ? w.gameObject : gameObject;
        }

        Build();
        if (window != gameObject) window.SetActive(false);
    }


    private void OnEnable()
    {
        if (combat == null) return;
        combat.EncounterStarted += HandleStarted;
        combat.RoundResolved += HandleRound;
        combat.EncounterEnded += HandleEnded;
    }


    private void OnDisable()
    {
        if (combat == null) return;
        combat.EncounterStarted -= HandleStarted;
        combat.RoundResolved -= HandleRound;
        combat.EncounterEnded -= HandleEnded;
    }


    private void Update()
    {
        if (!IsOpen) return;

        if (!playing && !waitingForContinue && queue.Count > 0) StartCoroutine(PlayNext());

        if (radarSweep != null) radarSweep.Rotate(0f, 0f, -radarDegreesPerSecond * Time.unscaledDeltaTime);

        crtTimer -= Time.unscaledDeltaTime;
        if (crtTimer <= 0f && crtImages.Count > 0)
        {
            crtTimer = 0.05f;
            foreach (RawImage crt in crtImages)
            {
                if (crt == null) continue;
                Color c = crt.color;
                c.a = 1f - UnityEngine.Random.value * crtFlicker;
                crt.color = c;
            }
        }
        if (radarBlip != null && radarBlipTimer > 0f)
        {
            radarBlipTimer -= Time.unscaledDeltaTime;
            Color c = radarBlip.color;
            c.a = Mathf.Clamp01(radarBlipTimer / 0.6f);
            radarBlip.color = c;
        }
    }


    // =============================================================== combat events

    private void HandleStarted(EnemyShip enemy)
    {
        EnemyCombatState state = combat.CurrentEnemyState;
        snap = new Snapshot
        {
            enemyName = state != null ? state.DisplayName : "UNKNOWN",
            playerHull = combat.PlayerHull,
            playerMaxHull = Mathf.Max(1f, combat.PlayerMaxHull),
            playerShields = combat.PlayerCurrentShields,
            playerMaxShields = combat.PlayerMaxShields,
            enemyHull = state != null ? state.CurrentHull : 0f,
            enemyMaxHull = state != null ? Mathf.Max(1f, state.MaxHull) : 1f,
            enemyShields = state != null ? state.CurrentShields : 0f,
            enemyMaxShields = state != null ? state.MaxShields : 0f
        };

        queue.Clear();
        log.Clear();
        playing = false;
        waitingForContinue = false;

        if (window != null) window.SetActive(true);
        transform.SetAsLastSibling();
        IsOpen = true;

        if (hold == null && player != null) hold = player.AcquireMovementInterruption("Combat screen");

        EnemyShipDefinition def = enemy != null ? enemy.Definition : null;
        if (enemyShip != null)
        {
            if (def != null) enemyShip.SetSprites(def.CombatSprite != null ? def.CombatSprite : def.Sprite, def.CombatShieldSprite);
            enemyShip.SetPlacement(def != null ? def.CombatOffset : Vector2.zero, def != null ? def.CombatScale : 1f);
        }

        if (playerShip != null) playerShip.ResetVisual(snap.playerShields > 0f);
        if (enemyShip != null) enemyShip.ResetVisual(snap.enemyShields > 0f);
        if (face != null) face.SetMood(CombatFaceMood.Default);

        foreach (SpriteBar bar in bars)
        {
            // No shields at all (capsule, monster): hide that shield bar.
            bool hasStat = !(bar.Stat == CombatBarStat.EnemyShields && snap.enemyMaxShields <= 0f) &&
                           !(bar.Stat == CombatBarStat.PlayerShields && snap.playerMaxShields <= 0f);
            bar.gameObject.SetActive(hasStat);
            bar.SetInstant(BarValue(bar.Stat));
        }
        UpdateLowHull();

        AddLog($"> CONTACT: {snap.enemyName.ToUpperInvariant()}");
        ShowActionButtons(true);
        RefreshStats();
    }


    private void HandleRound(CombatRoundResult result)
    {
        if (!IsOpen || result == null) return;
        queue.Enqueue(new QueueItem { round = result });
    }


    private void HandleEnded(EnemyShip enemy, CombatEncounterOutcome outcome, int reward)
    {
        if (!IsOpen) return;
        queue.Enqueue(new QueueItem { isEnd = true, outcome = outcome });
    }


    // ================================================================= playback

    private IEnumerator PlayNext()
    {
        playing = true;
        SetActionButtonsInteractable(false);

        QueueItem item = queue.Dequeue();
        if (item.isEnd) yield return PlayEnd(item.outcome);
        else yield return PlayRound(item.round);

        playing = false;
        if (!waitingForContinue && queue.Count == 0) SetActionButtonsInteractable(true);
    }


    private IEnumerator PlayRound(CombatRoundResult r)
    {
        CombatSide first = r.PlayerActedFirst ? CombatSide.Player : CombatSide.Enemy;
        CombatSide second = first == CombatSide.Player ? CombatSide.Enemy : CombatSide.Player;

        yield return PlayAction(first, r);
        yield return Pause();
        yield return PlayAction(second, r);

        // End-of-round shield recharge.
        if (r.PlayerShieldRecharge > 0f)
        {
            snap.playerShields = Mathf.Min(snap.playerMaxShields, snap.playerShields + r.PlayerShieldRecharge);
            if (playerShip != null && !playerShip.ShieldVisible && snap.playerShields > 0f) playerShip.SetShieldVisible(true);
        }
        if (r.EnemyShieldRecharge > 0f)
        {
            snap.enemyShields = Mathf.Min(snap.enemyMaxShields, snap.enemyShields + r.EnemyShieldRecharge);
            if (enemyShip != null && !enemyShip.ShieldVisible && snap.enemyShields > 0f) enemyShip.SetShieldVisible(true);
        }
        SyncFromLive();
        UpdateBars();

        if (!string.IsNullOrWhiteSpace(r.Summary)) AddLog(r.Summary);
        RefreshOdds();
        RefreshStats();
        yield return Pause();
        if (face != null)
        {
            // Settle between rounds: angry while hull is low, otherwise default.
            face.SetMood(snap.playerHull / snap.playerMaxHull <= lowHullFraction ? CombatFaceMood.Angry : CombatFaceMood.Default);
        }
    }


    private IEnumerator PlayAction(CombatSide side, CombatRoundResult r)
    {
        CombatAction action = side == CombatSide.Player ? r.PlayerAction : r.EnemyAction;
        CombatShipView self = View(side);
        CombatShipView target = View(Other(side));

        AddLog($"{(side == CombatSide.Player ? "YOU" : snap.enemyName.ToUpperInvariant())}: {Label(action)}");
        RefreshStats();
        ActionShown?.Invoke(side, action);

        switch (action)
        {
            case CombatAction.Defend:
                if (self != null) yield return self.RaiseShield();
                break;

            case CombatAction.Evade:
                if (self != null) yield return self.Evade();
                break;

            case CombatAction.Flee:
                if (side == CombatSide.Player && r.EscapeAttempted)
                {
                    FleeShown?.Invoke(r.EscapeSucceeded);
                    AddLog(r.EscapeSucceeded ? "> ESCAPE VECTOR FOUND" : "> ESCAPE FAILED");
                    RefreshStats();
                    if (!r.EscapeSucceeded && face != null) face.SetMood(CombatFaceMood.Confused);
                    if (self != null) yield return self.FleeDown(r.EscapeSucceeded);
                }
                break;

            case CombatAction.Fire:
                bool attempted = side == CombatSide.Player ? r.PlayerAttackAttempted : r.EnemyAttackAttempted;
                if (!attempted) break;
                bool hit = side == CombatSide.Player ? r.PlayerAttackHit : r.EnemyAttackHit;

                if (self != null && target != null)
                {
                    yield return self.Lunge(target.Rect.anchoredPosition - self.Rect.anchoredPosition);
                }
                PingRadar();

                if (hit) yield return PlayHit(Other(side), r);
                else
                {
                    AddLog("> MISS");
                    RefreshStats();
                    if (face != null) face.SetMood(CombatFaceMood.Confused);
                    Missed?.Invoke(Other(side));
                }
                break;
        }
    }


    private IEnumerator PlayHit(CombatSide targetSide, CombatRoundResult r)
    {
        bool targetIsPlayer = targetSide == CombatSide.Player;
        float shieldDamage = targetIsPlayer ? r.DamageToPlayerShields : r.DamageToEnemyShields;
        float hullDamage = targetIsPlayer ? r.DamageToPlayerHull : r.DamageToEnemyHull;
        CombatShipView target = View(targetSide);

        HitShown?.Invoke(targetSide, hullDamage, shieldDamage);
        if (target != null && (hullDamage > 0f || shieldDamage > 0f)) target.FlashDamage(damageFlashColour, damageFlashSeconds);

        if (shieldDamage > 0f)
        {
            float before = targetIsPlayer ? snap.playerShields : snap.enemyShields;
            float after = Mathf.Max(0f, before - shieldDamage);
            if (targetIsPlayer) snap.playerShields = after; else snap.enemyShields = after;
            UpdateBars();

            if (target != null)
            {
                if (after <= 0f && before > 0f)
                {
                    ShieldsPopped?.Invoke(targetSide);
                    if (targetIsPlayer && face != null) face.SetMood(CombatFaceMood.Confused);
                    yield return target.PopShield();
                }
                else yield return target.FlickerShield();
            }
        }

        if (hullDamage > 0f)
        {
            if (targetIsPlayer) snap.playerHull = Mathf.Max(0f, snap.playerHull - hullDamage);
            else snap.enemyHull = Mathf.Max(0f, snap.enemyHull - hullDamage);
            UpdateBars();
            UpdateLowHull();
            if (face != null && targetIsPlayer) face.SetMood(CombatFaceMood.Angry);
            if (target != null) yield return target.Shake();
        }

        AddLog($"> HIT: {(targetIsPlayer ? "YOU" : "TARGET")} -{shieldDamage:0} SHD -{hullDamage:0} HULL");
        RefreshStats();
    }


    private IEnumerator PlayEnd(CombatEncounterOutcome outcome)
    {
        switch (outcome)
        {
            case CombatEncounterOutcome.Victory:
                Destroyed?.Invoke(CombatSide.Enemy);
                if (face != null) face.SetMood(CombatFaceMood.Default);
                if (enemyShip != null) yield return enemyShip.Explode();
                AddLog("> TARGET DESTROYED");
                break;
            case CombatEncounterOutcome.Fled:
                if (face != null) face.SetMood(CombatFaceMood.Default);
                AddLog("> DISENGAGED");
                break;
            case CombatEncounterOutcome.Defeat:
                Destroyed?.Invoke(CombatSide.Player);
                if (face != null) face.SetMood(CombatFaceMood.Angry);
                if (playerShip != null) yield return playerShip.Explode();
                AddLog("> HULL BREACH. SIGNAL LOST");
                break;
            default:
                AddLog("> ENGAGEMENT INTERRUPTED");
                break;
        }

        RefreshStats();
        EncounterShownEnded?.Invoke(outcome);

        waitingForContinue = true;
        ShowActionButtons(false);
    }


    private void Close()
    {
        waitingForContinue = false;
        IsOpen = false;
        queue.Clear();
        hold?.Release();
        hold = null;
        if (window != null && window != gameObject) window.SetActive(false);
    }


    // ================================================================= buttons

    private void OnAction(int index)
    {
        if (!IsOpen || playing || waitingForContinue || queue.Count > 0 || combat == null) return;

        SetActionButtonsInteractable(false);
        if (!combat.SubmitPlayerAction(Actions[index])) SetActionButtonsInteractable(true);
        // RoundResolved (and maybe EncounterEnded) arrive during that call; Update plays them.
    }


    private void ShowActionButtons(bool actions)
    {
        for (int i = 0; i < actionButtons.Length; i++)
        {
            if (actionButtons[i] != null) actionButtons[i].gameObject.SetActive(actions);
        }
        if (continueButton != null) continueButton.gameObject.SetActive(!actions);
        if (actions)
        {
            RefreshOdds();
            SetActionButtonsInteractable(true);
        }
    }


    private void SetActionButtonsInteractable(bool on)
    {
        TerminalStyle.Palette palette = Palette();
        for (int i = 0; i < actionButtons.Length; i++)
        {
            if (actionButtons[i] == null) continue;
            if (style != null)
            {
                style.ApplyButton(actionButtons[i], actionLabels[i], palette, on);
                actionLabels[i].fontSize = buttonFontSize;
            }
            else actionButtons[i].interactable = on;
        }
    }


    private void RefreshOdds()
    {
        if (combat == null || combat.CurrentEnemyState == null) return;
        SetLabel(0, $"{offensiveLabel} ({Pct(combat.PlayerFireHitChance)})");
        SetLabel(1, $"{defensiveLabel} (-{Pct(combat.PlayerDefendReduction)} DMG)");
        SetLabel(2, $"{evasiveLabel} ({Pct(combat.PlayerEvadeChance)})");
        SetLabel(3, $"{fleeLabel} ({Pct(combat.PlayerEscapeChance)})");
    }


    private void SetLabel(int i, string text)
    {
        if (actionLabels[i] != null) actionLabels[i].text = text;
    }


    // ============================================================ stats, bars, fx

    private void AddLog(string line)
    {
        log.Add(line);
        while (log.Count > logLines) log.RemoveAt(0);
    }


    private void RefreshStats()
    {
        if (statsText == null) return;
        var sb = new StringBuilder();
        sb.Append("SYS://COMBAT\n");
        sb.Append($"TARGET {snap.enemyName.ToUpperInvariant()}\n");
        sb.Append($"HULL {snap.enemyHull:0}/{snap.enemyMaxHull:0}  SHD {snap.enemyShields:0}/{snap.enemyMaxShields:0}\n");
        sb.Append($"YOU    HULL {snap.playerHull:0}/{snap.playerMaxHull:0}  SHD {snap.playerShields:0}/{snap.playerMaxShields:0}\n");
        sb.Append("----------------\n");
        foreach (string line in log) sb.Append(line).Append('\n');
        statsText.text = sb.ToString();
    }


    /// <summary>
    /// After a round, take the real values from the combat controller (while the
    /// fight is still on), so bars and stats never drift from the actual state.
    /// </summary>
    private void SyncFromLive()
    {
        if (combat == null || !combat.IsEncounterActive || combat.CurrentEnemyState == null) return;
        snap.playerHull = combat.PlayerHull;
        snap.playerShields = combat.PlayerCurrentShields;
        snap.enemyHull = combat.CurrentEnemyState.CurrentHull;
        snap.enemyShields = combat.CurrentEnemyState.CurrentShields;
        UpdateLowHull();
    }


    private void UpdateBars()
    {
        foreach (SpriteBar bar in bars) bar.SetTarget(BarValue(bar.Stat));
    }


    private float BarValue(CombatBarStat stat)
    {
        switch (stat)
        {
            case CombatBarStat.PlayerHull: return snap.playerHull / snap.playerMaxHull;
            case CombatBarStat.PlayerShields: return snap.playerMaxShields > 0f ? snap.playerShields / snap.playerMaxShields : 0f;
            case CombatBarStat.EnemyHull: return snap.enemyHull / snap.enemyMaxHull;
            default: return snap.enemyMaxShields > 0f ? snap.enemyShields / snap.enemyMaxShields : 0f;
        }
    }


    private void UpdateLowHull()
    {
        if (playerShip != null) playerShip.SetLowHull(snap.playerHull / snap.playerMaxHull <= lowHullFraction);
        if (enemyShip != null) enemyShip.SetLowHull(snap.enemyHull / snap.enemyMaxHull <= lowHullFraction);
    }


    private void PingRadar()
    {
        if (radarBlip == null || radarOverlay == null) return;
        Rect area = radarOverlay.rect;
        radarBlip.rectTransform.anchoredPosition = new Vector2(
            UnityEngine.Random.Range(-0.35f, 0.35f) * area.width,
            UnityEngine.Random.Range(-0.35f, 0.35f) * area.height);
        radarBlipTimer = 0.6f;
    }


    private IEnumerator Pause()
    {
        float t = 0f;
        while (t < stepPause)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }


    // ==================================================================== build

    private void Build()
    {
        if (built) return;
        built = true;
        Transform root = window != null ? window.transform : transform;

        if (statsArea == null) statsArea = Find<RectTransform>(root, "StatsPanel/Content");
        if (buttonArea == null) buttonArea = Find<RectTransform>(root, "ButtonBar/Buttons");
        if (playerShip == null) playerShip = Find<CombatShipView>(root, "CombatView/PlayerShip");
        if (enemyShip == null) enemyShip = Find<CombatShipView>(root, "CombatView/EnemyShip");
        if (face == null) face = Find<CombatFace>(root, "FacePanel/Face");
        if (radarOverlay == null) radarOverlay = Find<RectTransform>(root, "RadarPanel/Overlay");

        if (clipCombatView)
        {
            Transform view = root.Find("CombatView");
            if (view != null && view.GetComponent<RectMask2D>() == null) view.gameObject.AddComponent<RectMask2D>();
        }

        BuildCrt(root);

        bars.Clear();
        bars.AddRange(root.GetComponentsInChildren<SpriteBar>(true));

        TerminalStyle.Palette palette = Palette();

        if (statsArea != null)
        {
            statsText = NewText("StatsText", statsArea);
            statsText.alignment = TextAlignmentOptions.TopLeft;
            statsText.textWrappingMode = TextWrappingModes.Normal;
            statsText.overflowMode = TextOverflowModes.Truncate;
            if (style != null) style.ApplyText(statsText, palette.primary, statsFontSize);
            else statsText.fontSize = statsFontSize;
        }

        if (buttonArea != null)
        {
            HorizontalLayoutGroup row = buttonArea.GetComponent<HorizontalLayoutGroup>();
            if (row == null) row = buttonArea.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = true;
            row.spacing = 12f;
            row.padding = new RectOffset(12, 12, 12, 12);

            for (int i = 0; i < Actions.Length; i++)
            {
                int index = i;
                actionButtons[i] = NewButton("Action " + Actions[i], buttonArea, "", out actionLabels[i]);
                actionButtons[i].onClick.AddListener(() => OnAction(index));
            }

            continueButton = NewButton("Continue", buttonArea, continueLabel, out TextMeshProUGUI continueText);
            continueButton.onClick.AddListener(() => { if (waitingForContinue) Close(); });
            if (style != null)
            {
                style.ApplyButton(continueButton, continueText, palette, true);
                continueText.fontSize = buttonFontSize;
            }
            continueButton.gameObject.SetActive(false);

            SetLabel(0, offensiveLabel);
            SetLabel(1, defensiveLabel);
            SetLabel(2, evasiveLabel);
            SetLabel(3, fleeLabel);
            SetActionButtonsInteractable(true);
        }

        if (radarOverlay != null)
        {
            Image sweep = NewImage("Sweep", radarOverlay, palette.primary);
            radarSweep = sweep.rectTransform;
            radarSweep.anchorMin = radarSweep.anchorMax = new Vector2(0.5f, 0.5f);
            radarSweep.pivot = new Vector2(0.5f, 0f);
            radarSweep.sizeDelta = new Vector2(radarSweepWidth, Mathf.Max(8f, Mathf.Min(radarOverlay.rect.width, radarOverlay.rect.height) * 0.45f));
            Color sweepColour = palette.primary;
            sweepColour.a = 0.7f;
            sweep.color = sweepColour;

            radarBlip = NewImage("Blip", radarOverlay, palette.primary);
            radarBlip.rectTransform.anchorMin = radarBlip.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            radarBlip.rectTransform.sizeDelta = new Vector2(6f, 6f);
            Color blip = palette.primary;
            blip.a = 0f;
            radarBlip.color = blip;
        }
    }


    /// <summary>
    /// Scanlines like the dialogue UI's, placed in each screen panel just under
    /// its Frame, so everything on the screen reads as a CRT.
    /// </summary>
    private void BuildCrt(Transform root)
    {
        if (style != null && !style.Scanlines) return;

        float thickness = style != null ? style.ScanlineThickness : 8f;
        float gap = style != null ? style.ScanlineGap : 8f;
        float opacity = style != null ? style.ScanlineOpacity : 0.313f;
        float period = Mathf.Max(0.5f, thickness + gap);

        const int height = 64;
        crtTexture = new Texture2D(1, height, TextureFormat.RGBA32, false);
        crtTexture.wrapMode = TextureWrapMode.Repeat;
        crtTexture.filterMode = FilterMode.Bilinear;
        float dark = Mathf.Clamp01(thickness / period);
        for (int y = 0; y < height; y++)
        {
            crtTexture.SetPixel(0, y, new Color(0f, 0f, 0f, (y + 0.5f) / height <= dark ? opacity : 0f));
        }
        crtTexture.Apply();

        foreach (string panelName in crtPanels)
        {
            Transform panel = root.Find(panelName);
            if (panel == null) continue;

            GameObject obj = new GameObject("CRT", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
            obj.transform.SetParent(panel, false);
            obj.GetComponent<LayoutElement>().ignoreLayout = true;
            RectTransform r = (RectTransform)obj.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;

            RawImage crt = obj.GetComponent<RawImage>();
            crt.texture = crtTexture;
            crt.raycastTarget = false;
            crt.uvRect = new Rect(0f, 0f, 1f, Mathf.Max(1f, ((RectTransform)panel).rect.height / period));
            crtImages.Add(crt);

            // Just under the Frame (or on top if there is no Frame).
            Transform frame = panel.Find("Frame");
            if (frame != null) obj.transform.SetSiblingIndex(frame.GetSiblingIndex());
            else obj.transform.SetAsLastSibling();
        }
    }


    private void OnDestroy()
    {
        if (crtTexture != null) Destroy(crtTexture);
    }


    // ================================================================== helpers

    private CombatShipView View(CombatSide side) => side == CombatSide.Player ? playerShip : enemyShip;
    private static CombatSide Other(CombatSide side) => side == CombatSide.Player ? CombatSide.Enemy : CombatSide.Player;
    private TerminalStyle.Palette Palette() => style != null ? style.GetPalette(false) : new TerminalStyle.Palette { background = Color.black, primary = Color.green, dim = Color.gray };
    private static string Pct(float value) => Mathf.RoundToInt(Mathf.Clamp01(value) * 100f) + "%";


    private string Label(CombatAction action)
    {
        switch (action)
        {
            case CombatAction.Fire: return offensiveLabel;
            case CombatAction.Defend: return defensiveLabel;
            case CombatAction.Evade: return evasiveLabel;
            case CombatAction.Flee: return fleeLabel;
            default: return action.ToString().ToUpperInvariant();
        }
    }


    private static T Find<T>(Transform root, string path) where T : Component
    {
        Transform t = root.Find(path);
        return t != null ? t.GetComponent<T>() : null;
    }


    private static TextMeshProUGUI NewText(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);
        RectTransform r = (RectTransform)obj.transform;
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(8f, 8f);
        r.offsetMax = new Vector2(-8f, -8f);
        TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        return text;
    }


    private static Image NewImage(string name, Transform parent, Color colour)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);
        Image image = obj.GetComponent<Image>();
        image.color = colour;
        image.raycastTarget = false;
        return image;
    }


    private static Button NewButton(string name, Transform parent, string label, out TextMeshProUGUI text)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        obj.transform.SetParent(parent, false);
        Button button = obj.GetComponent<Button>();
        button.targetGraphic = obj.GetComponent<Image>();

        text = NewText("Label", obj.transform);
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Truncate;
        text.text = label;
        return button;
    }
}
