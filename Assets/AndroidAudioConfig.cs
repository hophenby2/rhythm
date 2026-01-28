using UnityEngine;

/// <summary>
/// Android 平台：放弃音频焦点，确保不打断其他 App 的音乐播放。
/// 在 iOS 上设置 AVAudioSession 为 Ambient 模式。
/// </summary>
public class AndroidAudioConfig : MonoBehaviour
{
    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AbandonAudioFocusAndroid();
#elif UNITY_IOS && !UNITY_EDITOR
        SetAmbientAudioIOS();
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static void AbandonAudioFocusAndroid()
    {
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            {
                var audioServiceField = new AndroidJavaClass("android.content.Context")
                    .GetStatic<string>("AUDIO_SERVICE");
                using (var audioManager = context.Call<AndroidJavaObject>("getSystemService", audioServiceField))
                {
                    // 放弃音频焦点
                    audioManager.Call<int>("abandonAudioFocus", (AndroidJavaObject)null);
                }
            }
            Debug.Log("[TapBeat] Audio focus abandoned on Android");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[TapBeat] Could not abandon audio focus: " + e.Message);
        }
    }
#endif

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern void TapBeat_SetAmbientAudio();

    static void SetAmbientAudioIOS()
    {
        try { TapBeat_SetAmbientAudio(); }
        catch { Debug.LogWarning("[TapBeat] iOS ambient audio plugin not found"); }
    }
#endif
}
