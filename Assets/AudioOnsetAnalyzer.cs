using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 离线音频 onset 分析器
/// 对导入的音频文件进行预分析，提取所有鼓点/节拍时间点
/// 使用 Spectral Flux + Peak Picking 算法
/// </summary>
public static class AudioOnsetAnalyzer
{
    // 分析参数
    const int FFTSize = 1024;
    const int HopSize = 512;
    const float OnsetThresholdMultiplier = 1.4f;  // onset 阈值倍数
    const float MinOnsetInterval = 0.06f;          // 最小 onset 间隔（秒）

    /// <summary>
    /// 分析音频并返回所有 onset 时间点（秒）
    /// </summary>
    public static List<float> Analyze(AudioClip clip)
    {
        if (clip == null) return new List<float>();

        int sampleRate = clip.frequency;
        int channels = clip.channels;
        int totalSamples = clip.samples;

        // 获取所有样本（转为单声道）
        float[] samples = new float[totalSamples * channels];
        clip.GetData(samples, 0);

        float[] mono = ToMono(samples, channels);

        // 计算 spectral flux
        List<float> fluxValues = ComputeSpectralFlux(mono, sampleRate);

        // Peak picking 找 onset
        List<float> onsets = PickOnsets(fluxValues, sampleRate);

        Debug.Log($"[AudioOnsetAnalyzer] Found {onsets.Count} onsets in {clip.length:F1}s audio");

        return onsets;
    }

    static float[] ToMono(float[] samples, int channels)
    {
        if (channels == 1) return samples;

        int monoLength = samples.Length / channels;
        float[] mono = new float[monoLength];

        for (int i = 0; i < monoLength; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    static List<float> ComputeSpectralFlux(float[] samples, int sampleRate)
    {
        List<float> flux = new List<float>();
        int numFrames = (samples.Length - FFTSize) / HopSize + 1;

        float[] prevSpectrum = new float[FFTSize / 2];
        float[] currentSpectrum = new float[FFTSize / 2];
        float[] window = MakeHannWindow(FFTSize);
        float[] frame = new float[FFTSize];

        for (int f = 0; f < numFrames; f++)
        {
            int offset = f * HopSize;

            // 应用窗函数
            for (int i = 0; i < FFTSize; i++)
            {
                if (offset + i < samples.Length)
                    frame[i] = samples[offset + i] * window[i];
                else
                    frame[i] = 0;
            }

            // 计算频谱幅度（简化 DFT，按频带分组）
            ComputeMagnitudeSpectrum(frame, currentSpectrum, sampleRate);

            // 计算 spectral flux（只计算正向差分）
            float frameFlux = 0;
            for (int i = 0; i < currentSpectrum.Length; i++)
            {
                float diff = currentSpectrum[i] - prevSpectrum[i];
                if (diff > 0)
                {
                    // 高频加权（鼓点通常有更多高频成分）
                    float weight = 1f + (float)i / currentSpectrum.Length;
                    frameFlux += diff * weight;
                }
            }
            flux.Add(frameFlux);

            // 保存当前频谱
            System.Array.Copy(currentSpectrum, prevSpectrum, currentSpectrum.Length);
        }

        return flux;
    }

    static void ComputeMagnitudeSpectrum(float[] frame, float[] spectrum, int sampleRate)
    {
        // 简化版频谱计算：分成多个频带，计算每个频带的能量
        int bands = spectrum.Length;
        int samplesPerBand = FFTSize / bands;

        for (int b = 0; b < bands; b++)
        {
            float energy = 0;
            int start = b * samplesPerBand;
            int end = Mathf.Min(start + samplesPerBand, FFTSize);

            // 使用 Goertzel 算法的简化版本计算该频带能量
            float freq = (float)(b + 1) * sampleRate / FFTSize;
            float w = 2f * Mathf.PI * freq / sampleRate;
            float coeff = 2f * Mathf.Cos(w);
            float s0 = 0, s1 = 0, s2 = 0;

            for (int i = 0; i < FFTSize; i++)
            {
                s0 = frame[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            energy = Mathf.Sqrt(s1 * s1 + s2 * s2 - coeff * s1 * s2);
            spectrum[b] = energy;
        }
    }

    static List<float> PickOnsets(List<float> flux, int sampleRate)
    {
        List<float> onsets = new List<float>();
        if (flux.Count == 0) return onsets;

        // 计算自适应阈值（滑动窗口平均 + 标准差）
        int windowSize = Mathf.Max(10, sampleRate / HopSize / 4); // ~250ms 窗口
        float[] threshold = new float[flux.Count];

        for (int i = 0; i < flux.Count; i++)
        {
            int start = Mathf.Max(0, i - windowSize / 2);
            int end = Mathf.Min(flux.Count, i + windowSize / 2);

            float mean = 0, variance = 0;
            int count = end - start;

            for (int j = start; j < end; j++)
                mean += flux[j];
            mean /= count;

            for (int j = start; j < end; j++)
            {
                float d = flux[j] - mean;
                variance += d * d;
            }
            float stddev = Mathf.Sqrt(variance / count);

            threshold[i] = mean + OnsetThresholdMultiplier * Mathf.Max(stddev, 0.001f);
        }

        // Peak picking
        float minIntervalFrames = MinOnsetInterval * sampleRate / HopSize;
        float lastOnsetFrame = -minIntervalFrames;

        for (int i = 1; i < flux.Count - 1; i++)
        {
            // 检查是否是局部峰值且超过阈值
            if (flux[i] > threshold[i] &&
                flux[i] > flux[i - 1] &&
                flux[i] >= flux[i + 1] &&
                (i - lastOnsetFrame) >= minIntervalFrames)
            {
                float timeSeconds = (float)i * HopSize / sampleRate;
                onsets.Add(timeSeconds);
                lastOnsetFrame = i;
            }
        }

        // 后处理：合并太近的 onset
        onsets = MergeCloseOnsets(onsets, MinOnsetInterval);

        return onsets;
    }

    static List<float> MergeCloseOnsets(List<float> onsets, float minInterval)
    {
        if (onsets.Count <= 1) return onsets;

        List<float> merged = new List<float>();
        merged.Add(onsets[0]);

        for (int i = 1; i < onsets.Count; i++)
        {
            if (onsets[i] - merged[merged.Count - 1] >= minInterval)
                merged.Add(onsets[i]);
        }

        return merged;
    }

    static float[] MakeHannWindow(int size)
    {
        float[] window = new float[size];
        for (int i = 0; i < size; i++)
            window[i] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (size - 1)));
        return window;
    }

    /// <summary>
    /// 分析结果的简单统计
    /// </summary>
    public static float EstimateBPM(List<float> onsets)
    {
        if (onsets.Count < 4) return 0;

        // 计算间隔
        List<float> intervals = new List<float>();
        for (int i = 1; i < onsets.Count; i++)
        {
            float interval = onsets[i] - onsets[i - 1];
            if (interval > 0.2f && interval < 2f) // 30-300 BPM 范围
                intervals.Add(interval);
        }

        if (intervals.Count == 0) return 0;

        // 聚类找最常见的间隔
        intervals.Sort();
        int mid = intervals.Count / 2;
        float medianInterval = intervals[mid];

        return 60f / medianInterval;
    }
}
