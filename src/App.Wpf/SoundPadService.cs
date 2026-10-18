using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PerAppAudio.RoutingEngine.Audio;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.App.Wpf;

/// <summary>音效库中的一个音效（按文件名识别，支持子目录分类）。</summary>
public sealed record SoundItem(string FilePath, string Name, string Extension, string Category)
{
    /// <summary>唯一键（分类 + 名称），用于热键/收藏/循环映射。</summary>
    public string Key => Category.Length == 0 ? Name : $"{Category}/{Name}";

    /// <summary>展示名（含分类前缀）。</summary>
    public string DisplayName => Category.Length == 0 ? Name : $"{Category} / {Name}";
}

/// <summary>播放目标：扬声器与虚拟麦克风可分别指定（三态模式的落地）。</summary>
public sealed record PlayTargets(string? SpeakerDeviceId, string? MicDeviceId);

/// <summary>
/// 音板服务（SoundPad 功能）：管理 %APPDATA%\PerAppAudio\sounds/ 音效库（支持一级子目录=分类），
/// 每个音效独立 WASAPI 渲染线程播放（可同时多个），支持循环 / 暂停 / 音量归一化。
/// 「仅麦克风 / 两者」模式渲染到虚拟音频设备（VoiceMeeter Input / CABLE Input / 名称含 Virtual 等，
/// 通用规则自动检测，不绑定特定品牌；无虚拟设备时该模式不可用并提示）。
/// Block voice：麦克风模式默认只送音效、不转发真实麦克风；可选 SoundpadMixMic 启动 MicRouter
/// 捕获真实麦克风并混音到虚拟麦克风（由音频引擎与音效渲染会话自动混合）。
/// 客户端固定用设备 Mix 格式——本机共享模式实测只认 Mix 格式（单声道/异采样率请求均被拒
/// 0x88890008），故重采样（WdlResamplingSampleProvider）与声道上混在代码里做。
/// </summary>
public sealed class SoundPadService : IDisposable
{
    private static readonly string[] SupportedExtensions = { ".wav", ".mp3" };

    private readonly Dictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    private readonly Dictionary<string, float> _gainCache = LoadGainCache();
    private MicRouter? _micRouter;

    /// <summary>音效目录（不存在时自动创建）。</summary>
    public static string SoundsDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PerAppAudio", "sounds");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 创建失败（权限/磁盘）时返回路径即可，扫描会容错
            }
            return dir;
        }
    }

    /// <summary>播放状态变化（UI 刷新按钮高亮；可能在播放线程触发，调用方需自行 marshalling）。</summary>
    public event Action? StateChanged;

    /// <summary>扫描音效目录（顶层文件 + 一级子目录=分类，按分类/名称排序）。</summary>
    public IReadOnlyList<SoundItem> Rescan()
    {
        var list = new List<SoundItem>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(SoundsDirectory))
            {
                AddIfSupported(list, f, string.Empty);
            }
            foreach (var dir in Directory.EnumerateDirectories(SoundsDirectory))
            {
                string cat = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    AddIfSupported(list, f, cat);
                }
            }
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Category, b.Category);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
        }
        catch
        {
            return Array.Empty<SoundItem>();
        }
        return list;
    }

    private static void AddIfSupported(List<SoundItem> list, string file, string category)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
        {
            return;
        }
        list.Add(new SoundItem(file, Path.GetFileNameWithoutExtension(file), ext, category));
    }

    public bool IsPlaying(string key)
    {
        lock (_gate)
        {
            return _players.ContainsKey(key);
        }
    }

    /// <summary>按全局排序列表的索引取音效（Numpad/Index 热键用）。</summary>
    public static SoundItem? ByIndex(IReadOnlyList<SoundItem> all, int index)
        => (index >= 0 && index < all.Count) ? all[index] : null;

    /// <summary>播放/停止切换。已在播放则停止。</summary>
    public void Toggle(SoundItem item, PlayTargets targets, float volume, bool loop = false)
    {
        string key = item.Key;
        if (IsPlaying(key))
        {
            Stop(key);
            return;
        }

        if (targets.SpeakerDeviceId is null && targets.MicDeviceId is null)
        {
            return;
        }

        float gain = GetGain(item);
        var player = new Player { Stop = false, Loop = loop, Gain = gain };
        lock (_gate)
        {
            _players[key] = player;
        }

        StateChanged?.Invoke();

        player.Thread = new Thread(() => Pump(item, targets, volume, player))
        {
            IsBackground = true,
            Name = $"SoundPad:{key}",
        };
        player.Thread.Start();
    }

    /// <summary>停止单个音效。</summary>
    public void Stop(string key)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(key, out var player))
            {
                player.Stop = true;
            }
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                player.Stop = true;
            }
        }
    }

    /// <summary>暂停/恢复全部（特殊热键用）。</summary>
    public void PauseAll()
    {
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                player.Paused = true;
            }
        }
        StateChanged?.Invoke();
    }

    public void ResumeAll()
    {
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                player.Paused = false;
            }
        }
        StateChanged?.Invoke();
    }

    public void TogglePauseAll()
    {
        bool anyPlaying;
        lock (_gate)
        {
            anyPlaying = _players.Count > 0;
            if (!anyPlaying)
            {
                return;
            }
            bool allPaused = _players.Values.All(p => p.Paused);
            foreach (var player in _players.Values)
            {
                player.Paused = !allPaused;
            }
        }
        StateChanged?.Invoke();
    }

    public bool IsAnyPaused()
    {
        lock (_gate)
        {
            return _players.Values.Any(p => p.Paused);
        }
    }

    // ---------- 虚拟麦克风设备检测（不绑定品牌）----------

    private static readonly string[] VirtualDevicePatterns =
    {
        "voicemeeter input",
        "cable input",
        "cable-a input",
        "cable-b input",
        "virtual",
        "虚拟",
    };

    public static IReadOnlyList<WasapiDevice> FindVirtualMicDevices()
    {
        try
        {
            return DeviceManager.EnumerateRenderDevices(DeviceState.Active)
                .Where(d => VirtualDevicePatterns.Any(p =>
                    d.FriendlyName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch
        {
            return Array.Empty<WasapiDevice>();
        }
    }

    /// <summary>按偏好选择虚拟麦克风设备（空=自动检测第一个）；找不到返回 null。</summary>
    public static string? ResolveMicDeviceId(string preferredName)
    {
        var virtuals = FindVirtualMicDevices();
        if (virtuals.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredName))
        {
            var match = virtuals.FirstOrDefault(d =>
                string.Equals(d.FriendlyName, preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Id;
            }
        }
        return virtuals[0].Id;
    }

    /// <summary>解析扬声器目标（空=系统默认）；失败返回 null。</summary>
    public static string? ResolveSpeakerDeviceId(string preferredName)
    {
        try
        {
            if (!string.IsNullOrEmpty(preferredName))
            {
                return DeviceManager.ResolveDevice(preferredName).Id;
            }

            var devices = DeviceManager.EnumerateRenderDevices(DeviceState.Active);
            return (devices.FirstOrDefault(d => d.IsDefaultConsole) ?? devices.FirstOrDefault())?.Id;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Block voice / 混音路由 ----------

    /// <summary>
    /// 设置麦克风混音路由：enabled=true 时捕获真实默认麦克风并渲染到虚拟麦克风设备（与音效渲染会话
    /// 由音频引擎自动混合）；enabled=false（Block voice 默认）则停止转发，虚拟麦克风只收到音效。
    /// </summary>
    public void SetMicMix(bool enabled, string? micDeviceId)
    {
        lock (_gate)
        {
            if (enabled && !string.IsNullOrEmpty(micDeviceId))
            {
                if (_micRouter is null || _micRouter.DeviceId != micDeviceId)
                {
                    _micRouter?.Dispose();
                    _micRouter = new MicRouter(micDeviceId);
                    _micRouter.Start();
                }
            }
            else
            {
                _micRouter?.Dispose();
                _micRouter = null;
            }
        }
    }

    // ---------- 音量归一化 ----------

    /// <summary>取该音效的归一化增益（已缓存则直接返回；否则扫描峰值计算）。</summary>
    public float GetGain(SoundItem item)
    {
        if (!App.Settings.SoundpadNormalize)
        {
            return 1f;
        }
        lock (_gainCache)
        {
            if (_gainCache.TryGetValue(item.FilePath, out float g))
            {
                return g;
            }
        }
        float peak = ScanPeak(item.FilePath);
        float gain = peak > 0.0001f ? Math.Clamp(1f / peak, 1f, 8f) : 1f;
        lock (_gainCache)
        {
            _gainCache[item.FilePath] = gain;
            SaveGainCache(_gainCache);
        }
        return gain;
    }

    private static float ScanPeak(string filePath)
    {
        try
        {
            using var reader = new AudioFileReader(filePath);
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            var buffer = new float[4096 * channels];
            float peak = 0f;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    float a = Math.Abs(buffer[i]);
                    if (a > peak)
                    {
                        peak = a;
                    }
                }
            }
            return peak;
        }
        catch
        {
            return 1f;
        }
    }

    private static readonly string GainCacheFile = Path.Combine(SoundsDirectory, ".normalize.json");

    private static Dictionary<string, float> LoadGainCache()
    {
        try
        {
            if (File.Exists(GainCacheFile))
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GainCacheFile));
                var dict = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.GetSingle();
                }
                return dict;
            }
        }
        catch
        {
            // 损坏则忽略
        }
        return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveGainCache(Dictionary<string, float> cache)
    {
        try
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in cache)
            {
                obj[kv.Key] = kv.Value;
            }
            File.WriteAllText(GainCacheFile, obj.ToJsonString());
        }
        catch
        {
            // 写失败不影响播放
        }
    }

    // ---------- 播放线程 ----------

    private void Pump(SoundItem item, PlayTargets targets, float volume, Player player)
    {
        WasapiRenderer? renderer = null;
        try
        {
            using var reader = new AudioFileReader(item.FilePath);

            string targetDesc;
            try
            {
                if (targets.SpeakerDeviceId is not null && targets.MicDeviceId is not null)
                {
                    targetDesc = $"{DeviceManager.ResolveDevice(targets.SpeakerDeviceId).FriendlyName} + " +
                        $"{DeviceManager.ResolveDevice(targets.MicDeviceId).FriendlyName}(麦克风)";
                }
                else if (targets.MicDeviceId is not null)
                {
                    targetDesc = $"{DeviceManager.ResolveDevice(targets.MicDeviceId).FriendlyName}(麦克风)";
                }
                else
                {
                    targetDesc = DeviceManager.ResolveDevice(targets.SpeakerDeviceId!).FriendlyName;
                }
            }
            catch
            {
                targetDesc = targets.SpeakerDeviceId ?? targets.MicDeviceId ?? "?";
            }
            App.AppendFileLog($"[音板] 播放 {item.DisplayName} → {targetDesc}" +
                $"（{reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch vol {volume:F2} gain {player.Gain:F2}{(player.Loop ? " 循环" : string.Empty)}）");

            string primaryDeviceId = targets.MicDeviceId ?? targets.SpeakerDeviceId!;
            renderer = WasapiRenderer.Start(primaryDeviceId, 100);
            renderer.SetSessionVolume(Math.Clamp(volume, 0f, 1f));

            int sourceChannels = Math.Max(1, reader.WaveFormat.Channels);
            int deviceChannels = Math.Max(1, (int)renderer.Format.nChannels);
            bool isFloat = renderer.Format.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat;
            int bytesPerSample = isFloat ? 4 : 2;

            ISampleProvider samples = reader.WaveFormat.SampleRate == renderer.Format.nSamplesPerSec
                ? reader
                : new WdlResamplingSampleProvider(reader, (int)renderer.Format.nSamplesPerSec);

            int chunkFrames = Math.Min(480, renderer.BufferFrames);
            var readBuffer = new float[chunkFrames * sourceChannels];
            var byteBuffer = new byte[chunkFrames * deviceChannels * bytesPerSample];

            while (!player.Stop)
            {
                if (player.Paused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                int free = renderer.GetFreeFrames();
                if (free <= 0)
                {
                    renderer.WaitForPacket(200);
                    continue;
                }

                int framesWanted = Math.Min(free, chunkFrames);
                int samplesRead = samples.Read(readBuffer, 0, framesWanted * sourceChannels);
                if (samplesRead <= 0)
                {
                    if (player.Loop && !player.Stop)
                    {
                        reader.Position = 0;
                        continue;
                    }
                    break; // 播完
                }

                int frames = samplesRead / sourceChannels;
                PackSamples(readBuffer, sourceChannels, byteBuffer, frames, deviceChannels, isFloat, bytesPerSample, player.Gain);

                var handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                try
                {
                    renderer.Write(handle.AddrOfPinnedObject(), frames);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[音板] 播放失败 {item.DisplayName}: {ex.Message}");
            Debug.WriteLine($"[音板] 播放失败 {item.DisplayName}: {ex.Message}");
        }
        finally
        {
            renderer?.Dispose();
            lock (_gate)
            {
                _players.Remove(item.Key);
            }
            App.AppendFileLog($"[音板] 结束 {item.DisplayName}");
            StateChanged?.Invoke();
        }
    }

    /// <summary>把 sourceChannels 的浮点样本按增益缩放后布局到 deviceChannels 的字节缓冲（单声道上混）。</summary>
    internal static void PackSamples(float[] src, int srcChannels, byte[] dst, int frames, int dstChannels, bool isFloat, int bytesPerSample, float gain)
    {
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < dstChannels; c++)
            {
                float s = src[(f * srcChannels) + Math.Min(c, srcChannels - 1)] * gain;
                int offset = ((f * dstChannels) + c) * bytesPerSample;
                if (isFloat)
                {
                    BitConverter.GetBytes(s).CopyTo(dst, offset);
                }
                else
                {
                    short pcm = (short)Math.Clamp(s * 32767f, -32768f, 32767f);
                    BitConverter.GetBytes(pcm).CopyTo(dst, offset);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopAll();
        _micRouter?.Dispose();
        _micRouter = null;
        Thread.Sleep(150);
    }

    private sealed class Player
    {
        public Thread? Thread { get; set; }
        public volatile bool Stop;
        public volatile bool Loop;
        public volatile bool Paused;
        public float Gain = 1f;
    }
}
