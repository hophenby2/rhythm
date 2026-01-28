using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 纯黑屏节拍音游 — 分层节拍检测架构
///
/// 第一层：Tap-tempo BPM 锁定（零依赖，用户点几下建立节拍网格）
/// 第二层：麦克风 onset detection（自动修正漂移）
/// 第三层：Android AudioPlaybackCapture（干净数字信号）
///
/// 根据点击准确度播放不同反馈音效：Perfect / Good / OK / Miss
/// </summary>
public class TapBeat : MonoBehaviour
{
    // ======== 状态机 ========
    enum Phase { FreeTap, Calibrating, Locked }
    Phase phase = Phase.FreeTap;

    // ======== 节拍网格 ========
    float bpm;
    float beatInterval;   // 秒/拍
    float beatPhase;      // 节拍网格锚点时间（某一拍的精确时刻）
    int beatsSinceLock;

    // ======== 校准 ========
    const int CalibrateTaps = 6;          // 需要几拍进入锁定
    const float CalibrationTimeout = 3f;  // 超时重置
    readonly List<float> calTaps = new List<float>();

    // ======== 判定窗口（秒） ========
    const float PerfectWindow = 0.04f;  // ±40ms
    const float GoodWindow = 0.08f;     // ±80ms
    const float OkWindow = 0.14f;       // ±140ms
    // >140ms = Miss

    // ======== 漂移修正 ========
    const float DriftCorrectionRate = 0.15f; // 每次 tap 修正 15% 的相位误差
    const float BpmCorrectionRate = 0.05f;   // BPM 微调幅度

    // ======== 连击与分数 ========
    int combo;
    int maxCombo;
    int totalTaps;
    int[] judgeCounts = new int[4]; // Perfect, Good, OK, Miss

    // ======== 音频 ========
    AudioSource audioSource;
    Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

    // ======== UI ========
    Canvas canvas;
    Text hudText;
    Text judgeText;
    Text comboText;
    Text phaseText;      // 顶部状态
    float hudFadeTimer;
    float judgeFadeTimer;

    // ======== 涟漪 ========
    readonly List<RippleInfo> ripples = new List<RippleInfo>();

    // ======== 节拍引导脉冲 ========
    Image pulseOverlay;

    // ======== 麦克风 onset 检测（第二层） ========
    MicOnsetDetector micDetector;

    // ======== Android 系统音频捕获（第三层） ========
    AndroidAudioCaptureProxy androidCapture;
    bool useSystemAudio = true; // 优先使用系统音频（更准确）

    // ======== 双指 ========
    float lastSwitchTime;
    bool useVibration = true;

    // ==============================================
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

        BuildUI();
        SynthAllClips();

        // 第二层：麦克风检测（可选）
        micDetector = gameObject.AddComponent<MicOnsetDetector>();
        micDetector.OnOnsetDetected += OnMicOnset;

        // 第三层：Android 系统音频捕获（Android 10+）
        androidCapture = gameObject.AddComponent<AndroidAudioCaptureProxy>();
        androidCapture.OnOnsetDetected += OnSystemAudioOnset;
        androidCapture.OnPermissionResult += OnAndroidCapturePermission;

        // 尝试启动系统音频捕获
        if (androidCapture.IsSupported && useSystemAudio)
        {
            // 延迟请求，等 UI 准备好
            Invoke(nameof(TryStartSystemAudioCapture), 0.5f);
        }
    }

    void TryStartSystemAudioCapture()
    {
        if (androidCapture.IsSupported && !androidCapture.IsRunning)
        {
            ShowHUD("正在请求系统音频权限...");
            androidCapture.RequestAndStart();
        }
    }

    void OnAndroidCapturePermission(string result)
    {
        if (result == "granted")
        {
            ShowHUD("系统音频捕获已启用 - 精准检测模式");
            // 系统音频更准，降低麦克风权重
            micDetector.SetEnabled(false);
        }
        else if (result == "denied")
        {
            ShowHUD("已拒绝系统音频权限 - 使用麦克风模式");
        }
        else if (result == "unsupported")
        {
            ShowHUD("点击屏幕开始打拍子");
        }
    }

    // ======== UI 构建 ========
    void BuildUI()
    {
        var canvasGo = new GameObject("Canvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        canvasGo.AddComponent<GraphicRaycaster>();

        // 脉冲遮罩（锁定后跟拍闪烁）
        var pulseGo = new GameObject("Pulse");
        pulseGo.transform.SetParent(canvasGo.transform, false);
        pulseOverlay = pulseGo.AddComponent<Image>();
        pulseOverlay.color = Color.clear;
        pulseOverlay.raycastTarget = false;
        var prt = pulseOverlay.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.sizeDelta = Vector2.zero;

        // 顶部状态
        phaseText = MakeText(canvasGo, "Phase", 24,
            new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
            new Vector2(0, -12), new Vector2(0, 40));
        phaseText.color = new Color(1, 1, 1, 0.2f);

        // 判定文字（屏幕中央）
        judgeText = MakeText(canvasGo, "Judge", 72,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0, 40), new Vector2(600, 120));
        judgeText.color = Color.clear;

        // 连击数（判定文字下方）
        comboText = MakeText(canvasGo, "Combo", 40,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0, -40), new Vector2(400, 60));
        comboText.color = Color.clear;

        // 底部 HUD
        hudText = MakeText(canvasGo, "HUD", 26,
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0),
            new Vector2(0, 16), new Vector2(0, 50));
        hudText.color = new Color(1, 1, 1, 0.2f);
        hudText.text = "点击屏幕开始打拍子";
    }

    Text MakeText(GameObject parent, string name, int fontSize,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPos, Vector2 sizeDelta)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (t.font == null) t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.raycastTarget = false;
        var rt = t.rectTransform;
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;
        return t;
    }

    // ======== 音频合成 ========
    void SynthAllClips()
    {
        int sr = AudioSettings.outputSampleRate;
        // Perfect: 清脆高音 叮
        clips["perfect"] = SynthClip(sr, "perfect", 0.12f, (i, n, s) =>
        {
            float t = (float)i / s;
            float env = Mathf.Pow(0.001f, t / 0.12f);
            return Mathf.Sin(2f * Mathf.PI * 1200f * t) * 0.3f * env
                 + Mathf.Sin(2f * Mathf.PI * 1800f * t) * 0.15f * env;
        });
        // Good: 中音 哒
        clips["good"] = SynthClip(sr, "good", 0.10f, (i, n, s) =>
        {
            float t = (float)i / s;
            float env = Mathf.Pow(0.001f, t / 0.10f);
            return Mathf.Sin(2f * Mathf.PI * 660f * t) * 0.25f * env;
        });
        // OK: 低沉闷音 咚
        clips["ok"] = SynthClip(sr, "ok", 0.12f, (i, n, s) =>
        {
            float t = (float)i / s;
            float freq = Mathf.Lerp(220f, 110f, t / 0.12f);
            float env = Mathf.Pow(0.001f, t / 0.12f);
            return Mathf.Sin(2f * Mathf.PI * freq * t) * 0.25f * env;
        });
        // Miss: 噪声嗡 buzz
        clips["miss"] = SynthClip(sr, "miss", 0.15f, (i, n, s) =>
        {
            float t = (float)i / s;
            float env = Mathf.Pow(0.001f, t / 0.15f);
            float buzz = Mathf.Sin(2f * Mathf.PI * 80f * t);
            float noise = (float)(new System.Random(i).NextDouble() * 2 - 1);
            return (buzz * 0.15f + noise * 0.06f) * env;
        });
        // FreeTap: 通用打击音（校准阶段用）
        clips["tap"] = SynthClip(sr, "tap", 0.08f, (i, n, s) =>
        {
            float t = (float)i / s;
            float env = Mathf.Pow(0.001f, t / 0.08f);
            return Mathf.Sin(2f * Mathf.PI * 800f * t) * 0.2f * env;
        });
        // 节拍引导提示音（很轻）
        clips["guide"] = SynthClip(sr, "guide", 0.04f, (i, n, s) =>
        {
            float t = (float)i / s;
            float env = Mathf.Pow(0.001f, t / 0.04f);
            return Mathf.Sin(2f * Mathf.PI * 1000f * t) * 0.06f * env;
        });
    }

    delegate float SampleFunc(int index, int totalSamples, int sampleRate);

    AudioClip SynthClip(int sr, string name, float dur, SampleFunc func)
    {
        int n = Mathf.CeilToInt(dur * sr);
        var clip = AudioClip.Create(name, n, 1, sr, false);
        float[] data = new float[n];
        for (int i = 0; i < n; i++)
            data[i] = func(i, n, sr);
        clip.SetData(data, 0);
        return clip;
    }

    // ======== 主循环 ========
    void Update()
    {
        HandleInput();
        UpdateBeatPulse();
        UpdateRipples();
        UpdateFades();
    }

    // ======== 输入 ========
    void HandleInput()
    {
        // 触摸
        if (Input.touchCount > 0)
        {
            // 三指长按：重置
            if (Input.touchCount >= 3)
            {
                ResetToFreeTap();
                return;
            }
            // 双指：切换振动
            if (Input.touchCount >= 2)
            {
                if (Time.unscaledTime - lastSwitchTime > 0.5f)
                {
                    lastSwitchTime = Time.unscaledTime;
                    useVibration = !useVibration;
                    ShowHUD("振动: " + (useVibration ? "开" : "关"));
                }
                return;
            }

            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
                OnTap(touch.position);
        }
        else if (Input.GetMouseButtonDown(0))
        {
            OnTap(Input.mousePosition);
        }
    }

    void OnTap(Vector2 screenPos)
    {
        float now = Time.unscaledTime;

        switch (phase)
        {
            case Phase.FreeTap:
                // 第一下点击：进入校准
                calTaps.Clear();
                calTaps.Add(now);
                phase = Phase.Calibrating;
                audioSource.PlayOneShot(clips["tap"]);
                ShowPhase("校准中 1/" + CalibrateTaps);
                ShowHUD("跟着节奏继续点击...");
                break;

            case Phase.Calibrating:
                OnCalibrationTap(now);
                break;

            case Phase.Locked:
                OnLockedTap(now, screenPos);
                break;
        }

        SpawnRipple(screenPos, phase == Phase.Locked ? GetLastJudgeColor() : new Color(1, 1, 1, 0.3f));
    }

    // ======== 校准阶段 ========
    void OnCalibrationTap(float now)
    {
        // 超时检测
        if (calTaps.Count > 0 && now - calTaps[calTaps.Count - 1] > CalibrationTimeout)
        {
            ResetToFreeTap();
            OnTap(Vector2.zero); // 重新开始
            return;
        }

        calTaps.Add(now);
        audioSource.PlayOneShot(clips["tap"]);
        ShowPhase("校准中 " + calTaps.Count + "/" + CalibrateTaps);

        if (calTaps.Count >= CalibrateTaps)
            LockBPM();
    }

    void LockBPM()
    {
        // 用中位数间隔计算 BPM（抗异常值）
        List<float> intervals = new List<float>();
        for (int i = 1; i < calTaps.Count; i++)
            intervals.Add(calTaps[i] - calTaps[i - 1]);
        intervals.Sort();

        // 取中间一半的平均
        int lo = intervals.Count / 4;
        int hi = intervals.Count - lo;
        float sum = 0;
        for (int i = lo; i < hi; i++) sum += intervals[i];
        beatInterval = sum / (hi - lo);

        // 量化到整数 BPM
        bpm = Mathf.Round(60f / beatInterval);
        beatInterval = 60f / bpm;

        // 相位锚定到最后一拍
        beatPhase = calTaps[calTaps.Count - 1];
        beatsSinceLock = 0;
        combo = 0;
        maxCombo = 0;
        totalTaps = 0;
        judgeCounts = new int[4];

        phase = Phase.Locked;
        ShowPhase("BPM " + bpm + " | 已锁定");
        ShowHUD("跟着节拍点击！三指重置");
    }

    // ======== 锁定阶段 — 判定 ========
    Color lastJudgeColor = Color.white;

    void OnLockedTap(float now, Vector2 screenPos)
    {
        totalTaps++;

        // 找到离 now 最近的节拍时刻
        float timeSincePhase = now - beatPhase;
        float beatPos = timeSincePhase / beatInterval;
        int nearestBeat = Mathf.RoundToInt(beatPos);
        float nearestBeatTime = beatPhase + nearestBeat * beatInterval;
        float error = now - nearestBeatTime; // 正=晚，负=早
        float absError = Mathf.Abs(error);

        // 判定
        string judge;
        Color color;
        int judgeIdx;

        if (absError <= PerfectWindow)
        {
            judge = "Perfect"; color = new Color(1f, 0.9f, 0.2f); judgeIdx = 0;
        }
        else if (absError <= GoodWindow)
        {
            judge = "Good"; color = new Color(0.2f, 1f, 0.5f); judgeIdx = 1;
        }
        else if (absError <= OkWindow)
        {
            judge = "OK"; color = new Color(0.3f, 0.7f, 1f); judgeIdx = 2;
        }
        else
        {
            judge = "Miss"; color = new Color(1f, 0.3f, 0.3f); judgeIdx = 3;
        }

        judgeCounts[judgeIdx]++;
        lastJudgeColor = color;

        // 连击
        if (judgeIdx <= 2) // Perfect/Good/OK 维持 combo
        {
            combo++;
            if (combo > maxCombo) maxCombo = combo;
        }
        else
        {
            combo = 0;
        }

        // 播放对应音效
        string clipKey = judgeIdx == 0 ? "perfect" : judgeIdx == 1 ? "good" : judgeIdx == 2 ? "ok" : "miss";
        audioSource.PlayOneShot(clips[clipKey]);

        // 振动反馈
        if (useVibration && judgeIdx <= 1)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }

        // 漂移修正（第一层自适应）
        ApplyDriftCorrection(error, now);

        // 显示
        string earlyLate = absError < 0.01f ? "" : (error > 0 ? " 慢" : " 快");
        int ms = Mathf.RoundToInt(absError * 1000f);
        ShowJudge(judge + earlyLate + " " + ms + "ms", color);
        if (combo > 1)
            ShowCombo(combo);
        ShowPhase("BPM " + Mathf.RoundToInt(bpm) + " | combo " + combo);
    }

    Color GetLastJudgeColor() { return lastJudgeColor; }

    // ======== 漂移修正 ========
    readonly List<float> recentErrors = new List<float>();

    void ApplyDriftCorrection(float error, float now)
    {
        // 相位微调：向实际点击时间靠拢
        beatPhase += error * DriftCorrectionRate;

        // BPM 微调：收集最近的误差趋势
        recentErrors.Add(error);
        if (recentErrors.Count > 12) recentErrors.RemoveAt(0);

        if (recentErrors.Count >= 6)
        {
            // 如果持续偏晚（正误差），说明实际 BPM 比锁定值略快
            float avgError = 0;
            for (int i = 0; i < recentErrors.Count; i++) avgError += recentErrors[i];
            avgError /= recentErrors.Count;

            if (Mathf.Abs(avgError) > 0.015f) // 超过 15ms 平均偏差才调整
            {
                // avgError > 0 表示总是晚 -> 实际间隔比预测短 -> 缩短 beatInterval
                beatInterval -= avgError * BpmCorrectionRate;
                beatInterval = Mathf.Clamp(beatInterval, 0.2f, 2f); // 30-300 BPM
                bpm = 60f / beatInterval;
                recentErrors.Clear();
            }
        }
    }

    // ======== 外部 onset 回调（第二/三层）========
    void OnMicOnset(float onsetTime)
    {
        // 麦克风 onset - 噪声较多，保守修正
        ApplyExternalOnsetCorrection(onsetTime, 0.06f);
    }

    void OnSystemAudioOnset(float onsetTime)
    {
        // 系统音频 onset - 信号干净，可以更信任
        ApplyExternalOnsetCorrection(onsetTime, 0.12f);
    }

    void ApplyExternalOnsetCorrection(float onsetTime, float correctionWeight)
    {
        if (phase != Phase.Locked) return;

        // 用外部 onset 做额外的相位修正
        float timeSincePhase = onsetTime - beatPhase;
        float beatPos = timeSincePhase / beatInterval;
        int nearestBeat = Mathf.RoundToInt(beatPos);
        float nearestBeatTime = beatPhase + nearestBeat * beatInterval;
        float error = onsetTime - nearestBeatTime;

        // 只有当误差较小时才信任外部 onset（过滤噪声/异常）
        if (Mathf.Abs(error) < OkWindow)
        {
            beatPhase += error * correctionWeight;
        }
    }

    // ======== 节拍脉冲视觉引导 ========
    void UpdateBeatPulse()
    {
        if (phase != Phase.Locked)
        {
            pulseOverlay.color = Color.clear;
            return;
        }

        float timeSincePhase = Time.unscaledTime - beatPhase;
        float beatPos = timeSincePhase / beatInterval;
        float frac = beatPos - Mathf.Floor(beatPos); // 0~1 在一拍内的位置

        // 在接近下一拍时发出微弱引导光
        // frac 接近 1.0 或 0.0 时 = 接近节拍
        float distToBeat = Mathf.Min(frac, 1f - frac);
        float glow = Mathf.Max(0, 1f - distToBeat * 12f); // 在节拍前后 ~80ms 内发光
        glow = glow * glow * 0.06f; // 非常微弱
        pulseOverlay.color = new Color(1, 1, 1, glow);
    }

    // ======== 重置 ========
    void ResetToFreeTap()
    {
        phase = Phase.FreeTap;
        calTaps.Clear();
        recentErrors.Clear();
        combo = 0;
        ShowPhase("");
        ShowHUD("已重置 | 点击重新校准");
        judgeText.color = Color.clear;
        comboText.color = Color.clear;
    }

    // ======== UI 更新 ========
    void ShowPhase(string text)
    {
        phaseText.text = text;
        phaseText.color = new Color(1, 1, 1, 0.25f);
    }

    void ShowHUD(string text)
    {
        hudText.text = text;
        hudText.color = new Color(1, 1, 1, 0.25f);
        hudFadeTimer = 4f;
    }

    void ShowJudge(string text, Color color)
    {
        judgeText.text = text;
        judgeText.color = color;
        judgeFadeTimer = 0.6f;
        judgeText.transform.localScale = Vector3.one * 1.2f;
    }

    void ShowCombo(int c)
    {
        comboText.text = c + " combo";
        comboText.color = new Color(1, 1, 1, 0.5f);
    }

    void UpdateFades()
    {
        // 判定文字淡出 + 缩回
        if (judgeFadeTimer > 0)
        {
            judgeFadeTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(judgeFadeTimer / 0.6f);
            judgeText.color = new Color(judgeText.color.r, judgeText.color.g, judgeText.color.b, t);
            judgeText.transform.localScale = Vector3.one * Mathf.Lerp(1f, 1.2f, t * t);

            if (judgeFadeTimer <= 0.2f)
                comboText.color = new Color(1, 1, 1, Mathf.Max(0, judgeFadeTimer / 0.2f) * 0.5f);
        }

        // HUD 淡出
        if (hudFadeTimer > 0)
        {
            hudFadeTimer -= Time.deltaTime;
            if (hudFadeTimer <= 1f)
                hudText.color = new Color(1, 1, 1, Mathf.Max(0, hudFadeTimer) * 0.25f);
        }
    }

    // ======== 涟漪 ========
    struct RippleInfo
    {
        public GameObject go;
        public Image img;
        public float birth;
        public Color color;
    }

    void SpawnRipple(Vector2 screenPos, Color color)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = new Color(color.r, color.g, color.b, 0.5f);
        img.rectTransform.sizeDelta = new Vector2(30, 30);

        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform, screenPos, null, out localPoint);
        img.rectTransform.anchoredPosition = localPoint;

        ripples.Add(new RippleInfo { go = go, img = img, birth = Time.time, color = color });
    }

    void UpdateRipples()
    {
        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            var r = ripples[i];
            float age = Time.time - r.birth;
            if (age > 0.5f)
            {
                Destroy(r.go);
                ripples.RemoveAt(i);
                continue;
            }
            float t = age / 0.5f;
            r.go.transform.localScale = Vector3.one * (1f + t * 4f);
            r.img.color = new Color(r.color.r, r.color.g, r.color.b, 0.5f * (1f - t));
        }
    }
}
