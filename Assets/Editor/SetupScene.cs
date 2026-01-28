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

        // 创建 TapBeat 对象
        var tapBeat = new GameObject("TapBeat");
        tapBeat.AddComponent<TapBeat>();

        // 标记场景已修改
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log("TapBeat scene setup complete.\n" +
            "两种模式:\n" +
            "1. 导入音频 - 预分析鼓点，播放时判定\n" +
            "2. 自由模式 - 纯点击，只有反馈音效\n\n" +
            "Press Play to test.");
    }

    [MenuItem("TapBeat/Create StreamingAssets Folder")]
    public static void CreateStreamingAssets()
    {
        string path = Application.streamingAssetsPath;
        if (!System.IO.Directory.Exists(path))
        {
            System.IO.Directory.CreateDirectory(path);
            AssetDatabase.Refresh();
        }
        EditorUtility.DisplayDialog("StreamingAssets",
            "已创建 StreamingAssets 文件夹。\n\n" +
            "将音频文件（mp3/wav/ogg）放入此文件夹，\n" +
            "命名为 music.mp3 即可在移动端自动加载。",
            "OK");
    }
}
