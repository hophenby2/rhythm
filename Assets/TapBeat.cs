using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏节拍器：点击屏幕播放音效
/// </summary>
public class TapBeat : MonoBehaviour
{
    AudioSource audioSource;
    AudioClip tapClip;
    Canvas canvas;
    readonly List<RippleInfo> ripples = new List<RippleInfo>();

    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 60;

        var cam = Camera.main;
        if (cam != null)
        {
            cam.backgroundColor = Color.black;
            cam.clearFlags = CameraClearFlags.SolidColor;
        }

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        tapClip = SynthTapSound();
        BuildCanvas();
    }

    void BuildCanvas()
    {
        var go = new GameObject("Canvas");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        go.AddComponent<CanvasScaler>();
    }

    AudioClip SynthTapSound()
    {
        int sr = AudioSettings.outputSampleRate;
        float dur = 0.1f;
        int n = Mathf.CeilToInt(dur * sr);
        var clip = AudioClip.Create("tap", n, 1, sr, false);
        float[] data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.001f, t / dur);
            data[i] = (Mathf.Sin(2f * Mathf.PI * 1200f * t) * 0.3f +
                       Mathf.Sin(2f * Mathf.PI * 1800f * t) * 0.15f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    void Update()
    {
        // 点击检测
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
                OnTap(touch.position);
        }
        else if (Input.GetMouseButtonDown(0))
        {
            OnTap(Input.mousePosition);
        }

        // 更新涟漪
        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            var r = ripples[i];
            float age = Time.time - r.birth;
            if (age > 0.4f)
            {
                Destroy(r.go);
                ripples.RemoveAt(i);
                continue;
            }
            float t = age / 0.4f;
            r.go.transform.localScale = Vector3.one * (1f + t * 3f);
            r.img.color = new Color(1f, 0.9f, 0.2f, 0.5f * (1f - t));
        }
    }

    void OnTap(Vector2 pos)
    {
        audioSource.PlayOneShot(tapClip);
        SpawnRipple(pos);
    }

    void SpawnRipple(Vector2 screenPos)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = new Color(1f, 0.9f, 0.2f, 0.5f);
        img.rectTransform.sizeDelta = new Vector2(40, 40);

        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform, screenPos, null, out localPoint);
        img.rectTransform.anchoredPosition = localPoint;

        ripples.Add(new RippleInfo { go = go, img = img, birth = Time.time });
    }

    struct RippleInfo
    {
        public GameObject go;
        public Image img;
        public float birth;
    }
}
