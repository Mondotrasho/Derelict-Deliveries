using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Minimal pause and restart menu for the single-sector prototype.
/// It creates itself at runtime so the test scene needs no extra authoring.
///
/// [Oscar - terminal UI additions] If placed in the scene with a
/// BannerChoiceView assigned, the menu shows in the terminal-style window
/// instead of the OnGUI one, and adds a volume control and a credits page.
/// With no view assigned it behaves exactly as originally written (the OnGUI
/// menu below is untouched and still the fallback). The Escape key is now
/// optional (List For Escape) and a HUD button can call Toggle() instead.
/// Additions are marked [TERMINAL UI].
/// </summary>
[DefaultExecutionOrder(-10000)]
[DisallowMultipleComponent]
public sealed class PrototypePauseMenuController : MonoBehaviour
{
    private const float MenuWidth = 420.0f;
    private const float MenuHeight = 250.0f;

    private static PrototypePauseMenuController instance;
    private static bool escapeConsumedThisFrame;

    private bool isOpen;
    private bool isRestarting;
    private float timeScaleBeforePause = 1.0f;

    // [TERMINAL UI] ---------------------------------------------------------
    private const string ResumeId = "resume";
    private const string RestartId = "restart";
    private const string VolumeDownId = "vol-";
    private const string VolumeUpId = "vol+";
    private const string CreditsId = "credits";
    private const string BackId = "back";
    private const int VolumeBarSegments = 10;   // fallback text only (window width unknown)
    private static readonly int[] MainMenuRows = { 1, 2, 1, 1 };

    [Header("Terminal UI (optional - empty = original OnGUI menu)")]
    [Tooltip("Terminal-style window for the menu. Use its own copy, not the planet picker's.")]
    [SerializeField] private BannerChoiceView view;
    [Tooltip("Optional full-screen Image behind the window that stops clicks reaching the HUD while paused.")]
    [SerializeField] private GameObject inputBlocker;
    [SerializeField] private string titlePrefix = "SYS://";
    [SerializeField] private Sprite banner;

    [Header("Input")]
    [Tooltip("Open/close with Escape. Off = only Toggle() (e.g. a HUD button). The auto-created fallback menu always listens, as originally written.")]
    [SerializeField] private bool listenForEscape = false;

    [Header("Volume")]
    [Range(0.01f, 0.5f)] [SerializeField] private float volumeStep = 0.1f;
    [Tooltip("Characters in the volume bar. It is stretched to the window width either way; more = finer steps.")]
    [Range(5, 60)] [SerializeField] private int volumeBarLength = 20;
    [SerializeField] private char volumeFilledChar = '#';
    [SerializeField] private char volumeEmptyChar = '.';

    [Header("Credits")]
    [Tooltip("Scrolling credits screen. If set, CREDITS opens it; otherwise the plain text below is shown.")]
    [SerializeField] private CreditsScreen creditsScreen;
    [Tooltip("Plain text fallback for the credits page.")]
    [SerializeField] private TextAsset creditsText;

    private Coroutine terminalRoutine;
    private bool showingCredits;
    private readonly List<BannerChoiceView.Choice> terminalChoices = new List<BannerChoiceView.Choice>();
    // ------------------------------------------------------------------------

    private GUIStyle titleStyle;
    private GUIStyle messageStyle;
    private GUIStyle buttonStyle;

    /// <summary>
    /// True while the menu is open and during the frame that Escape toggles
    /// it. Gameplay input can use this to avoid reacting to the same keypress.
    /// </summary>
    public static bool BlocksGameplayInput
    {
        get
        {
            return escapeConsumedThisFrame ||
                   (instance != null && instance.isOpen);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        instance = null;
        escapeConsumedThisFrame = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForPrototype()
    {
        if (instance != null ||
            FindFirstObjectByType<PrototypePauseMenuController>() != null)
        {
            return;
        }

        GameObject menuObject = new GameObject("Prototype Pause Menu");
        PrototypePauseMenuController menu = menuObject.AddComponent<PrototypePauseMenuController>();

        // [TERMINAL UI] Only the auto-created menu persists across restarts and
        // listens for Escape (the original behaviour). A scene-placed menu is
        // rebuilt with the scene, so its view reference never goes stale.
        menu.listenForEscape = true;
        DontDestroyOnLoad(menuObject);
    }

    /// <summary>[TERMINAL UI] Opens or closes the menu. Hook a HUD button's OnClick here.</summary>
    public void Toggle()
    {
        if (isRestarting)
        {
            return;
        }

        if (isOpen)
        {
            Resume();
        }
        else
        {
            Open();
        }
    }

    /// <summary>[TERMINAL UI] Static helper so other scripts can open the menu without a reference.</summary>
    public static void ToggleMenu()
    {
        if (instance != null)
        {
            instance.Toggle();
        }
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        // [TERMINAL UI] DontDestroyOnLoad moved to CreateForPrototype - see the note there.
    }

    private void Update()
    {
        if (isRestarting || !listenForEscape || Keyboard.current == null ||
            !Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        escapeConsumedThisFrame = true;

        if (isOpen)
        {
            Resume();
        }
        else
        {
            Open();
        }
    }

    private void LateUpdate()
    {
        escapeConsumedThisFrame = false;
    }

    private void OnDestroy()
    {
        if (instance != this)
        {
            return;
        }

        RestoreTimeScale();
        instance = null;
        escapeConsumedThisFrame = false;
    }

    private void OnGUI()
    {
        if (!isOpen || view != null)   // [TERMINAL UI] the terminal window replaces this when assigned
        {
            return;
        }

        EnsureStyles();
        GUI.depth = -10000;

        Color previousColour = GUI.color;
        GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.72f);
        GUI.DrawTexture(
            new Rect(0.0f, 0.0f, Screen.width, Screen.height),
            Texture2D.whiteTexture
        );
        GUI.color = previousColour;

        Rect menuRect = new Rect(
            (Screen.width - MenuWidth) * 0.5f,
            (Screen.height - MenuHeight) * 0.5f,
            MenuWidth,
            MenuHeight
        );

        GUILayout.BeginArea(menuRect, GUI.skin.window);
        GUILayout.Space(18.0f);
        GUILayout.Label("PAUSED", titleStyle);
        GUILayout.Space(6.0f);
        GUILayout.Label(
            "Resume the current test or restart this sector.",
            messageStyle
        );
        GUILayout.Space(22.0f);

        GUI.enabled = !isRestarting;

        if (GUILayout.Button("RESUME", buttonStyle, GUILayout.Height(46.0f)))
        {
            Resume();
        }

        GUILayout.Space(10.0f);

        if (GUILayout.Button(
                isRestarting ? "RESTARTING" : "RESTART SECTOR",
                buttonStyle,
                GUILayout.Height(46.0f)))
        {
            RestartSector();
        }

        GUI.enabled = true;
        GUILayout.EndArea();
    }

    private void Open()
    {
        if (isOpen)
        {
            return;
        }

        timeScaleBeforePause = Time.timeScale;
        Time.timeScale = 0.0f;
        isOpen = true;

        // [TERMINAL UI]
        if (view != null)
        {
            showingCredits = false;
            if (inputBlocker != null)
            {
                inputBlocker.SetActive(true);
                inputBlocker.transform.SetAsLastSibling();   // view then moves in front of it on Show
            }
            terminalRoutine = StartCoroutine(TerminalMenu());
        }
    }

    private void Resume()
    {
        if (!isOpen || isRestarting)
        {
            return;
        }

        isOpen = false;
        CloseTerminal();   // [TERMINAL UI]
        RestoreTimeScale();
    }

    private void RestartSector()
    {
        if (isRestarting)
        {
            return;
        }

        isRestarting = true;
        isOpen = false;
        CloseTerminal();   // [TERMINAL UI]
        Time.timeScale = 1.0f;

        Scene currentScene = SceneManager.GetActiveScene();

        if (currentScene.buildIndex >= 0)
        {
            SceneManager.LoadScene(currentScene.buildIndex);
        }
        else
        {
            SceneManager.LoadScene(currentScene.name);
        }

        isRestarting = false;
    }

    // [TERMINAL UI] ---------------------------------------------------------

    private IEnumerator TerminalMenu()
    {
        while (isOpen)
        {
            ShowTerminalPage();

            string picked = null;
            yield return view.WaitForChoice(id => picked = id);

            if (!isOpen)
            {
                yield break;   // closed from elsewhere (Toggle, restart)
            }

            switch (picked)
            {
                case null:   // window hidden by something else: treat as resume
                case ResumeId:
                    terminalRoutine = null;
                    Resume();
                    yield break;
                case RestartId:
                    terminalRoutine = null;
                    RestartSector();
                    yield break;
                case VolumeDownId:
                    GameVolume.Master = GameVolume.Master - volumeStep;
                    break;
                case VolumeUpId:
                    GameVolume.Master = GameVolume.Master + volumeStep;
                    break;
                case CreditsId:
                    if (creditsScreen != null)
                    {
                        view.Hide();
                        yield return creditsScreen.PlayAndWait();   // ShowTerminalPage reopens the menu after
                    }
                    else
                    {
                        showingCredits = true;
                    }
                    break;
                case BackId:
                    showingCredits = false;
                    break;
            }
        }
    }

    private void ShowTerminalPage()
    {
        terminalChoices.Clear();

        if (showingCredits)
        {
            string credits = creditsText != null ? creditsText.text : "No credits file assigned.";
            view.Show(banner, titlePrefix + "CREDITS", "PAUSED", credits, false);
            terminalChoices.Add(new BannerChoiceView.Choice(BackId, "BACK"));
        }
        else
        {
            view.Show(banner, titlePrefix + "PAUSED", "SIM HALTED", null, false);
            terminalChoices.Add(new BannerChoiceView.Choice(ResumeId, "RESUME"));
            terminalChoices.Add(new BannerChoiceView.Choice(VolumeDownId, "VOLUME -", GameVolume.Master > 0.001f));
            terminalChoices.Add(new BannerChoiceView.Choice(VolumeUpId, "VOLUME +", GameVolume.Master < 0.999f));
            terminalChoices.Add(new BannerChoiceView.Choice(CreditsId, "CREDITS"));
            terminalChoices.Add(new BannerChoiceView.Choice(RestartId, "RESTART SECTOR"));
        }

        view.SetChoices(terminalChoices);

        if (!showingCredits)
        {
            // RESUME / volume bar / [VOLUME -][VOLUME +] / CREDITS / RESTART SECTOR
            view.SetChoiceRows(MainMenuRows, new[] { null, VolumeLine(), null, null },
                               TMPro.TextAlignmentOptions.MidlineLeft, spread: true);
            view.SetRowLabelBuilder(1, BuildVolumeBar);   // stretches the bar to the window width
        }
    }

    private static string VolumeLine()
    {
        float volume = GameVolume.Master;
        int filled = Mathf.RoundToInt(volume * VolumeBarSegments);
        StringBuilder sb = new StringBuilder("VOLUME  [");
        sb.Append('#', filled).Append('.', VolumeBarSegments - filled);
        sb.Append("]  ").Append(Mathf.RoundToInt(volume * 100.0f)).Append('%');
        return sb.ToString();
    }

    /// <summary>
    /// "VOLUME [####......]  60%" where the bar exactly fills the width left over.
    /// Every bar character is forced to the same width with TMP's &lt;mspace&gt;, so
    /// the bar never changes length as it fills.
    /// </summary>
    private string BuildVolumeBar(TMP_Text text, float width)
    {
        float volume = GameVolume.Master;
        int percent = Mathf.RoundToInt(volume * 100.0f);
        const string prefix = "VOLUME [";
        string suffix = "] " + percent + "%";

        // Measure with "100%" so the bar keeps the same length at any volume.
        float fixedWidth = text.GetPreferredValues(prefix + "] 100%").x;
        float barWidth = width - fixedWidth - text.fontSize * 0.5f;   // small safety margin
        float fontSize = Mathf.Max(1.0f, text.fontSize);
        float charEm = Mathf.Max(0.05f, barWidth / volumeBarLength / fontSize);

        int filled = Mathf.RoundToInt(volume * volumeBarLength);
        StringBuilder sb = new StringBuilder(prefix);
        sb.Append("<mspace=").Append(charEm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("em>");
        sb.Append(volumeFilledChar, filled).Append(volumeEmptyChar, volumeBarLength - filled);
        sb.Append("</mspace>").Append(suffix);
        return sb.ToString();
    }

    private void CloseTerminal()
    {
        if (terminalRoutine != null)
        {
            StopCoroutine(terminalRoutine);
            terminalRoutine = null;
        }

        if (view != null)
        {
            view.Hide();
        }

        if (creditsScreen != null)
        {
            creditsScreen.Close();
        }

        if (inputBlocker != null)
        {
            inputBlocker.SetActive(false);
        }
    }

    // ------------------------------------------------------------------------

    private void RestoreTimeScale()
    {
        if (isOpen || Mathf.Approximately(Time.timeScale, 0.0f))
        {
            Time.timeScale = Mathf.Max(0.0f, timeScaleBeforePause);
        }
    }

    private void EnsureStyles()
    {
        if (titleStyle != null)
        {
            return;
        }

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 30,
            fontStyle = FontStyle.Bold
        };

        messageStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 16,
            wordWrap = true
        };

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold
        };
    }
}
