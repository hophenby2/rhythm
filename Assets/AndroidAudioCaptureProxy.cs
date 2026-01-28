using UnityEngine;
using System;

/// <summary>
/// 第三层：Android 系统音频捕获代理
/// 封装 Java 端的 AudioPlaybackCapture API 调用
///
/// 仅在 Android 10+ (API 29+) 可用
/// 需要用户授权 MediaProjection 权限
/// </summary>
public class AndroidAudioCaptureProxy : MonoBehaviour
{
    public event Action<float> OnOnsetDetected;
    public event Action<string> OnPermissionResult;

    bool isSupported;
    bool isRunning;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaClass captureClass;
#endif

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            captureClass = new AndroidJavaClass("com.tapbeat.audiocapture.AndroidAudioCapture");
            isSupported = captureClass.CallStatic<bool>("isSupported");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AndroidAudioCapture] Failed to load Java class: " + e.Message);
            isSupported = false;
        }
#else
        isSupported = false;
#endif
    }

    /// <summary>
    /// 是否支持系统音频捕获（Android 10+）
    /// </summary>
    public bool IsSupported => isSupported;

    /// <summary>
    /// 是否正在捕获
    /// </summary>
    public bool IsRunning => isRunning;

    /// <summary>
    /// 请求权限并开始捕获
    /// 会显示系统对话框让用户确认
    /// </summary>
    public void RequestAndStart()
    {
        if (!isSupported)
        {
            Debug.LogWarning("[AndroidAudioCapture] Not supported on this device");
            OnPermissionResult?.Invoke("unsupported");
            return;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            captureClass.CallStatic("requestPermission", gameObject.name, "OnPermissionCallback");
        }
        catch (Exception e)
        {
            Debug.LogError("[AndroidAudioCapture] Failed to request permission: " + e.Message);
            OnPermissionResult?.Invoke("error");
        }
#endif
    }

    /// <summary>
    /// 停止捕获
    /// </summary>
    public void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isSupported) return;
        try
        {
            captureClass.CallStatic("stopCapture");
            isRunning = false;
        }
        catch (Exception e)
        {
            Debug.LogError("[AndroidAudioCapture] Failed to stop: " + e.Message);
        }
#endif
    }

    /// <summary>
    /// 设置 onset 检测阈值
    /// </summary>
    public void SetThreshold(float threshold)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isSupported) return;
        try
        {
            captureClass.CallStatic("setOnsetThreshold", threshold);
        }
        catch { }
#endif
    }

    // Java 回调：权限结果
    void OnPermissionCallback(string result)
    {
        Debug.Log("[AndroidAudioCapture] Permission result: " + result);
        isRunning = (result == "granted");
        OnPermissionResult?.Invoke(result);
    }

    // Java 回调：Onset 检测
    void OnAndroidAudioOnset(string timeSecondsStr)
    {
        if (float.TryParse(timeSecondsStr, out float t))
        {
            OnOnsetDetected?.Invoke(t);
        }
    }

    void OnDestroy()
    {
        Stop();
    }

    void OnApplicationPause(bool paused)
    {
        // 应用切到后台时停止捕获
        if (paused && isRunning)
        {
            Stop();
        }
    }
}
