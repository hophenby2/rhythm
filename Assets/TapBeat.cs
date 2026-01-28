using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏节拍器
/// - 点击时立即播放可延长音效，松手时停止（最小0.1秒）
/// - 多种预设音效可选（攻击音+循环音结构）
/// - 涟漪为由内而外扩散的圆环
/// - 设置界面：音效选择、偏移量、音量、涟漪亮度
/// </summary>
public class TapBeat : MonoBehaviour
{
    // 状态
    enum State { Settings, Playing }
    State state = State.Settings;

    // 设置值
    float delayMs = 0f;        // 0 ~ 200ms 延迟
    float volume = 0.8f;       // 0 ~ 1
    float rippleBrightness = 0.8f; // 0 ~ 1
    int selectedSound = 0;     // 当前选中的音效

    // 按压追踪
    Dictionary<int, float> touchStartTimes = new Dictionary<int, float>();
    float mouseDownTime;
    bool mouseDown;

    // 双击检测
    float lastTapTime;
    const float DoubleTapThreshold = 0.3f;

    // 音频
    AudioSource attackSource;  // 播放攻击音
    AudioSource sustainSource; // 播放循环音
    const float MinPlayTime = 0.1f; // 最小播放时间
    float soundStartTime;
    bool isPlaying;
    int activeInputCount = 0;
    Coroutine minPlayCoroutine;

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
    GameObject soundSelector;
    Text[] soundLabels;
    readonly List<RippleInfo> ripples = new List<RippleInfo>();

    // 涟漪精灵（圆环）
    Sprite ringSprite;

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

        // 两个音频源：攻击音和循环音
        attackSource = gameObject.AddComponent<AudioSource>();
        attackSource.playOnAwake = false;
        attackSource.loop = false;

        sustainSource = gameObject.AddComponent<AudioSource>();
        sustainSource.playOnAwake = false;
        sustainSource.loop = true;

        CreateSoundPresets();
        LoadSettings();
        CreateRingSprite();
        BuildUI();
    }

    void CreateSoundPresets()
    {
        int sr = AudioSettings.outputSampleRate;
        presets = new SoundPreset[6];

        // 1. 电子音 - 明亮的正弦波
        presets[0] = new SoundPreset
        {
            name = "电子",
            attackClip = CreateAttackClip(sr, 1200f, 1800f, 0.05f),
            sustainClip = CreateSustainClip(sr, 1200f, 1800f, 0.1f)
        };

        // 2. 钢琴 - 泛音丰富
        presets[1] = new SoundPreset
        {
            name = "钢琴",
            attackClip = CreatePianoAttack(sr),
            sustainClip = CreatePianoSustain(sr)
        };

        // 3. 铃声 - 高频清脆
        presets[2] = new SoundPreset
        {
            name = "铃声",
            attackClip = CreateBellAttack(sr),
            sustainClip = CreateBellSustain(sr)
        };

        // 4. 低音 - 低沉浑厚
        presets[3] = new SoundPreset
        {
            name = "低音",
            attackClip = CreateAttackClip(sr, 220f, 440f, 0.06f),
            sustainClip = CreateSustainClip(sr, 220f, 330f, 0.15f)
        };

        // 5. 弦乐 - 温暖柔和
        presets[4] = new SoundPreset
        {
            name = "弦乐",
            attackClip = CreateStringAttack(sr),
            sustainClip = CreateStringSustain(sr)
        };

        // 6. 合成 - 电子合成音
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
            float env = Mathf.Pow(0.01f, t / duration); // 快速衰减
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
            // 平滑的持续音，带轻微振幅调制，首尾淡入淡出确保无缝循环
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
        float baseFreq = 523.25f; // C5

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.005f, t / duration);
            // 钢琴有丰富的泛音
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
            // 铃声：高频 + 非谐波泛音
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
        float baseFreq = 440f; // A4

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            // 弦乐：渐入然后保持
            float env = Mathf.Clamp01(t / 0.03f) * Mathf.Pow(0.1f, Mathf.Max(0, t - 0.03f) / duration);
            // 加入轻微颤音
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
            // 合成音：方波 + 锯齿波近似
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
            // 带滤波效果的持续音
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

    void CreateRingSprite()
    {
        int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = size / 2f;
        float outerR = size / 2f - 2;
        float innerR = outerR - 8;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha = 0;
                if (dist >= innerR && dist <= outerR)
                {
                    float edgeInner = Mathf.Clamp01((dist - innerR) / 1.5f);
                    float edgeOuter = Mathf.Clamp01((outerR - dist) / 1.5f);
                    alpha = Mathf.Min(edgeInner, edgeOuter);
                }
                tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }
        }
        tex.Apply();
        ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
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

        // 设置面板（居中显示，增大高度以容纳音效选择）
        settingsPanel = new GameObject("Settings");
        settingsPanel.transform.SetParent(canvas.transform, false);
        var panelRt = settingsPanel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.1f, 0.3f);
        panelRt.anchorMax = new Vector2(0.9f, 0.7f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        var panelImg = settingsPanel.AddComponent<Image>();
        panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);

        // 音效选择器（顶部）
        CreateSoundSelector(settingsPanel, 0.85f);

        // 滑动条
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
            SpawnRipple(new Vector2(Screen.width / 2f, Screen.height / 2f), 0.15f);
        });

        // 提示文字
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
        soundSelector = new GameObject("SoundSelector");
        soundSelector.transform.SetParent(parent.transform, false);
        var rt = soundSelector.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, yAnchor - 0.12f);
        rt.anchorMax = new Vector2(0.95f, yAnchor + 0.05f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // 标签
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(soundSelector.transform, false);
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

        // 音效按钮
        soundLabels = new Text[presets.Length];
        float btnWidth = 0.8f / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            int idx = i;
            var btnGo = new GameObject(presets[i].name);
            btnGo.transform.SetParent(soundSelector.transform, false);

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

        // 更新按钮颜色
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
                    touchStartTimes[touch.fingerId] = Time.time;

                    if (Time.time - lastTapTime < DoubleTapThreshold)
                    {
                        StartGame();
                        return;
                    }
                    lastTapTime = Time.time;
                    OnTapPress(touch.position);
                }
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                if (touchStartTimes.ContainsKey(touch.fingerId))
                {
                    float holdTime = Time.time - touchStartTimes[touch.fingerId];
                    OnTapRelease(touch.position, holdTime);
                    touchStartTimes.Remove(touch.fingerId);
                }
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (!IsTouchOnSettings(Input.mousePosition))
            {
                mouseDown = true;
                mouseDownTime = Time.time;

                if (Time.time - lastTapTime < DoubleTapThreshold)
                {
                    StartGame();
                    return;
                }
                lastTapTime = Time.time;
                OnTapPress(Input.mousePosition);
            }
        }
        if (Input.GetMouseButtonUp(0) && mouseDown)
        {
            mouseDown = false;
            float holdTime = Time.time - mouseDownTime;
            OnTapRelease(Input.mousePosition, holdTime);
        }
    }

    void HandlePlayingInput()
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            if (touch.phase == TouchPhase.Began)
            {
                touchStartTimes[touch.fingerId] = Time.time;
                OnTapPress(touch.position);
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                if (touchStartTimes.ContainsKey(touch.fingerId))
                {
                    float holdTime = Time.time - touchStartTimes[touch.fingerId];
                    OnTapRelease(touch.position, holdTime);
                    touchStartTimes.Remove(touch.fingerId);
                }
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            mouseDown = true;
            mouseDownTime = Time.time;
            OnTapPress(Input.mousePosition);
        }
        if (Input.GetMouseButtonUp(0) && mouseDown)
        {
            mouseDown = false;
            float holdTime = Time.time - mouseDownTime;
            OnTapRelease(Input.mousePosition, holdTime);
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
        touchStartTimes.Clear();
        mouseDown = false;
        activeInputCount = 0;
        StopTapSound();
    }

    void OnTapPress(Vector2 pos)
    {
        SpawnRipple(pos, 0.15f);

        activeInputCount++;
        if (activeInputCount == 1)
        {
            if (delayMs <= 0)
            {
                StartTapSound();
            }
            else
            {
                StartCoroutine(DelayedStartSound(delayMs / 1000f));
            }
        }
    }

    void OnTapRelease(Vector2 pos, float holdTime)
    {
        activeInputCount = Mathf.Max(0, activeInputCount - 1);
        if (activeInputCount == 0)
        {
            // 确保最小播放时间
            if (isPlaying)
            {
                float elapsed = Time.time - soundStartTime;
                if (elapsed < MinPlayTime)
                {
                    // 延迟停止
                    if (minPlayCoroutine != null)
                        StopCoroutine(minPlayCoroutine);
                    minPlayCoroutine = StartCoroutine(DelayedStopSound(MinPlayTime - elapsed));
                }
                else
                {
                    StopTapSound();
                }
            }
        }
    }

    System.Collections.IEnumerator DelayedStartSound(float delaySec)
    {
        yield return new WaitForSeconds(delaySec);
        if (activeInputCount > 0)
        {
            StartTapSound();
        }
    }

    System.Collections.IEnumerator DelayedStopSound(float delaySec)
    {
        yield return new WaitForSeconds(delaySec);
        if (activeInputCount == 0)
        {
            StopTapSound();
        }
    }

    void StartTapSound()
    {
        if (isPlaying) return;
        isPlaying = true;
        soundStartTime = Time.time;

        var preset = presets[selectedSound];

        // 播放攻击音
        attackSource.volume = volume;
        attackSource.PlayOneShot(preset.attackClip);

        // 设置并播放循环音（攻击音结束后会平滑过渡）
        sustainSource.clip = preset.sustainClip;
        sustainSource.volume = volume;
        sustainSource.Play();
    }

    void StopTapSound()
    {
        isPlaying = false;
        attackSource.Stop();
        sustainSource.Stop();
    }

    void PlayPreviewSound()
    {
        // 预览音效：播放攻击音 + 短暂循环音
        var preset = presets[selectedSound];
        attackSource.volume = volume;
        attackSource.PlayOneShot(preset.attackClip);

        sustainSource.clip = preset.sustainClip;
        sustainSource.volume = volume;
        sustainSource.Play();
        StartCoroutine(StopPreviewAfter(0.2f));
    }

    System.Collections.IEnumerator StopPreviewAfter(float sec)
    {
        yield return new WaitForSeconds(sec);
        sustainSource.Stop();
    }

    void SpawnRipple(Vector2 screenPos, float holdTime)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.sprite = ringSprite;
        img.raycastTarget = false;

        float brightness = rippleBrightness;
        img.color = new Color(brightness, brightness * 0.9f, brightness * 0.2f, brightness * 0.8f);

        float baseSize = Mathf.Lerp(80, 200, Mathf.Clamp01(holdTime / 0.5f));
        img.rectTransform.sizeDelta = new Vector2(baseSize, baseSize);

        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform, screenPos, null, out localPoint);
        img.rectTransform.anchoredPosition = localPoint;

        ripples.Add(new RippleInfo
        {
            go = go,
            img = img,
            birth = Time.time,
            baseSize = baseSize,
            brightness = brightness
        });
    }

    void UpdateRipples()
    {
        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            var r = ripples[i];
            float age = Time.time - r.birth;
            float duration = 0.5f;

            if (age > duration)
            {
                Destroy(r.go);
                ripples.RemoveAt(i);
                continue;
            }

            float t = age / duration;
            float scale = 1f + t * 2.5f;
            r.go.transform.localScale = Vector3.one * scale;

            float alpha = r.brightness * 0.8f * (1f - t);
            r.img.color = new Color(r.brightness, r.brightness * 0.9f, r.brightness * 0.2f, alpha);
        }
    }

    struct RippleInfo
    {
        public GameObject go;
        public Image img;
        public float birth;
        public float baseSize;
        public float brightness;
    }
}
