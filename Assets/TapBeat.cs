using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏音游：点击屏幕打节拍，不打断其他音乐播放。
/// 挂载到场景中任意 GameObject 即可，会自动创建所需 UI。
/// </summary>
public class TapBeat : MonoBehaviour
{
    // --- 音色参数 ---
    static readonly SoundDef[] soundDefs = new SoundDef[]
    {
        // Hi-hat
        new SoundDef(800f, 2000f, 0.08f, 0.15f, WaveType.Square),
        // Kick
        new SoundDef(150f, 30f, 0.15f, 0.30f, WaveType.Sine),
        // Rim / 木鱼
        new SoundDef(520f, 400f, 0.06f, 0.25f, WaveType.Sine),
        // Snare (高频方波模拟噪声)
        new SoundDef(600f, 1800f, 0.09f, 0.18f, WaveType.Noise),
    };

    int currentSound = 0;
    AudioSource audioSource;

    // BPM
    readonly List<float> tapTimes = new List<float>();
    float bpmHideTimer;
    Text hudText;
    Canvas canvas;

    // 涟漪池
    readonly List<RippleData> ripples = new List<RippleData>();
    static readonly Color[] rippleColors = {
        new Color(1f, .27f, .27f), new Color(.27f, .67f, 1f),
        new Color(.27f, 1f, .53f), new Color(1f, .67f, .27f),
        new Color(1f, .27f, 1f),   new Color(1f, 1f, .27f)
    };
    int colorIdx;

    // 双指检测
    float lastSwitchTime;

    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 60;
        Camera.main.backgroundColor = Color.black;
        Camera.main.clearFlags = CameraClearFlags.SolidColor;

        // AudioSource 设置为不独占 (Android 默认 Ambient 模式不打断其他音乐)
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;

        BuildUI();
        PrepareSounds();
    }

    void BuildUI()
    {
        // Canvas
        var canvasGo = new GameObject("Canvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        // HUD text
        var hudGo = new GameObject("HUD");
        hudGo.transform.SetParent(canvasGo.transform, false);
        hudText = hudGo.AddComponent<Text>();
        hudText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (hudText.font == null)
            hudText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        hudText.fontSize = 28;
        hudText.alignment = TextAnchor.UpperCenter;
        hudText.color = new Color(1, 1, 1, 0.25f);
        hudText.text = "轻触屏幕打节拍 | BPM: --";
        hudText.raycastTarget = false;
        var rt = hudText.rectTransform;
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1);
        rt.anchoredPosition = new Vector2(0, -20);
        rt.sizeDelta = new Vector2(0, 50);
    }

    // --- 音频合成 ---
    readonly Dictionary<int, AudioClip> clipCache = new Dictionary<int, AudioClip>();

    void PrepareSounds()
    {
        for (int i = 0; i < soundDefs.Length; i++)
            clipCache[i] = GenerateClip(soundDefs[i], i);
    }

    AudioClip GenerateClip(SoundDef def, int id)
    {
        int sampleRate = AudioSettings.outputSampleRate;
        int samples = Mathf.CeilToInt(def.duration * sampleRate);
        var clip = AudioClip.Create("beat_" + id, samples, 1, sampleRate, false);
        float[] data = new float[samples];

        // 用于噪声的随机种子
        System.Random rng = new System.Random(42 + id);

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float progress = t / def.duration; // 0~1

            // 频率随时间指数衰减 (从 freqStart 到 freqEnd)
            float freq = def.freqStart * Mathf.Pow(def.freqEnd / def.freqStart, progress);

            // 振幅包络：指数衰减
            float amp = def.volume * Mathf.Pow(0.001f, progress);

            float sample;
            switch (def.wave)
            {
                case WaveType.Square:
                    float phase = t * freq;
                    sample = (phase % 1f < 0.5f) ? 1f : -1f;
                    sample *= amp;
                    break;
                case WaveType.Noise:
                    sample = ((float)rng.NextDouble() * 2f - 1f) * amp;
                    break;
                default: // Sine
                    sample = Mathf.Sin(2f * Mathf.PI * freq * t) * amp;
                    break;
            }
            data[i] = sample;
        }
        clip.SetData(data, 0);
        return clip;
    }

    void PlayBeat()
    {
        audioSource.PlayOneShot(clipCache[currentSound]);
    }

    // --- Update ---
    void Update()
    {
        HandleInput();
        UpdateRipples();
        UpdateHUD();
    }

    void HandleInput()
    {
        // 触摸
        if (Input.touchCount > 0)
        {
            // 双指切换音色
            if (Input.touchCount >= 2)
            {
                if (Time.time - lastSwitchTime > 0.4f)
                {
                    lastSwitchTime = Time.time;
                    currentSound = (currentSound + 1) % soundDefs.Length;
                    PlayBeat();
                    ShowHUD("音色 " + (currentSound + 1) + "/" + soundDefs.Length);
                }
                return;
            }

            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                PlayBeat();
                CalcBPM();
                SpawnRipple(touch.position);
            }
        }
        // 鼠标（编辑器调试）
        else if (Input.GetMouseButtonDown(0))
        {
            PlayBeat();
            CalcBPM();
            SpawnRipple(Input.mousePosition);
        }
    }

    // --- BPM ---
    void CalcBPM()
    {
        tapTimes.Add(Time.unscaledTime);
        while (tapTimes.Count > 8) tapTimes.RemoveAt(0);

        if (tapTimes.Count < 2) { ShowHUD("BPM: --"); return; }

        float sum = 0;
        for (int i = 1; i < tapTimes.Count; i++)
            sum += tapTimes[i] - tapTimes[i - 1];
        float avg = sum / (tapTimes.Count - 1);
        int bpm = Mathf.RoundToInt(60f / avg);

        ShowHUD(bpm > 300 ? "BPM: --" : "BPM: " + bpm);
    }

    void ShowHUD(string text)
    {
        hudText.text = text;
        hudText.color = new Color(1, 1, 1, 0.3f);
        bpmHideTimer = 3f;
    }

    void UpdateHUD()
    {
        if (bpmHideTimer > 0)
        {
            bpmHideTimer -= Time.deltaTime;
            if (bpmHideTimer <= 1f)
                hudText.color = new Color(1, 1, 1, 0.3f * bpmHideTimer);
        }
    }

    // --- 涟漪 ---
    struct RippleData
    {
        public GameObject go;
        public Image img;
        public float birth;
        public Vector2 pos;
        public Color color;
    }

    void SpawnRipple(Vector2 screenPos)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = rippleColors[colorIdx % rippleColors.Length];
        colorIdx++;

        // 空心圆：用 Outline 近似
        img.color = new Color(img.color.r, img.color.g, img.color.b, 0.5f);
        var outline = go.AddComponent<Outline>();
        outline.effectColor = img.color;
        outline.effectDistance = new Vector2(2, 2);
        img.sprite = null; // 纯色方块，缩放后近似

        var rt = img.rectTransform;
        rt.sizeDelta = new Vector2(30, 30);

        // 屏幕坐标转换到 Canvas
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform, screenPos, null, out localPoint);
        rt.anchoredPosition = localPoint;

        ripples.Add(new RippleData
        {
            go = go, img = img, birth = Time.time,
            pos = localPoint, color = img.color
        });
    }

    void UpdateRipples()
    {
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
            float scale = 1f + t * 3f;
            r.go.transform.localScale = Vector3.one * scale;
            r.img.color = new Color(r.color.r, r.color.g, r.color.b, 0.5f * (1f - t));
        }
    }

    // --- 数据结构 ---
    enum WaveType { Sine, Square, Noise }

    struct SoundDef
    {
        public float freqStart, freqEnd, duration, volume;
        public WaveType wave;
        public SoundDef(float fs, float fe, float d, float v, WaveType w)
        { freqStart = fs; freqEnd = fe; duration = d; volume = v; wave = w; }
    }
}
