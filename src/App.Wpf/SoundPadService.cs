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

/// <summary>音效库中的一个音效（按文件名识别）。</summary>
public sealed record SoundItem(string FilePath, string Name, string Extension);

/// <summary>
/// 音板服务（SoundPad 功能）：管理 %APPDATA%\PerAppAudio\sounds/ 音效库，
/// 每个音效独立 WASAPI 渲染线程播放（可同时多个）。
/// 客户端固定用设备 Mix 格式——本机共享模式实测只认 Mix 格式
/// （单声道/异采样率请求均被拒 0x88890008），故重采样（WdlResamplingSampleProvider）
/// 与声道上混在代码里做。「送入麦克风」= 渲染到 VoiceMeeter Input 虚拟设备，
/// 经 VoiceMeeter 进虚拟麦克风。
/// </summary>
public sealed class SoundPadService : IDisposable
{
    private static readonly string[] SupportedExtensions = { ".wav", ".mp3" };

    private readonly Dictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

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

    /// <summary>扫描音效目录（按文件名排序）。</summary>
    public IReadOnlyList<SoundItem> Rescan()
    {
        try
        {
            return Directory.EnumerateFiles(SoundsDirectory)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new SoundItem(f, Path.GetFileNameWithoutExtension(f), Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<SoundItem>();
        }
    }

    public bool IsPlaying(string name)
    {
        lock (_gate)
        {
            return _players.ContainsKey(name);
        }
    }

    /// <summary>播放/停止切换。已在播放则停止。</summary>
    public void Toggle(SoundItem item, string deviceId, float volume)
    {
        if (IsPlaying(item.Name))
        {
            Stop(item.Name);
            return;
        }

        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var player = new Player { Stop = false };
        lock (_gate)
        {
            _players[item.Name] = player;
        }

        StateChanged?.Invoke();

        player.Thread = new Thread(() => Pump(item, deviceId, volume, player))
        {
            IsBackground = true,
            Name = $"SoundPad:{item.Name}",
        };
        player.Thread.Start();
    }

    /// <summary>停止单个音效。</summary>
    public void Stop(string name)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(name, out var player))
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

    /// <summary>找 VoiceMeeter 输入（虚拟）设备 ID——「送入麦克风」的落点。</summary>
    public static string? FindVoiceMeeterInputId()
    {
        try
        {
            return DeviceManager.EnumerateRenderDevices(DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch
        {
            return null;
        }
    }

    private void Pump(SoundItem item, string deviceId, float volume, Player player)
    {
        WasapiRenderer? renderer = null;
        try
        {
            using var reader = new AudioFileReader(item.FilePath);
            string targetName = deviceId;
            try
            {
                targetName = DeviceManager.ResolveDevice(deviceId).FriendlyName;
            }
            catch
            {
                // 解析失败用 ID 兜底显示
            }
            App.AppendFileLog($"[音板] 播放 {item.Name} → {targetName}（{reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch vol {volume:F2}）");

            // 客户端固定用设备 Mix 格式——本机共享模式实测只认 Mix 格式
            //（单声道/异采样率请求均被拒 0x88890008）。重采样与声道上混在代码里做。
            renderer = WasapiRenderer.Start(deviceId, 100);
            renderer.SetSessionVolume(Math.Clamp(volume, 0f, 1f));

            int sourceChannels = Math.Max(1, reader.WaveFormat.Channels);
            int deviceChannels = Math.Max(1, (int)renderer.Format.nChannels);
            bool isFloat = renderer.Format.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat;
            int bytesPerSample = isFloat ? 4 : 2;

            // 采样率不一致时重采样到设备采样率（保留源声道数，后面再上混）
            ISampleProvider samples = reader.WaveFormat.SampleRate == renderer.Format.nSamplesPerSec
                ? reader
                : new WdlResamplingSampleProvider(reader, (int)renderer.Format.nSamplesPerSec);

            int chunkFrames = Math.Min(480, renderer.BufferFrames);
            var readBuffer = new float[chunkFrames * sourceChannels];
            var byteBuffer = new byte[chunkFrames * deviceChannels * bytesPerSample];

            while (!player.Stop)
            {
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
                    break; // 播完
                }

                int frames = samplesRead / sourceChannels;

                // 逐声道布局：单声道复制到全部声道，多声道取前 N 声（不足补零由上面 Min 保证）
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < deviceChannels; c++)
                    {
                        float s = readBuffer[(f * sourceChannels) + Math.Min(c, sourceChannels - 1)];
                        int offset = ((f * deviceChannels) + c) * bytesPerSample;
                        if (isFloat)
                        {
                            BitConverter.GetBytes(s).CopyTo(byteBuffer, offset);
                        }
                        else
                        {
                            short pcm = (short)Math.Clamp(s * 32767f, -32768f, 32767f);
                            BitConverter.GetBytes(pcm).CopyTo(byteBuffer, offset);
                        }
                    }
                }

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
            App.AppendFileLog($"[音板] 播放失败 {item.Name}: {ex.Message}");
            Debug.WriteLine($"[音板] 播放失败 {item.Name}: {ex.Message}");
        }
        finally
        {
            renderer?.Dispose();
            lock (_gate)
            {
                _players.Remove(item.Name);
            }
            App.AppendFileLog($"[音板] 结束 {item.Name}");
            StateChanged?.Invoke();
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
        // 播放线程会在 Stop 后自然退出并释放渲染器；给一个短窗口
        Thread.Sleep(150);
    }

    private sealed class Player
    {
        public Thread? Thread { get; set; }
        public volatile bool Stop;
    }
}
