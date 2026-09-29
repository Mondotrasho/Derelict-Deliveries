using UnityEngine;

/// <summary>
/// One master volume for the whole game (music now, SFX later), saved between
/// sessions. It drives AudioListener.volume, so anything that plays through the
/// scene's AudioListener follows it without needing to know this class exists.
/// The pause menu's volume control writes here.
/// </summary>
public static class GameVolume
{
    private const string PrefsKey = "DerelictDeliveries.MasterVolume";
    private const float DefaultVolume = 0.8f;

    private static float master = -1f;

    public static float Master
    {
        get
        {
            if (master < 0f) master = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsKey, DefaultVolume));
            return master;
        }
        set
        {
            master = Mathf.Clamp01(value);
            AudioListener.volume = master;
            PlayerPrefs.SetFloat(PrefsKey, master);
            PlayerPrefs.Save();
        }
    }


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        master = -1f;
    }


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplySaved()
    {
        AudioListener.volume = Master;
    }
}
