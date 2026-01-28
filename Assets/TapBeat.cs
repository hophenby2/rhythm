using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏节拍器
/// - 音效长度跟按住时长有关
/// - 涟漪为由内而外扩散的圆环
/// - 设置界面：偏移量、音量、涟漪亮度
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

    // 按压追踪
    Dictionary<int, float> touchStartTimes = new Dictionary<int, float>();
    float mouseDownTime;
    bool mouseDown;

    // 双击检测
    float lastTapTime;
    const float DoubleTapThreshold = 0.3f;

    // 音频
    AudioSource audioSource;

    // UI
    Canvas canvas;
    GameObject settingsPanel;
    Slider offsetSlider, volumeSlider, brightnessSlider;
    Text hintText;
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

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        LoadSettings();
        CreateRingSprite();
        BuildUI();
    }

    void LoadSettings()
    {
        delayMs = PlayerPrefs.GetFloat("TapBeat_Delay", 0f);
        volume = PlayerPrefs.GetFloat("TapBeat_Volume", 0.8f);
        rippleBrightness = PlayerPrefs.GetFloat("TapBeat_Brightness", 0.8f);
    }

    void SaveSettings()
    {
        PlayerPrefs.SetFloat("TapBeat_Delay", delayMs);
        PlayerPrefs.SetFloat("TapBeat_Volume", volume);
        PlayerPrefs.SetFloat("TapBeat_Brightness", rippleBrightness);
        PlayerPrefs.Save();
    }

    void CreateRingSprite()
    {
        // 创建圆环纹理
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
                    // 圆环内，边缘抗锯齿
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
        // EventSystem（滑动条必需）
        if (FindObjectOfType<EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        // Canvas
        var canvasGo = new GameObject("Canvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGo.AddComponent<GraphicRaycaster>();

        // 设置面板
        settingsPanel = new GameObject("Settings");
        settingsPanel.transform.SetParent(canvas.transform, false);
        var panelRt = settingsPanel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.1f, 0.7f);
        panelRt.anchorMax = new Vector2(0.9f, 0.95f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        var panelImg = settingsPanel.AddComponent<Image>();
        panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);

        // 滑动条
        offsetSlider = CreateSlider(settingsPanel, "延迟", 0.75f, 0f, 200f, delayMs, v =>
        {
            delayMs = v;
            SaveSettings();
            PlayTapSound(0.15f);
        });

        volumeSlider = CreateSlider(settingsPanel, "音量", 0.45f, 0f, 1f, volume, v =>
        {
            volume = v;
            SaveSettings();
            PlayTapSound(0.15f);
        });

        brightnessSlider = CreateSlider(settingsPanel, "涟漪", 0.15f, 0f, 1f, rippleBrightness, v =>
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
        hintRt.anchorMin = new Vector2(0, 0.55f);
        hintRt.anchorMax = new Vector2(1, 0.65f);
        hintRt.offsetMin = hintRt.offsetMax = Vector2.zero;
    }

    Slider CreateSlider(GameObject parent, string label, float yAnchor, float min, float max, float value, System.Action<float> onChange)
    {
        var go = new GameObject(label);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, yAnchor - 0.1f);
        rt.anchorMax = new Vector2(0.95f, yAnchor + 0.1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // 标签
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
        labelRt.anchorMax = new Vector2(0.2f, 1);
        labelRt.offsetMin = labelRt.offsetMax = Vector2.zero;

        // Slider 背景
        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(go.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.3f, 0.3f, 0.3f, 1f);
        var bgRt = bgImg.rectTransform;
        bgRt.anchorMin = new Vector2(0.22f, 0.35f);
        bgRt.anchorMax = new Vector2(1f, 0.65f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        // Fill 区域
        var fillAreaGo = new GameObject("Fill Area");
        fillAreaGo.transform.SetParent(go.transform, false);
        var fillAreaRt = fillAreaGo.AddComponent<RectTransform>();
        fillAreaRt.anchorMin = new Vector2(0.22f, 0.35f);
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

        // Handle 区域
        var handleAreaGo = new GameObject("Handle Slide Area");
        handleAreaGo.transform.SetParent(go.transform, false);
        var handleAreaRt = handleAreaGo.AddComponent<RectTransform>();
        handleAreaRt.anchorMin = new Vector2(0.22f, 0f);
        handleAreaRt.anchorMax = new Vector2(1f, 1f);
        handleAreaRt.offsetMin = handleAreaRt.offsetMax = Vector2.zero;

        var handleGo = new GameObject("Handle");
        handleGo.transform.SetParent(handleAreaGo.transform, false);
        var handleImg = handleGo.AddComponent<Image>();
        handleImg.color = Color.white;
        var handleRt = handleImg.rectTransform;
        handleRt.sizeDelta = new Vector2(30, 50);

        // Slider 组件
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
        // 触摸
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            if (touch.phase == TouchPhase.Began)
            {
                if (!IsTouchOnSettings(touch.position))
                {
                    touchStartTimes[touch.fingerId] = Time.time;

                    // 双击检测
                    if (Time.time - lastTapTime < DoubleTapThreshold)
                    {
                        StartGame();
                        return;
                    }
                    lastTapTime = Time.time;
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

        // 鼠标
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
        // 触摸
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            if (touch.phase == TouchPhase.Began)
            {
                touchStartTimes[touch.fingerId] = Time.time;
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

        // 鼠标
        if (Input.GetMouseButtonDown(0))
        {
            mouseDown = true;
            mouseDownTime = Time.time;
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

        // 转换到面板的本地坐标检测
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
    }

    void OnTapRelease(Vector2 pos, float holdTime)
    {
        // 涟漪立即显示
        SpawnRipple(pos, holdTime);

        // 音效延迟播放
        if (delayMs <= 0)
        {
            PlayTapSound(holdTime);
        }
        else
        {
            StartCoroutine(DelayedSound(holdTime, delayMs / 1000f));
        }
    }

    System.Collections.IEnumerator DelayedSound(float holdTime, float delaySec)
    {
        yield return new WaitForSeconds(delaySec);
        PlayTapSound(holdTime);
    }

    void PlayTapSound(float holdTime)
    {
        // 音效时长：0.05s ~ 0.3s，由按住时长决定
        float duration = Mathf.Clamp(0.05f + holdTime * 0.5f, 0.05f, 0.3f);

        int sr = AudioSettings.outputSampleRate;
        int n = Mathf.CeilToInt(duration * sr);
        var clip = AudioClip.Create("tap", n, 1, sr, false);
        float[] data = new float[n];

        float baseFreq = 1200f;
        float harmFreq = 1800f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Pow(0.001f, t / duration);
            data[i] = (Mathf.Sin(2f * Mathf.PI * baseFreq * t) * 0.3f +
                       Mathf.Sin(2f * Mathf.PI * harmFreq * t) * 0.15f) * env;
        }
        clip.SetData(data, 0);

        audioSource.PlayOneShot(clip, volume);
    }

    void SpawnRipple(Vector2 screenPos, float holdTime)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.sprite = ringSprite;
        img.raycastTarget = false;

        // 初始颜色（黄色，带亮度）
        float brightness = rippleBrightness;
        img.color = new Color(brightness, brightness * 0.9f, brightness * 0.2f, brightness * 0.8f);

        // 初始大小（由按住时长决定，0.05s~0.5s -> 80~200）
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
            // 由内而外扩散
            float scale = 1f + t * 2.5f;
            r.go.transform.localScale = Vector3.one * scale;

            // 渐隐
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
