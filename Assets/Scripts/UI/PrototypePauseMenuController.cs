using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Minimal pause and restart menu for the single-sector prototype.
/// It creates itself at runtime so the test scene needs no extra authoring.
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
        menuObject.AddComponent<PrototypePauseMenuController>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (isRestarting || Keyboard.current == null ||
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
        if (!isOpen)
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
    }

    private void Resume()
    {
        if (!isOpen || isRestarting)
        {
            return;
        }

        isOpen = false;
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
