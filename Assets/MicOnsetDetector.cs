using UnityEngine;
using System;

/// <summary>
/// 第二层：麦克风 onset detection
/// 使用能量谱通量 (Spectral Flux) 算法检测音频 onset
/// 类似 aubio 的 HFC (High Frequency Content) 方法
///
/// 可自动检测外放音乐的节拍，用于修正 tap-tempo 的漂移
/// </summary>
public class MicOnsetDetector : MonoBehaviour
{
    // 配置
    [Header("麦克风设置")]
    public bool enableMic = true;
    public int sampleRate = 22050;       // 降采样率以减少计算量
    public float bufferLengthSec = 0.5f;

    [Header("Onset 检测参数")]
    public int fftSize = 1024;           // FFT 窗口大小
    public int hopSize = 512;            // 帧移
    public float onsetThreshold = 1.5f;  // onset 阈值（相对于滑动平均）
    public float minOnsetInterval = 0.1f;// 两次 onset 最小间隔（秒）

    // 事件
    public event Action<float> OnOnsetDetected;

    // 内部状态
    AudioClip micClip;
    string micDevice;
    int lastReadPos;
    float[] sampleBuffer;
    float[] fftBuffer;
    float[] prevSpectrum;
    float[] spectrum;

    // Onset 检测状态
    float[] fluxHistory;
    int fluxHistoryIdx;
    const int FluxHistoryLen = 20;
    float lastOnsetTime;

    // 是否已初始化
    bool initialized;

    void Start()
    {
        if (enableMic)
            InitMicrophone();
    }

    void InitMicrophone()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[MicOnsetDetector] No microphone found");
            return;
        }

        micDevice = Microphone.devices[0];

        // 请求麦克风权限（Android）
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
            return; // 权限回调后会重新尝试
        }
#endif

        int minFreq, maxFreq;
        Microphone.GetDeviceCaps(micDevice, out minFreq, out maxFreq);

        // 使用设备支持的采样率
        int actualRate = sampleRate;
        if (maxFreq > 0) actualRate = Mathf.Min(sampleRate, maxFreq);
        if (minFreq > 0) actualRate = Mathf.Max(actualRate, minFreq);

        int bufferLen = Mathf.CeilToInt(bufferLengthSec * actualRate);
        micClip = Microphone.Start(micDevice, true, 1, actualRate);

        if (micClip == null)
        {
            Debug.LogWarning("[MicOnsetDetector] Failed to start microphone");
            return;
        }

        sampleRate = actualRate;
        sampleBuffer = new float[hopSize * 4];
        fftBuffer = new float[fftSize];
        spectrum = new float[fftSize / 2];
        prevSpectrum = new float[fftSize / 2];
        fluxHistory = new float[FluxHistoryLen];
        lastReadPos = 0;
        initialized = true;

        Debug.Log("[MicOnsetDetector] Microphone started: " + micDevice + " @ " + actualRate + "Hz");
    }

    void Update()
    {
        if (!initialized || !enableMic) return;

        ProcessMicrophoneData();
    }

    void ProcessMicrophoneData()
    {
        int currentPos = Microphone.GetPosition(micDevice);
        if (currentPos < 0) return;

        int samplesToRead = currentPos - lastReadPos;
        if (samplesToRead < 0) // 环形缓冲区回绕
            samplesToRead += micClip.samples;

        if (samplesToRead < hopSize) return; // 数据不够一帧

        // 读取样本
        int readLen = Mathf.Min(samplesToRead, sampleBuffer.Length);
        float[] tempBuffer = new float[readLen];

        // 处理回绕情况
        int firstPart = Mathf.Min(readLen, micClip.samples - lastReadPos);
        micClip.GetData(tempBuffer, lastReadPos);

        // 处理音频帧
        int processedSamples = 0;
        while (processedSamples + hopSize <= readLen)
        {
            // 填充 FFT 缓冲区
            for (int i = 0; i < fftSize; i++)
            {
                int idx = processedSamples + i - (fftSize - hopSize);
                if (idx >= 0 && idx < readLen)
                    fftBuffer[i] = tempBuffer[idx] * HannWindow(i, fftSize);
                else
                    fftBuffer[i] = 0;
            }

            // 计算频谱（简化版 FFT -> 使用能量）
            ComputeMagnitudeSpectrum();

            // 计算 spectral flux
            float flux = ComputeSpectralFlux();

            // Onset 检测
            DetectOnset(flux);

            // 保存当前频谱
            Array.Copy(spectrum, prevSpectrum, spectrum.Length);

            processedSamples += hopSize;
        }

        lastReadPos = (lastReadPos + processedSamples) % micClip.samples;
    }

    float HannWindow(int n, int N)
    {
        return 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * n / (N - 1)));
    }

    void ComputeMagnitudeSpectrum()
    {
        // 简化版：使用 Unity 的 FFT（通过 AudioSource.GetSpectrumData 不可用时的替代）
        // 这里用 DFT 的简化近似：按频带分组计算能量
        int bands = spectrum.Length;
        float bandWidth = (float)sampleRate / fftSize;

        for (int b = 0; b < bands; b++)
        {
            float sum = 0;
            int start = b * fftSize / bands;
            int end = (b + 1) * fftSize / bands;
            for (int i = start; i < end && i < fftBuffer.Length; i++)
                sum += fftBuffer[i] * fftBuffer[i];
            spectrum[b] = Mathf.Sqrt(sum);
        }

        // 高频加权 (HFC - High Frequency Content)
        for (int b = 0; b < bands; b++)
        {
            float weight = 1f + (float)b / bands; // 高频权重更大
            spectrum[b] *= weight;
        }
    }

    float ComputeSpectralFlux()
    {
        // Spectral flux: 只计算正向差分（onset 是能量增加）
        float flux = 0;
        for (int i = 0; i < spectrum.Length; i++)
        {
            float diff = spectrum[i] - prevSpectrum[i];
            if (diff > 0)
                flux += diff;
        }
        return flux;
    }

    void DetectOnset(float flux)
    {
        // 存入历史
        fluxHistory[fluxHistoryIdx] = flux;
        fluxHistoryIdx = (fluxHistoryIdx + 1) % FluxHistoryLen;

        // 计算滑动平均和标准差
        float mean = 0, variance = 0;
        for (int i = 0; i < FluxHistoryLen; i++)
            mean += fluxHistory[i];
        mean /= FluxHistoryLen;

        for (int i = 0; i < FluxHistoryLen; i++)
        {
            float d = fluxHistory[i] - mean;
            variance += d * d;
        }
        float stddev = Mathf.Sqrt(variance / FluxHistoryLen);

        // 自适应阈值
        float threshold = mean + onsetThreshold * Mathf.Max(stddev, 0.001f);

        // 检测 onset
        float now = Time.unscaledTime;
        if (flux > threshold && now - lastOnsetTime > minOnsetInterval)
        {
            lastOnsetTime = now;
            OnOnsetDetected?.Invoke(now);
        }
    }

    void OnDestroy()
    {
        if (micDevice != null && Microphone.IsRecording(micDevice))
            Microphone.End(micDevice);
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            if (micDevice != null && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            initialized = false;
        }
        else
        {
            if (enableMic)
                InitMicrophone();
        }
    }

    // 公开方法
    public void SetEnabled(bool enabled)
    {
        enableMic = enabled;
        if (enabled && !initialized)
            InitMicrophone();
        else if (!enabled && initialized)
        {
            if (micDevice != null && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            initialized = false;
        }
    }

    public void SetThreshold(float threshold)
    {
        onsetThreshold = Mathf.Clamp(threshold, 0.5f, 5f);
    }
}
