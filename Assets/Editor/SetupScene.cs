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

        Debug.Log("TapBeat scene setup complete. Press Play to test.");
    }
}
