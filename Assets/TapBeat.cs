using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏节拍器
/// - 点击时立即播放可延长音效，松手时停止（最小0.05秒）
/// - 多触摸点独立音效，正确混音
/// - 涟漪扩散到全屏，无透明度变化
/// - 每次启动都进入设置界面
/// </summary>
public class TapBeat : MonoBehaviour
{
    // 状态
    enum State { Settings, Playing }
    State state = State.Settings;

    // 设置值
    float delayMs = 0f;
    float volume = 0.8f;
    float rippleBrightness = 0.8f;
    int selectedSound = 0;

    // 双击检测
    float lastTapTime;
    const float DoubleTapThreshold = 0.3f;

    // 音频 - 每个触摸点独立
    const float MinPlayTime = 0.1f;
    const int MaxTouchSources = 10;

    class TouchSound
    {
        public int id; // fingerId 或 -1 表示鼠标
        public AudioSource attackSource;
        public AudioSource sustainSource;
        public float startTime;
        public bool isPlaying;
        public Coroutine stopCoroutine;
    }
    List<TouchSound> touchSounds = new List<TouchSound>();
    Queue<TouchSound> soundPool = new Queue<TouchSound>();

    // 音效预设
    struct SoundPreset
    {
        public string name;
        public AudioClip attackClip;
        public AudioClip sustainClip;
    }
    SoundPreset[] presets;

    // UI
    Canvas canvas;
    GameObject settingsPanel;
    Slider offsetSlider, volumeSlider, brightnessSlider;
    Text hintText;
    Text[] soundLabels;
    readonly List<RippleInfo> ripples = new List<RippleInfo>();

    // 彩虹七色
    static readonly Color[] RainbowColors = new Color[]
    {
        new Color(1f, 0f, 0f),       // 红
        new Color(1f, 0.5f, 0f),     // 橙
        new Color(1f, 1f, 0f),       // 黄
        new Color(0f, 1f, 0f),       // 绿
        new Color(0f, 1f, 1f),       // 青
        new Color(0f, 0f, 1f),       // 蓝
        new Color(0.5f, 0f, 1f)      // 紫
    };

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

        // 预创建音频源池
        for (int i = 0; i < MaxTouchSources; i++)
        {
            var ts = new TouchSound
            {
                attackSource = gameObject.AddComponent<AudioSource>(),
                sustainSource = gameObject.AddComponent<AudioSource>()
            };
            ts.attackSource.playOnAwake = false;
            ts.attackSource.loop = false;
            ts.sustainSource.playOnAwake = false;
            ts.sustainSource.loop = true;
            soundPool.Enqueue(ts);
        }

        CreateSoundPresets();
        LoadSettings();
        BuildUI();

        // 每次启动都进入设置界面
        state = State.Settings;
    }

    void CreateSoundPresets()
    {
        int sr = AudioSettings.outputSampleRate;
        presets = new SoundPreset[6];

        presets[0] = new SoundPreset
        {
            name = "电子",
            attackClip = CreateAttackClip(sr, 1200f, 1800f, 0.05f),
            sustainClip = CreateSustainClip(sr, 1200f, 1800f, 0.1f)
        };

        presets[1] = new SoundPreset
        {
            name = "钢琴",
            attackClip = CreatePianoAttack(sr),
            sustainClip = CreatePianoSustain(sr)
        };

        presets[2] = new SoundPreset
        {
            name = "铃声",
            attackClip = CreateBellAttack(sr),
            sustainClip = CreateBellSustain(sr)
        };

        presets[3] = new SoundPreset
        {
            name = "低音",
            attackClip = CreateAttackClip(sr, 220f, 440f, 0.06f),
            sustainClip = CreateSustainClip(sr, 220f, 330f, 0.15f)
        };

        presets[4] = new SoundPreset
        {
            name = "弦乐",
            attackClip = CreateStringAttack(sr),
            sustainClip = CreateStringSustain(sr)
        };

        presets[5] = new SoundPreset
        {
            name = "合成",
            attackClip = CreateSynthAttack(sr),
            sustainClip = CreateSynthSustain(sr)
        };
    }

    AudioClip CreateAttackClip(int sr, float freq1, float freq2, float duration)
    {
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("attack", n, 1, sr, false);
        float[] data = new float[n];

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.01f, t / duration);
            data[i] = (Mathf.Sin(2f * Mathf.PI * freq1 * t) * 0.4f +
                       Mathf.Sin(2f * Mathf.PI * freq2 * t) * 0.2f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateSustainClip(int sr, float freq1, float freq2, float duration)
    {
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("sustain", n, 1, sr, false);
        float[] data = new float[n];

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float fadeIn = Mathf.Clamp01(t / 0.01f);
            float fadeOut = Mathf.Clamp01((duration - t) / 0.01f);
            float env = (0.3f + 0.05f * Mathf.Sin(2f * Mathf.PI * 8f * t)) * fadeIn * fadeOut;
            data[i] = (Mathf.Sin(2f * Mathf.PI * freq1 * t) * 0.35f +
                       Mathf.Sin(2f * Mathf.PI * freq2 * t) * 0.15f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreatePianoAttack(int sr)
    {
        float duration = 0.08f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("pianoAttack", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 523.25f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.005f, t / duration);
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * t) * 0.35f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2f * t) * 0.2f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 3f * t) * 0.1f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 4f * t) * 0.05f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreatePianoSustain(int sr)
    {
        float duration = 0.12f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("pianoSustain", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 523.25f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float fadeIn = Mathf.Clamp01(t / 0.01f);
            float fadeOut = Mathf.Clamp01((duration - t) / 0.01f);
            float env = 0.25f * fadeIn * fadeOut;
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * t) * 0.3f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2f * t) * 0.15f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 3f * t) * 0.08f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateBellAttack(int sr)
    {
        float duration = 0.06f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("bellAttack", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 2000f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.001f, t / duration);
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * t) * 0.3f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2.4f * t) * 0.2f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 3.1f * t) * 0.1f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateBellSustain(int sr)
    {
        float duration = 0.08f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("bellSustain", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 2000f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float fadeIn = Mathf.Clamp01(t / 0.005f);
            float fadeOut = Mathf.Clamp01((duration - t) / 0.005f);
            float env = 0.2f * fadeIn * fadeOut;
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * t) * 0.25f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2.4f * t) * 0.15f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateStringAttack(int sr)
    {
        float duration = 0.1f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("stringAttack", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 440f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Clamp01(t / 0.03f) * Mathf.Pow(0.1f, Mathf.Max(0, t - 0.03f) / duration);
            float vibrato = 1f + 0.003f * Mathf.Sin(2f * Mathf.PI * 5f * t);
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * vibrato * t) * 0.3f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2f * vibrato * t) * 0.15f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 3f * vibrato * t) * 0.08f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateStringSustain(int sr)
    {
        float duration = 0.15f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("stringSustain", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 440f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float fadeIn = Mathf.Clamp01(t / 0.01f);
            float fadeOut = Mathf.Clamp01((duration - t) / 0.01f);
            float env = 0.28f * fadeIn * fadeOut;
            float vibrato = 1f + 0.004f * Mathf.Sin(2f * Mathf.PI * 5.5f * t);
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * vibrato * t) * 0.28f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 2f * vibrato * t) * 0.12f +
                       Mathf.Sin(2f * Mathf.PI * baseFreq * 3f * vibrato * t) * 0.06f) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateSynthAttack(int sr)
    {
        float duration = 0.04f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("synthAttack", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 880f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.01f, t / duration);
            float saw = 0f;
            for (int h = 1; h <= 8; h++)
                saw += Mathf.Sin(2f * Mathf.PI * baseFreq * h * t) / h * 0.15f;
            data[i] = (Mathf.Sign(Mathf.Sin(2f * Mathf.PI * baseFreq * t)) * 0.2f + saw) * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    AudioClip CreateSynthSustain(int sr)
    {
        float duration = 0.1f;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("synthSustain", n, 1, sr, false);
        float[] data = new float[n];
        float baseFreq = 880f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float fadeIn = Mathf.Clamp01(t / 0.008f);
            float fadeOut = Mathf.Clamp01((duration - t) / 0.008f);
            float env = 0.25f * fadeIn * fadeOut;
            float lfo = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 4f * t);
            float saw = 0f;
            for (int h = 1; h <= 6; h++)
                saw += Mathf.Sin(2f * Mathf.PI * baseFreq * h * t) / h * 0.12f * Mathf.Pow(lfo, h * 0.3f);
            data[i] = saw * env;
        }
        clip.SetData(data, 0);
        return clip;
    }

    void LoadSettings()
    {
        delayMs = PlayerPrefs.GetFloat("TapBeat_Delay", 0f);
        volume = PlayerPrefs.GetFloat("TapBeat_Volume", 0.8f);
        rippleBrightness = PlayerPrefs.GetFloat("TapBeat_Brightness", 0.8f);
        selectedSound = PlayerPrefs.GetInt("TapBeat_Sound", 0);
        if (selectedSound < 0 || selectedSound >= presets.Length)
            selectedSound = 0;
    }

    void SaveSettings()
    {
        PlayerPrefs.SetFloat("TapBeat_Delay", delayMs);
        PlayerPrefs.SetFloat("TapBeat_Volume", volume);
        PlayerPrefs.SetFloat("TapBeat_Brightness", rippleBrightness);
        PlayerPrefs.SetInt("TapBeat_Sound", selectedSound);
        PlayerPrefs.Save();
    }


    void BuildUI()
    {
        if (FindObjectOfType<EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        var canvasGo = new GameObject("Canvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGo.AddComponent<GraphicRaycaster>();

        settingsPanel = new GameObject("Settings");
        settingsPanel.transform.SetParent(canvas.transform, false);
        var panelRt = settingsPanel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.1f, 0.3f);
        panelRt.anchorMax = new Vector2(0.9f, 0.7f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        var panelImg = settingsPanel.AddComponent<Image>();
        panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);

        CreateSoundSelector(settingsPanel, 0.85f);

        offsetSlider = CreateSlider(settingsPanel, "延迟", 0.62f, 0f, 200f, delayMs, v =>
        {
            delayMs = v;
            SaveSettings();
            PlayPreviewSound();
        });

        volumeSlider = CreateSlider(settingsPanel, "音量", 0.42f, 0f, 1f, volume, v =>
        {
            volume = v;
            SaveSettings();
            PlayPreviewSound();
        });

        brightnessSlider = CreateSlider(settingsPanel, "涟漪", 0.22f, 0f, 1f, rippleBrightness, v =>
        {
            rippleBrightness = v;
            SaveSettings();
            SpawnRipple(new Vector2(Screen.width / 2f, Screen.height / 2f));
        });

        var hintGo = new GameObject("Hint");
        hintGo.transform.SetParent(canvas.transform, false);
        hintText = hintGo.AddComponent<Text>();
        hintText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (hintText.font == null) hintText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        hintText.fontSize = 32;
        hintText.alignment = TextAnchor.MiddleCenter;
        hintText.color = new Color(1, 1, 1, 0.3f);
        hintText.text = "双击下方空白区域开始";
        var hintRt = hintText.rectTransform;
        hintRt.anchorMin = new Vector2(0, 0.2f);
        hintRt.anchorMax = new Vector2(1, 0.27f);
        hintRt.offsetMin = hintRt.offsetMax = Vector2.zero;
    }

    void CreateSoundSelector(GameObject parent, float yAnchor)
    {
        var selector = new GameObject("SoundSelector");
        selector.transform.SetParent(parent.transform, false);
        var rt = selector.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, yAnchor - 0.12f);
        rt.anchorMax = new Vector2(0.95f, yAnchor + 0.05f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(selector.transform, false);
        var labelText = labelGo.AddComponent<Text>();
        labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (labelText.font == null) labelText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        labelText.fontSize = 28;
        labelText.text = "音效";
        labelText.color = Color.white;
        labelText.alignment = TextAnchor.MiddleLeft;
        var labelRt = labelText.rectTransform;
        labelRt.anchorMin = new Vector2(0, 0.5f);
        labelRt.anchorMax = new Vector2(0.15f, 1f);
        labelRt.offsetMin = labelRt.offsetMax = Vector2.zero;

        soundLabels = new Text[presets.Length];
        float btnWidth = 0.8f / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            int idx = i;
            var btnGo = new GameObject(presets[i].name);
            btnGo.transform.SetParent(selector.transform, false);

            var btnImg = btnGo.AddComponent<Image>();
            btnImg.color = (i == selectedSound) ? new Color(1f, 0.9f, 0.2f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);

            var btnRt = btnImg.rectTransform;
            btnRt.anchorMin = new Vector2(0.18f + i * btnWidth, 0.5f);
            btnRt.anchorMax = new Vector2(0.18f + (i + 1) * btnWidth - 0.01f, 1f);
            btnRt.offsetMin = btnRt.offsetMax = Vector2.zero;

            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.AddListener(() => SelectSound(idx));

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(btnGo.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null) text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 22;
            text.text = presets[i].name;
            text.color = (i == selectedSound) ? Color.black : Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = textRt.offsetMax = Vector2.zero;

            soundLabels[i] = text;
        }
    }

    void SelectSound(int index)
    {
        selectedSound = index;
        SaveSettings();

        for (int i = 0; i < presets.Length; i++)
        {
            var btnImg = soundLabels[i].transform.parent.GetComponent<Image>();
            btnImg.color = (i == selectedSound) ? new Color(1f, 0.9f, 0.2f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
            soundLabels[i].color = (i == selectedSound) ? Color.black : Color.white;
        }

        PlayPreviewSound();
    }

    Slider CreateSlider(GameObject parent, string label, float yAnchor, float min, float max, float value, System.Action<float> onChange)
    {
        var go = new GameObject(label);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, yAnchor - 0.08f);
        rt.anchorMax = new Vector2(0.95f, yAnchor + 0.08f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelText = labelGo.AddComponent<Text>();
        labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (labelText.font == null) labelText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        labelText.fontSize = 28;
        labelText.text = label;
        labelText.color = Color.white;
        labelText.alignment = TextAnchor.MiddleLeft;
        var labelRt = labelText.rectTransform;
        labelRt.anchorMin = new Vector2(0, 0);
        labelRt.anchorMax = new Vector2(0.15f, 1);
        labelRt.offsetMin = labelRt.offsetMax = Vector2.zero;

        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(go.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.3f, 0.3f, 0.3f, 1f);
        var bgRt = bgImg.rectTransform;
        bgRt.anchorMin = new Vector2(0.18f, 0.35f);
        bgRt.anchorMax = new Vector2(1f, 0.65f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var fillAreaGo = new GameObject("Fill Area");
        fillAreaGo.transform.SetParent(go.transform, false);
        var fillAreaRt = fillAreaGo.AddComponent<RectTransform>();
        fillAreaRt.anchorMin = new Vector2(0.18f, 0.35f);
        fillAreaRt.anchorMax = new Vector2(1f, 0.65f);
        fillAreaRt.offsetMin = fillAreaRt.offsetMax = Vector2.zero;

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(fillAreaGo.transform, false);
        var fillImg = fillGo.AddComponent<Image>();
        fillImg.color = new Color(1f, 0.9f, 0.2f, 1f);
        var fillRt = fillImg.rectTransform;
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;

        var handleAreaGo = new GameObject("Handle Slide Area");
        handleAreaGo.transform.SetParent(go.transform, false);
        var handleAreaRt = handleAreaGo.AddComponent<RectTransform>();
        handleAreaRt.anchorMin = new Vector2(0.18f, 0f);
        handleAreaRt.anchorMax = new Vector2(1f, 1f);
        handleAreaRt.offsetMin = handleAreaRt.offsetMax = Vector2.zero;

        var handleGo = new GameObject("Handle");
        handleGo.transform.SetParent(handleAreaGo.transform, false);
        var handleImg = handleGo.AddComponent<Image>();
        handleImg.color = Color.white;
        var handleRt = handleImg.rectTransform;
        handleRt.sizeDelta = new Vector2(30, 50);

        var slider = go.AddComponent<Slider>();
        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        slider.onValueChanged.AddListener(v => onChange(v));

        return slider;
    }

    void Update()
    {
        HandleInput();
        UpdateRipples();
    }

    void HandleInput()
    {
        if (state == State.Settings)
            HandleSettingsInput();
        else
            HandlePlayingInput();
    }

    void HandleSettingsInput()
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            if (touch.phase == TouchPhase.Began)
            {
                if (!IsTouchOnSettings(touch.position))
                {
                    if (Time.time - lastTapTime < DoubleTapThreshold)
                    {
                        StartGame();
                        return;
                    }
                    lastTapTime = Time.time;
                    OnTapPress(touch.fingerId, touch.position);
                }
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                OnTapRelease(touch.fingerId);
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (!IsTouchOnSettings(Input.mousePosition))
            {
                if (Time.time - lastTapTime < DoubleTapThreshold)
                {
                    StartGame();
                    return;
                }
                lastTapTime = Time.time;
                OnTapPress(-1, Input.mousePosition);
            }
        }
        if (Input.GetMouseButtonUp(0))
        {
            OnTapRelease(-1);
        }
    }

    void HandlePlayingInput()
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            if (touch.phase == TouchPhase.Began)
            {
                OnTapPress(touch.fingerId, touch.position);
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                OnTapRelease(touch.fingerId);
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            OnTapPress(-1, Input.mousePosition);
        }
        if (Input.GetMouseButtonUp(0))
        {
            OnTapRelease(-1);
        }
    }

    bool IsTouchOnSettings(Vector2 screenPos)
    {
        if (settingsPanel == null || !settingsPanel.activeSelf) return false;

        RectTransform panelRt = settingsPanel.GetComponent<RectTransform>();
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform, screenPos, null, out localPoint);

        Vector3[] corners = new Vector3[4];
        panelRt.GetWorldCorners(corners);

        float minX = corners[0].x, maxX = corners[2].x;
        float minY = corners[0].y, maxY = corners[2].y;

        Vector3 worldPos = canvas.transform.TransformPoint(localPoint);
        return worldPos.x >= minX && worldPos.x <= maxX && worldPos.y >= minY && worldPos.y <= maxY;
    }

    void StartGame()
    {
        state = State.Playing;
        settingsPanel.SetActive(false);
        hintText.gameObject.SetActive(false);
        StopAllTouchSounds();
    }

    void OnTapPress(int id, Vector2 pos)
    {
        SpawnRipple(pos);

        // 获取或创建独立的音效实例
        if (soundPool.Count > 0)
        {
            var ts = soundPool.Dequeue();
            ts.id = id;
            ts.startTime = Time.time;
            ts.isPlaying = true;

            if (delayMs <= 0)
            {
                StartSound(ts);
            }
            else
            {
                StartCoroutine(DelayedStartSound(ts, delayMs / 1000f));
            }

            touchSounds.Add(ts);
        }
    }

    void OnTapRelease(int id)
    {
        var ts = touchSounds.Find(t => t.id == id);
        if (ts != null)
        {
            float elapsed = Time.time - ts.startTime;
            if (elapsed < MinPlayTime)
            {
                if (ts.stopCoroutine != null)
                    StopCoroutine(ts.stopCoroutine);
                ts.stopCoroutine = StartCoroutine(DelayedStopSound(ts, MinPlayTime - elapsed));
            }
            else
            {
                StopSound(ts);
            }
        }
    }

    System.Collections.IEnumerator DelayedStartSound(TouchSound ts, float delaySec)
    {
        yield return new WaitForSeconds(delaySec);
        if (ts.isPlaying)
        {
            StartSound(ts);
        }
    }

    System.Collections.IEnumerator DelayedStopSound(TouchSound ts, float delaySec)
    {
        yield return new WaitForSeconds(delaySec);
        StopSound(ts);
    }

    void StartSound(TouchSound ts)
    {
        var preset = presets[selectedSound];
        ts.attackSource.volume = volume;
        ts.attackSource.PlayOneShot(preset.attackClip);
        ts.sustainSource.clip = preset.sustainClip;
        ts.sustainSource.volume = volume;
        ts.sustainSource.Play();
    }

    void StopSound(TouchSound ts)
    {
        ts.isPlaying = false;
        ts.attackSource.Stop();
        ts.sustainSource.Stop();
        touchSounds.Remove(ts);
        soundPool.Enqueue(ts);
    }

    void StopAllTouchSounds()
    {
        foreach (var ts in touchSounds)
        {
            ts.isPlaying = false;
            ts.attackSource.Stop();
            ts.sustainSource.Stop();
            soundPool.Enqueue(ts);
        }
        touchSounds.Clear();
    }

    // 处理应用暂停（锁屏、切后台、来电等）
    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            StopAllTouchSounds();
        }
    }

    // 处理应用失去焦点（弹窗、通知等）
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            StopAllTouchSounds();
        }
    }

    void PlayPreviewSound()
    {
        if (soundPool.Count > 0)
        {
            var ts = soundPool.Dequeue();
            var preset = presets[selectedSound];
            ts.attackSource.volume = volume;
            ts.attackSource.PlayOneShot(preset.attackClip);
            ts.sustainSource.clip = preset.sustainClip;
            ts.sustainSource.volume = volume;
            ts.sustainSource.Play();
            StartCoroutine(StopPreviewAfter(ts, 0.2f));
        }
    }

    System.Collections.IEnumerator StopPreviewAfter(TouchSound ts, float sec)
    {
        yield return new WaitForSeconds(sec);
        ts.sustainSource.Stop();
        soundPool.Enqueue(ts);
    }

    void SpawnRipple(Vector2 screenPos)
    {
        var go = new GameObject("Ripple");

        // 将屏幕坐标转换为世界坐标（z=10，在相机前面）
        Vector3 worldPos = Camera.main.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));
        go.transform.position = worldPos;

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.sortingOrder = 100; // 确保在最前面

        // 从彩虹七色中随机选择，应用亮度
        Color baseColor = RainbowColors[Random.Range(0, RainbowColors.Length)];
        float brightness = rippleBrightness;
        Color finalColor = new Color(baseColor.r * brightness, baseColor.g * brightness, baseColor.b * brightness, 1f);

        // 设置材质和颜色
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = finalColor;
        lr.endColor = finalColor;

        // 固定线宽（世界单位）
        float lineWidth = 0.05f;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;

        // 初始半径
        float baseRadius = 0.5f;
        SetCirclePositions(lr, baseRadius, 64);

        ripples.Add(new RippleInfo
        {
            go = go,
            lr = lr,
            birth = Time.time,
            baseRadius = baseRadius
        });
    }

    void SetCirclePositions(LineRenderer lr, float radius, int segments)
    {
        lr.positionCount = segments;
        float angleStep = 360f / segments;
        for (int i = 0; i < segments; i++)
        {
            float angle = Mathf.Deg2Rad * (i * angleStep);
            float x = Mathf.Cos(angle) * radius;
            float y = Mathf.Sin(angle) * radius;
            lr.SetPosition(i, new Vector3(x, y, 0));
        }
    }

    void UpdateRipples()
    {
        // 计算需要扩散到全屏的目标半径（世界单位）
        // 相机在z=0，涟漪在z=10，计算该距离下可见的最大半径
        float distance = 10f;
        float maxRadius = distance * Mathf.Tan(Camera.main.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.5f;

        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            var r = ripples[i];
            float age = Time.time - r.birth;
            float duration = 2.5f; // 扩散时间

            if (age > duration)
            {
                Destroy(r.go);
                ripples.RemoveAt(i);
                continue;
            }

            float t = age / duration;
            // 更新半径，圆环粗细保持不变
            float newRadius = r.baseRadius + t * maxRadius;
            SetCirclePositions(r.lr, newRadius, 64);
        }
    }

    struct RippleInfo
    {
        public GameObject go;
        public LineRenderer lr;
        public float birth;
        public float baseRadius;
    }
}
