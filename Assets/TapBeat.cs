using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// 纯黑屏节拍音游
///
/// 两种模式：
/// 1. 导入音频模式：加载音频文件，预分析所有鼓点，播放时判定
/// 2. 自由模式：纯点击，只播放反馈音效，无判定
/// </summary>
public class TapBeat : MonoBehaviour
{
    // ======== 模式 ========
    public enum GameMode { Menu, Playing, FreeTap }
    GameMode mode = GameMode.Menu;

    // ======== 音频 ========
    AudioSource musicSource;     // 播放导入的音乐
    AudioSource sfxSource;       // 播放音效
    AudioClip loadedClip;
    List<float> beatTimes = new List<float>();  // 预分析的节拍时间点
    int nextBeatIndex;
    bool isPlaying;

    // ======== 判定 ========
    const float PerfectWindow = 0.045f;  // ±45ms
    const float GoodWindow = 0.090f;     // ±90ms
    const float OkWindow = 0.150f;       // ±150ms
    HashSet<int> hitBeats = new HashSet<int>();  // 已击中的节拍

    // ======== 统计 ========
    int combo;
    int maxCombo;
    int[] judgeCounts = new int[4];  // Perfect, Good, OK, Miss
    int totalBeats;

    // ======== UI ========
    Canvas canvas;
    Text titleText;
    Text hudText;
    Text judgeText;
    Text comboText;
    Text statsText;
    Button importButton;
    Button freeButton;
    Button backButton;
    float judgeFadeTimer;

    // ======== 音效 ========
    Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

    // ======== 涟漪 ========
    readonly List<RippleInfo> ripples = new List<RippleInfo>();

    // ======== 预加载音频路径（可通过 Inspector 设置） ========
    public string preloadAudioPath = "";

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

        // 两个 AudioSource：一个放音乐，一个放音效
        musicSource = gameObject.AddComponent<AudioSource>();
        musicSource.playOnAwake = false;

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;

        SynthClips();
        BuildUI();

        // 如果有预设路径，尝试加载
        if (!string.IsNullOrEmpty(preloadAudioPath))
            TryLoadAudio(preloadAudioPath);
    }

    // ======== UI ========
    void BuildUI()
    {
        var canvasGo = new GameObject("Canvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1080, 1920);
        canvasGo.AddComponent<GraphicRaycaster>();

        // 标题
        titleText = MakeText("Title", 48, new Vector2(0.5f, 0.7f), "Tap Beat");
        titleText.color = new Color(1, 1, 1, 0.8f);

        // 导入按钮
        importButton = MakeButton("Import", new Vector2(0.5f, 0.5f), "导入音频", OnImportClick);

        // 自由模式按钮
        freeButton = MakeButton("Free", new Vector2(0.5f, 0.4f), "自由模式", OnFreeClick);

        // 返回按钮（游戏中）
        backButton = MakeButton("Back", new Vector2(0.1f, 0.95f), "返回", OnBackClick);
        backButton.gameObject.SetActive(false);

        // 判定文字
        judgeText = MakeText("Judge", 72, new Vector2(0.5f, 0.55f), "");
        judgeText.color = Color.clear;

        // Combo
        comboText = MakeText("Combo", 40, new Vector2(0.5f, 0.45f), "");
        comboText.color = Color.clear;

        // HUD
        hudText = MakeText("HUD", 24, new Vector2(0.5f, 0.1f), "");
        hudText.color = new Color(1, 1, 1, 0.3f);

        // 统计
        statsText = MakeText("Stats", 28, new Vector2(0.5f, 0.5f), "");
        statsText.color = Color.clear;
    }

    Text MakeText(string name, int size, Vector2 anchor, string content)
    {
        var go = new GameObject(name);
        go.transform.SetParent(canvas.transform, false);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (t.font == null) t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = content;
        t.raycastTarget = false;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(800, 100);
        return t;
    }

    Button MakeButton(string name, Vector2 anchor, string label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(name);
        go.transform.SetParent(canvas.transform, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(300, 80);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var t = textGo.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (t.font == null) t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = 32;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = label;
        t.color = Color.white;
        var trt = t.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.sizeDelta = Vector2.zero;

        return btn;
    }

    // ======== 音效合成 ========
    void SynthClips()
    {
        int sr = AudioSettings.outputSampleRate;

        // Perfect
        clips["perfect"] = Synth(sr, 0.12f, (t, dur) =>
        {
            float env = Mathf.Pow(0.001f, t / dur);
            return (Mathf.Sin(2f * Mathf.PI * 1200f * t) * 0.3f +
                    Mathf.Sin(2f * Mathf.PI * 1800f * t) * 0.15f) * env;
        });

        // Good
        clips["good"] = Synth(sr, 0.10f, (t, dur) =>
        {
            float env = Mathf.Pow(0.001f, t / dur);
            return Mathf.Sin(2f * Mathf.PI * 800f * t) * 0.25f * env;
        });

        // OK
        clips["ok"] = Synth(sr, 0.12f, (t, dur) =>
        {
            float freq = Mathf.Lerp(300f, 150f, t / dur);
            float env = Mathf.Pow(0.001f, t / dur);
            return Mathf.Sin(2f * Mathf.PI * freq * t) * 0.2f * env;
        });

        // Miss
        clips["miss"] = Synth(sr, 0.15f, (t, dur) =>
        {
            float env = Mathf.Pow(0.001f, t / dur);
            float buzz = Mathf.Sin(2f * Mathf.PI * 80f * t);
            return buzz * 0.12f * env;
        });

        // Free tap
        clips["tap"] = Synth(sr, 0.08f, (t, dur) =>
        {
            float env = Mathf.Pow(0.001f, t / dur);
            return Mathf.Sin(2f * Mathf.PI * 660f * t) * 0.2f * env;
        });
    }

    delegate float SynthFunc(float t, float duration);

    AudioClip Synth(int sr, float dur, SynthFunc func)
    {
        int n = Mathf.CeilToInt(dur * sr);
        var clip = AudioClip.Create("sfx", n, 1, sr, false);
        float[] data = new float[n];
        for (int i = 0; i < n; i++)
            data[i] = func((float)i / sr, dur);
        clip.SetData(data, 0);
        return clip;
    }

    // ======== 按钮回调 ========
    void OnImportClick()
    {
        // 在编辑器中使用 EditorUtility，运行时使用原生文件选择器或预设路径
#if UNITY_EDITOR
        string path = UnityEditor.EditorUtility.OpenFilePanel("选择音频文件", "", "mp3,wav,ogg");
        if (!string.IsNullOrEmpty(path))
            StartCoroutine(LoadAudioCoroutine(path));
#else
        // 移动端：检查 StreamingAssets 或使用 NativeFilePicker
        ShowHUD("请将音频文件放入 StreamingAssets 文件夹\n或设置 preloadAudioPath");
        // 尝试加载默认路径
        string defaultPath = Path.Combine(Application.streamingAssetsPath, "music.mp3");
        if (File.Exists(defaultPath))
            StartCoroutine(LoadAudioCoroutine(defaultPath));
#endif
    }

    void OnFreeClick()
    {
        mode = GameMode.FreeTap;
        SetMenuVisible(false);
        backButton.gameObject.SetActive(true);
        ShowHUD("自由模式 - 随意点击");
    }

    void OnBackClick()
    {
        StopPlaying();
        mode = GameMode.Menu;
        SetMenuVisible(true);
        backButton.gameObject.SetActive(false);
        statsText.color = Color.clear;
        judgeText.color = Color.clear;
        comboText.color = Color.clear;
    }

    void SetMenuVisible(bool visible)
    {
        titleText.gameObject.SetActive(visible);
        importButton.gameObject.SetActive(visible);
        freeButton.gameObject.SetActive(visible);
    }

    // ======== 音频加载 ========
    void TryLoadAudio(string path)
    {
        if (File.Exists(path))
            StartCoroutine(LoadAudioCoroutine(path));
    }

    System.Collections.IEnumerator LoadAudioCoroutine(string path)
    {
        ShowHUD("加载中...");

        // Unity 需要使用 UnityWebRequest 加载音频
        string url = "file:///" + path.Replace("\\", "/");
        AudioType audioType = GetAudioType(path);

        using (var www = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(url, audioType))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                loadedClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                loadedClip.name = Path.GetFileName(path);

                ShowHUD("分析节拍中...");
                yield return null;

                // 分析节拍
                beatTimes = AudioOnsetAnalyzer.Analyze(loadedClip);
                totalBeats = beatTimes.Count;

                float bpm = AudioOnsetAnalyzer.EstimateBPM(beatTimes);
                ShowHUD($"已加载: {loadedClip.name}\n{beatTimes.Count} 个节拍 | ~{bpm:F0} BPM\n点击屏幕开始");

                // 自动进入播放模式
                mode = GameMode.Playing;
                SetMenuVisible(false);
                backButton.gameObject.SetActive(true);
                isPlaying = false;
            }
            else
            {
                ShowHUD("加载失败: " + www.error);
            }
        }
    }

    AudioType GetAudioType(string path)
    {
        string ext = Path.GetExtension(path).ToLower();
        switch (ext)
        {
            case ".mp3": return AudioType.MPEG;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".wav": return AudioType.WAV;
            default: return AudioType.UNKNOWN;
        }
    }

    // ======== 游戏控制 ========
    void StartPlaying()
    {
        if (loadedClip == null || isPlaying) return;

        musicSource.clip = loadedClip;
        musicSource.Play();
        isPlaying = true;
        nextBeatIndex = 0;
        hitBeats.Clear();
        combo = 0;
        maxCombo = 0;
        judgeCounts = new int[4];

        ShowHUD("");
    }

    void StopPlaying()
    {
        if (musicSource.isPlaying)
            musicSource.Stop();
        isPlaying = false;
    }

    // ======== Update ========
    void Update()
    {
        HandleInput();
        UpdatePlayback();
        UpdateFades();
        UpdateRipples();
    }

    void HandleInput()
    {
        bool tapped = false;
        Vector2 tapPos = Vector2.zero;

        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                tapped = true;
                tapPos = touch.position;
            }
        }
        else if (Input.GetMouseButtonDown(0))
        {
            tapped = true;
            tapPos = Input.mousePosition;

            // 检查是否点击了按钮区域（简单检测）
            if (mode == GameMode.Menu)
            {
                // 让按钮处理点击
                return;
            }
        }

        if (!tapped) return;

        switch (mode)
        {
            case GameMode.Menu:
                // 菜单点击由按钮处理
                break;

            case GameMode.Playing:
                OnPlayingTap(tapPos);
                break;

            case GameMode.FreeTap:
                OnFreeTap(tapPos);
                break;
        }
    }

    void OnPlayingTap(Vector2 tapPos)
    {
        // 如果还没开始播放，点击开始
        if (!isPlaying)
        {
            StartPlaying();
            return;
        }

        float currentTime = musicSource.time;

        // 找到最近的节拍
        int closestBeat = -1;
        float closestError = float.MaxValue;

        for (int i = 0; i < beatTimes.Count; i++)
        {
            float error = Mathf.Abs(currentTime - beatTimes[i]);
            if (error < closestError && error < OkWindow * 1.5f && !hitBeats.Contains(i))
            {
                closestError = error;
                closestBeat = i;
            }
        }

        // 判定
        string judge;
        Color color;
        int judgeIdx;

        if (closestBeat < 0 || closestError > OkWindow)
        {
            judge = "Miss";
            color = new Color(1f, 0.3f, 0.3f);
            judgeIdx = 3;
            combo = 0;
        }
        else
        {
            hitBeats.Add(closestBeat);
            float error = currentTime - beatTimes[closestBeat];

            if (closestError <= PerfectWindow)
            {
                judge = "Perfect";
                color = new Color(1f, 0.9f, 0.2f);
                judgeIdx = 0;
            }
            else if (closestError <= GoodWindow)
            {
                judge = "Good";
                color = new Color(0.2f, 1f, 0.5f);
                judgeIdx = 1;
            }
            else
            {
                judge = "OK";
                color = new Color(0.3f, 0.7f, 1f);
                judgeIdx = 2;
            }

            combo++;
            if (combo > maxCombo) maxCombo = combo;

            // 显示早/晚
            if (closestError > 0.015f)
                judge += (error > 0 ? " 慢" : " 快");
        }

        judgeCounts[judgeIdx]++;

        // 播放音效
        string sfx = judgeIdx == 0 ? "perfect" : judgeIdx == 1 ? "good" : judgeIdx == 2 ? "ok" : "miss";
        sfxSource.PlayOneShot(clips[sfx]);

        // 显示
        ShowJudge(judge + $" {Mathf.RoundToInt(closestError * 1000)}ms", color);
        if (combo > 1) ShowCombo(combo);

        SpawnRipple(tapPos, color);
    }

    void OnFreeTap(Vector2 tapPos)
    {
        sfxSource.PlayOneShot(clips["tap"]);
        SpawnRipple(tapPos, new Color(1, 1, 1, 0.5f));
    }

    void UpdatePlayback()
    {
        if (mode != GameMode.Playing || !isPlaying) return;

        // 检查播放是否结束
        if (!musicSource.isPlaying && musicSource.time >= loadedClip.length - 0.1f)
        {
            OnSongEnd();
        }

        // 更新 HUD 显示进度
        float progress = musicSource.time / loadedClip.length;
        int hitCount = hitBeats.Count;
        hudText.text = $"{hitCount}/{totalBeats} | {(int)(musicSource.time / 60)}:{(musicSource.time % 60):00}";
        hudText.color = new Color(1, 1, 1, 0.3f);
    }

    void OnSongEnd()
    {
        isPlaying = false;

        // 计算漏掉的节拍
        int missed = totalBeats - hitBeats.Count;
        judgeCounts[3] += missed;

        // 显示统计
        float accuracy = totalBeats > 0 ?
            (float)(judgeCounts[0] * 100 + judgeCounts[1] * 70 + judgeCounts[2] * 30) / (totalBeats * 100) * 100f : 0;

        statsText.text = $"结果\n\n" +
            $"Perfect: {judgeCounts[0]}\n" +
            $"Good: {judgeCounts[1]}\n" +
            $"OK: {judgeCounts[2]}\n" +
            $"Miss: {judgeCounts[3]}\n\n" +
            $"Max Combo: {maxCombo}\n" +
            $"准确率: {accuracy:F1}%\n\n" +
            $"点击返回";
        statsText.color = Color.white;

        judgeText.color = Color.clear;
        comboText.color = Color.clear;
    }

    // ======== UI 显示 ========
    void ShowHUD(string text)
    {
        hudText.text = text;
        hudText.color = new Color(1, 1, 1, 0.5f);
    }

    void ShowJudge(string text, Color color)
    {
        judgeText.text = text;
        judgeText.color = color;
        judgeText.transform.localScale = Vector3.one * 1.2f;
        judgeFadeTimer = 0.5f;
    }

    void ShowCombo(int c)
    {
        comboText.text = c + " combo";
        comboText.color = new Color(1, 1, 1, 0.6f);
    }

    void UpdateFades()
    {
        if (judgeFadeTimer > 0)
        {
            judgeFadeTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(judgeFadeTimer / 0.5f);
            judgeText.color = new Color(judgeText.color.r, judgeText.color.g, judgeText.color.b, t);
            judgeText.transform.localScale = Vector3.one * Mathf.Lerp(1f, 1.2f, t);
            comboText.color = new Color(1, 1, 1, t * 0.6f);
        }
    }

    // ======== 涟漪 ========
    struct RippleInfo { public GameObject go; public Image img; public float birth; public Color color; }

    void SpawnRipple(Vector2 screenPos, Color color)
    {
        var go = new GameObject("Ripple");
        go.transform.SetParent(canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = new Color(color.r, color.g, color.b, 0.5f);
        img.rectTransform.sizeDelta = new Vector2(40, 40);

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
            if (age > 0.4f)
            {
                Destroy(r.go);
                ripples.RemoveAt(i);
                continue;
            }
            float t = age / 0.4f;
            r.go.transform.localScale = Vector3.one * (1f + t * 3f);
            r.img.color = new Color(r.color.r, r.color.g, r.color.b, 0.5f * (1f - t));
        }
    }
}
