using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 高效的离线音频 onset 分析器
/// 使用多频带能量差分检测鼓点/节拍
/// </summary>
public static class AudioOnsetAnalyzer
{
    // 分析参数
    const int FrameSize = 1024;          // 每帧样本数
    const int HopSize = 512;             // 帧移
    const int NumBands = 8;              // 频带数（少量频带，快速计算）
    const float MinOnsetInterval = 0.05f; // 最小 onset 间隔

    /// <summary>
    /// 分析音频并返回所有 onset 时间点（秒）
    /// </summary>
    public static List<float> Analyze(AudioClip clip)
    {
        if (clip == null) return new List<float>();

        int sampleRate = clip.frequency;
        int channels = clip.channels;
        int totalSamples = clip.samples;

        // 获取样本并转单声道
        float[] samples = new float[totalSamples * channels];
        clip.GetData(samples, 0);
        float[] mono = ToMono(samples, channels, totalSamples);

        // 降采样以加速分析（目标 ~11kHz，足够检测 onset）
        int downsampleFactor = Mathf.Max(1, sampleRate / 11025);
        float[] downsampled = Downsample(mono, downsampleFactor);
        int effectiveRate = sampleRate / downsampleFactor;

        // 计算每帧的多频带能量
        List<float[]> bandEnergies = ComputeBandEnergies(downsampled, effectiveRate);

        // 检测 onset（能量突增）
        List<float> onsets = DetectOnsets(bandEnergies, effectiveRate);

        Debug.Log($"[AudioOnsetAnalyzer] {clip.length:F1}s audio, {onsets.Count} onsets detected");

        return onsets;
    }

    static float[] ToMono(float[] samples, int channels, int monoLength)
    {
        if (channels == 1) return samples;

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

    static float[] Downsample(float[] samples, int factor)
    {
        if (factor <= 1) return samples;

        int newLen = samples.Length / factor;
        float[] result = new float[newLen];
        for (int i = 0; i < newLen; i++)
            result[i] = samples[i * factor];
        return result;
    }

    static List<float[]> ComputeBandEnergies(float[] samples, int sampleRate)
    {
        List<float[]> energies = new List<float[]>();
        int numFrames = (samples.Length - FrameSize) / HopSize + 1;

        // 预计算每个频带的频率范围（对数分布）
        // 频带 0: 低频 (bass), 频带 7: 高频 (hi-hat)
        float[] bandLimits = new float[NumBands + 1];
        float minFreq = 60f;
        float maxFreq = sampleRate / 2f * 0.8f;
        for (int b = 0; b <= NumBands; b++)
        {
            float t = (float)b / NumBands;
            bandLimits[b] = minFreq * Mathf.Pow(maxFreq / minFreq, t);
        }

        for (int f = 0; f < numFrames; f++)
        {
            int offset = f * HopSize;
            float[] bandEnergy = new float[NumBands];

            // 简单的频带能量计算：用差分近似高频
            // 低频用原始信号能量，高频用差分信号能量
            float lowEnergy = 0, midEnergy = 0, highEnergy = 0;

            for (int i = 0; i < FrameSize && offset + i < samples.Length; i++)
            {
                float s = samples[offset + i];
                lowEnergy += s * s;

                // 一阶差分（近似高频）
                if (i > 0)
                {
                    float diff = samples[offset + i] - samples[offset + i - 1];
                    midEnergy += diff * diff;
                }

                // 二阶差分（更高频）
                if (i > 1)
                {
                    float diff2 = samples[offset + i] - 2 * samples[offset + i - 1] + samples[offset + i - 2];
                    highEnergy += diff2 * diff2;
                }
            }

            // 简化为 3 个主要频带，复制到 8 个用于平滑
            lowEnergy = Mathf.Sqrt(lowEnergy / FrameSize);
            midEnergy = Mathf.Sqrt(midEnergy / FrameSize) * 2f;  // 放大中频
            highEnergy = Mathf.Sqrt(highEnergy / FrameSize) * 4f; // 放大高频

            // 分配到各频带
            bandEnergy[0] = lowEnergy;
            bandEnergy[1] = lowEnergy * 0.7f + midEnergy * 0.3f;
            bandEnergy[2] = lowEnergy * 0.3f + midEnergy * 0.7f;
            bandEnergy[3] = midEnergy;
            bandEnergy[4] = midEnergy * 0.7f + highEnergy * 0.3f;
            bandEnergy[5] = midEnergy * 0.3f + highEnergy * 0.7f;
            bandEnergy[6] = highEnergy;
            bandEnergy[7] = highEnergy;

            energies.Add(bandEnergy);
        }

        return energies;
    }

    static List<float> DetectOnsets(List<float[]> bandEnergies, int sampleRate)
    {
        List<float> onsets = new List<float>();
        if (bandEnergies.Count < 3) return onsets;

        int windowSize = Mathf.Max(5, sampleRate / HopSize / 8); // ~125ms 窗口
        float[] flux = new float[bandEnergies.Count];

        // 计算 spectral flux（各频带能量增加的加权和）
        for (int f = 1; f < bandEnergies.Count; f++)
        {
            float frameFlux = 0;
            for (int b = 0; b < NumBands; b++)
            {
                float diff = bandEnergies[f][b] - bandEnergies[f - 1][b];
                if (diff > 0)
                {
                    // 高频带权重更大（鼓点特征）
                    float weight = 1f + (float)b / NumBands;
                    frameFlux += diff * weight;
                }
            }
            flux[f] = frameFlux;
        }

        // 自适应阈值 + 峰值检测
        float minIntervalFrames = MinOnsetInterval * sampleRate / HopSize;
        float lastOnsetFrame = -minIntervalFrames;

        for (int f = 1; f < flux.Length - 1; f++)
        {
            // 计算局部统计
            int start = Mathf.Max(0, f - windowSize);
            int end = Mathf.Min(flux.Length, f + windowSize);

            float mean = 0, max = 0;
            for (int i = start; i < end; i++)
            {
                mean += flux[i];
                if (flux[i] > max) max = flux[i];
            }
            mean /= (end - start);

            // 自适应阈值
            float threshold = mean * 1.5f + max * 0.1f;

            // 峰值检测
            if (flux[f] > threshold &&
                flux[f] > flux[f - 1] &&
                flux[f] >= flux[f + 1] &&
                (f - lastOnsetFrame) >= minIntervalFrames)
            {
                float timeSeconds = (float)f * HopSize / sampleRate;
                onsets.Add(timeSeconds);
                lastOnsetFrame = f;
            }
        }

        return onsets;
    }

    /// <summary>
    /// 估算 BPM
    /// </summary>
    public static float EstimateBPM(List<float> onsets)
    {
        if (onsets.Count < 4) return 0;

        List<float> intervals = new List<float>();
        for (int i = 1; i < onsets.Count; i++)
        {
            float interval = onsets[i] - onsets[i - 1];
            if (interval > 0.2f && interval < 2f)
                intervals.Add(interval);
        }

        if (intervals.Count == 0) return 0;

        intervals.Sort();
        float median = intervals[intervals.Count / 2];
        return 60f / median;
    }
}
