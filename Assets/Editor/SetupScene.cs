using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SetupScene
{
    [MenuItem("TapBeat/Setup Scene")]
    public static void Setup()
    {
        // 清空场景
        var scene = SceneManager.GetActiveScene();
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.GetComponent<Camera>() == null)
                Object.DestroyImmediate(go);
        }

        // 设置相机
        var cam = Camera.main;
        if (cam != null)
        {
            cam.backgroundColor = Color.black;
            cam.clearFlags = CameraClearFlags.SolidColor;
        }

        // 创建 TapBeat 对象（会自动添加 MicOnsetDetector 和 AndroidAudioCaptureProxy）
        var tapBeat = new GameObject("TapBeat");
        tapBeat.AddComponent<TapBeat>();

        // 标记场景已修改
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log("TapBeat scene setup complete.\n" +
            "- Layer 1: Tap-tempo BPM lock (always active)\n" +
            "- Layer 2: Microphone onset detection (fallback)\n" +
            "- Layer 3: Android system audio capture (Android 10+ only)\n" +
            "Press Play to test.");
    }

    [MenuItem("TapBeat/Build Settings Info")]
    public static void ShowBuildInfo()
    {
        EditorUtility.DisplayDialog("TapBeat Build Settings",
            "Android 构建注意事项:\n\n" +
            "1. Minimum API Level: 21 (Android 5.0)\n" +
            "2. Target API Level: 29+ (系统音频捕获需要 Android 10)\n" +
            "3. 确保 AndroidManifest.xml 中包含 RECORD_AUDIO 权限\n" +
            "4. 确保 AndroidManifest.xml 中 audio-focus 设为 false\n\n" +
            "iOS 构建注意事项:\n" +
            "1. 在 Player Settings 添加 Microphone Usage Description\n" +
            "2. iOS 无法捕获系统音频，仅支持麦克风模式",
            "OK");
    }
}
