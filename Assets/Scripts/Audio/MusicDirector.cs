using UnityEngine;

/// <summary>
/// Background music. Plays one of three moods and crossfades between them:
///
///   Combat  -> while a combat encounter is running
///   Hunted  -> once the detection countdown has hit zero (the hunt is on)
///   Ambient -> everything else
///
/// Each mood is a playlist that steps to the next track when one ends (a
/// one-track playlist just loops). It only READS existing systems
/// (CombatEncounterController, TurnManager, EnemySpawnController) and changes
/// nothing on them. Uses unscaled time, so music keeps playing while paused.
///
/// Put this on its own new GameObject. It adds its own two AudioSources.
/// Volume is GameVolume.Master (the pause menu); the per-mood sliders below
/// are just for balancing the tracks against each other.
/// </summary>
[DisallowMultipleComponent]
public sealed class MusicDirector : MonoBehaviour
{
    private enum Mood { None, Ambient, Hunted, Combat }

    [Header("Playlists")]
    [Tooltip("Slow, moody tracks. Played in order, then back to the first.")]
    [SerializeField] private AudioClip[] ambientTracks = new AudioClip[0];
    [Tooltip("Plays once the hunt has started. Empty = keep the ambient playlist.")]
    [SerializeField] private AudioClip[] huntedTracks = new AudioClip[0];
    [Tooltip("Plays during combat encounters. Empty = keep whatever was playing.")]
    [SerializeField] private AudioClip[] combatTracks = new AudioClip[0];

    [Header("Mix")]
    [Range(0f, 1f)] [SerializeField] private float ambientVolume = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float huntedVolume = 0.75f;
    [Range(0f, 1f)] [SerializeField] private float combatVolume = 0.8f;

    [Header("Timing")]
    [Tooltip("Seconds to crossfade when the mood changes.")]
    [Min(0f)] [SerializeField] private float moodCrossfade = 2.5f;
    [Tooltip("Seconds to crossfade into the next track of the same playlist.")]
    [Min(0f)] [SerializeField] private float trackCrossfade = 4f;
    [Tooltip("Stay in combat music this long after a fight ends, so it doesn't snap straight out.")]
    [Min(0f)] [SerializeField] private float combatHoldSeconds = 1.5f;

    [Header("References (empty = found automatically)")]
    [SerializeField] private CombatEncounterController combatController;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private EnemySpawnController enemySpawner;

    private AudioSource sourceA, sourceB, active, fading;
    private Mood mood = Mood.None;
    private float fadeDuration, fadeElapsed, fadeFromVolume;
    private bool fadingActive;
    private float combatHoldUntil;
    private readonly int[] nextIndex = new int[4];


    private void Awake()
    {
        if (combatController == null) combatController = FindFirstObjectByType<CombatEncounterController>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();

        sourceA = CreateSource("Music A");
        sourceB = CreateSource("Music B");
        active = sourceA;
        fading = sourceB;
    }


    private AudioSource CreateSource(string label)
    {
        AudioSource s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = false;
        s.spatialBlend = 0f;   // 2D
        s.volume = 0f;
        s.ignoreListenerPause = true;
        return s;
    }


    private void Update()
    {
        Mood wanted = WantedMood();
        if (wanted != mood)
        {
            mood = wanted;
            PlayNext(moodCrossfade, true);
        }
        else if (active.clip != null && TrackEnding())
        {
            PlayNext(trackCrossfade, false);
        }

        UpdateFade();
    }


    private Mood WantedMood()
    {
        bool inCombat = (combatController != null && combatController.IsEncounterActive) ||
                        (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat);
        if (inCombat) combatHoldUntil = Time.unscaledTime + combatHoldSeconds;

        if ((inCombat || Time.unscaledTime < combatHoldUntil) && combatTracks.Length > 0) return Mood.Combat;
        if (enemySpawner != null && enemySpawner.IsDetectionActive && huntedTracks.Length > 0) return Mood.Hunted;
        return Mood.Ambient;
    }


    private bool TrackEnding()
    {
        if (fadingActive) return false;
        if (!active.isPlaying) return true;
        AudioClip[] list = Playlist(mood);
        if (list.Length <= 1) return false;   // single track loops on its own
        return active.clip.length - active.time <= trackCrossfade;
    }


    private void PlayNext(float crossfade, bool moodChanged)
    {
        AudioClip[] list = Playlist(mood);
        AudioClip clip = PickClip(list);
        if (clip == null)
        {
            StartFade(null, crossfade);
            return;
        }

        // Same clip already playing (e.g. a one-track mood re-entered): leave it alone.
        if (moodChanged && active.clip == clip && active.isPlaying && !fadingActive) return;

        StartFade(clip, crossfade);
        active.loop = list.Length == 1;
    }


    private AudioClip PickClip(AudioClip[] list)
    {
        if (list == null || list.Length == 0) return null;
        int m = (int)mood;
        for (int tries = 0; tries < list.Length; tries++)
        {
            int i = nextIndex[m] % list.Length;
            nextIndex[m] = i + 1;
            if (list[i] != null) return list[i];
        }
        return null;
    }


    private void StartFade(AudioClip clip, float duration)
    {
        // The old active source becomes the one fading out.
        AudioSource old = active;
        active = fading;
        fading = old;

        fadeFromVolume = fading.volume;
        fadeDuration = Mathf.Max(0.01f, duration);
        fadeElapsed = 0f;
        fadingActive = true;

        active.Stop();
        active.clip = clip;
        active.volume = 0f;
        active.time = 0f;
        if (clip != null) active.Play();
    }


    private void UpdateFade()
    {
        float target = MoodVolume(mood);

        if (!fadingActive)
        {
            active.volume = active.clip != null ? target : 0f;
            return;
        }

        fadeElapsed += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(fadeElapsed / fadeDuration);
        active.volume = active.clip != null ? target * t : 0f;
        fading.volume = fadeFromVolume * (1f - t);

        if (t >= 1f)
        {
            fading.Stop();
            fading.clip = null;
            fadingActive = false;
        }
    }


    private AudioClip[] Playlist(Mood m)
    {
        switch (m)
        {
            case Mood.Combat: return combatTracks;
            case Mood.Hunted: return huntedTracks;
            default: return ambientTracks;
        }
    }


    private float MoodVolume(Mood m)
    {
        switch (m)
        {
            case Mood.Combat: return combatVolume;
            case Mood.Hunted: return huntedVolume;
            default: return ambientVolume;
        }
    }
}
