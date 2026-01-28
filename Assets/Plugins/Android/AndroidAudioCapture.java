package com.tapbeat.audiocapture;

import android.annotation.TargetApi;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.util.Log;

import com.unity3d.player.UnityPlayer;

/**
 * 第三层：Android 10+ 系统音频捕获
 * 使用 AudioPlaybackCapture API 捕获其他 App 的音频输出
 * 不会打断音乐播放
 */
@TargetApi(Build.VERSION_CODES.Q)
public class AndroidAudioCapture {
    private static final String TAG = "TapBeatAudioCapture";
    private static final int REQUEST_CODE = 1001;

    private static MediaProjectionManager projectionManager;
    private static MediaProjection mediaProjection;
    private static AudioRecord audioRecord;
    private static Thread captureThread;
    private static volatile boolean isCapturing = false;

    // 音频参数
    private static final int SAMPLE_RATE = 22050;
    private static final int CHANNEL_CONFIG = AudioFormat.CHANNEL_IN_MONO;
    private static final int AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT;
    private static int bufferSize;

    // Unity 回调
    private static String gameObjectName;
    private static String callbackMethod;

    // Onset 检测状态（在 Java 端预处理，减少 JNI 开销）
    private static float[] energyHistory = new float[20];
    private static int energyIdx = 0;
    private static long lastOnsetMs = 0;
    private static final long MIN_ONSET_INTERVAL_MS = 100;
    private static float onsetThreshold = 1.8f;

    /**
     * 检查是否支持（Android 10+）
     */
    public static boolean isSupported() {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q;
    }

    /**
     * 请求屏幕捕获权限（会显示系统对话框）
     */
    public static void requestPermission(String unityGameObject, String method) {
        if (!isSupported()) {
            Log.w(TAG, "AudioPlaybackCapture requires Android 10+");
            return;
        }

        gameObjectName = unityGameObject;
        callbackMethod = method;

        Activity activity = UnityPlayer.currentActivity;
        projectionManager = (MediaProjectionManager)
                activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE);

        Intent intent = projectionManager.createScreenCaptureIntent();
        activity.startActivityForResult(intent, REQUEST_CODE);
    }

    /**
     * 处理权限请求结果（需要在 UnityPlayerActivity 中调用）
     */
    public static void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode != REQUEST_CODE) return;

        if (resultCode == Activity.RESULT_OK && data != null) {
            mediaProjection = projectionManager.getMediaProjection(resultCode, data);
            startCapture();
            notifyUnity("granted");
        } else {
            notifyUnity("denied");
        }
    }

    /**
     * 开始捕获
     */
    private static void startCapture() {
        if (mediaProjection == null || isCapturing) return;

        try {
            bufferSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT);
            bufferSize = Math.max(bufferSize, SAMPLE_RATE / 10); // 至少 100ms 缓冲

            AudioPlaybackCaptureConfiguration config =
                    new AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
                            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                            .addMatchingUsage(AudioAttributes.USAGE_GAME)
                            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                            .build();

            AudioFormat format = new AudioFormat.Builder()
                    .setEncoding(AUDIO_FORMAT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(CHANNEL_CONFIG)
                    .build();

            audioRecord = new AudioRecord.Builder()
                    .setAudioPlaybackCaptureConfig(config)
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(bufferSize * 2)
                    .build();

            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                Log.e(TAG, "AudioRecord initialization failed");
                notifyUnity("error");
                return;
            }

            audioRecord.startRecording();
            isCapturing = true;

            captureThread = new Thread(AndroidAudioCapture::captureLoop, "AudioCaptureThread");
            captureThread.start();

            Log.i(TAG, "Audio capture started");

        } catch (Exception e) {
            Log.e(TAG, "Failed to start capture: " + e.getMessage());
            notifyUnity("error");
        }
    }

    /**
     * 音频捕获循环
     */
    private static void captureLoop() {
        short[] buffer = new short[bufferSize / 2];
        float[] floatBuffer = new float[buffer.length];

        while (isCapturing) {
            int read = audioRecord.read(buffer, 0, buffer.length);
            if (read > 0) {
                // 转换为 float 并计算能量
                float energy = 0;
                for (int i = 0; i < read; i++) {
                    float sample = buffer[i] / 32768f;
                    floatBuffer[i] = sample;
                    energy += sample * sample;
                }
                energy = (float) Math.sqrt(energy / read);

                // Onset 检测
                detectOnset(energy);
            }
        }
    }

    /**
     * 简单的 onset 检测（能量突增）
     */
    private static void detectOnset(float energy) {
        // 存入历史
        energyHistory[energyIdx] = energy;
        energyIdx = (energyIdx + 1) % energyHistory.length;

        // 计算平均和标准差
        float mean = 0, variance = 0;
        for (float e : energyHistory) mean += e;
        mean /= energyHistory.length;

        for (float e : energyHistory) {
            float d = e - mean;
            variance += d * d;
        }
        float stddev = (float) Math.sqrt(variance / energyHistory.length);

        // 自适应阈值检测
        float threshold = mean + onsetThreshold * Math.max(stddev, 0.001f);

        long now = System.currentTimeMillis();
        if (energy > threshold && now - lastOnsetMs > MIN_ONSET_INTERVAL_MS) {
            lastOnsetMs = now;
            // 通知 Unity
            notifyOnset(now / 1000f);
        }
    }

    /**
     * 停止捕获
     */
    public static void stopCapture() {
        isCapturing = false;

        if (captureThread != null) {
            try {
                captureThread.join(1000);
            } catch (InterruptedException ignored) {}
            captureThread = null;
        }

        if (audioRecord != null) {
            audioRecord.stop();
            audioRecord.release();
            audioRecord = null;
        }

        if (mediaProjection != null) {
            mediaProjection.stop();
            mediaProjection = null;
        }

        Log.i(TAG, "Audio capture stopped");
    }

    /**
     * 设置 onset 检测阈值
     */
    public static void setOnsetThreshold(float threshold) {
        onsetThreshold = Math.max(0.5f, Math.min(5f, threshold));
    }

    /**
     * 通知 Unity 权限结果
     */
    private static void notifyUnity(String message) {
        if (gameObjectName != null && callbackMethod != null) {
            UnityPlayer.UnitySendMessage(gameObjectName, callbackMethod, message);
        }
    }

    /**
     * 通知 Unity onset 事件
     */
    private static void notifyOnset(float timeSeconds) {
        if (gameObjectName != null) {
            UnityPlayer.UnitySendMessage(gameObjectName, "OnAndroidAudioOnset",
                    String.valueOf(timeSeconds));
        }
    }

    /**
     * 检查是否正在捕获
     */
    public static boolean isRunning() {
        return isCapturing;
    }
}
