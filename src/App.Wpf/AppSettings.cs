using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 应用设置（%APPDATA%\PerAppAudio\ui.json）。含窗口几何、刷新策略、自启、OSD、热键配置。
/// 语言/主题字段已预留，由 i18n 轮次启用。
/// </summary>
public sealed class AppSettings
{
    // ---- 窗口几何 ----
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1060;
    public double Height { get; set; } = 680;

    // ---- 正在播放 ----
    public int RefreshSeconds { get; set; } = 10;
    public bool AutoRefresh { get; set; }

    // ---- 常规 ----
    public bool StartWithWindows { get; set; }
    public bool OsdEnabled { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "Light";

    // ---- 音板（SoundPad）----
    /// <summary>扬声器目标设备（友好名；空 = 跟随系统默认）。</summary>
    public string SoundpadDevice { get; set; } = string.Empty;

    /// <summary>虚拟麦克风目标设备（友好名；空 = 自动检测 VoiceMeeter Input / CABLE Input 等）。</summary>
    public string SoundpadMicDevice { get; set; } = string.Empty;

    /// <summary>播放模式：0=仅扬声器 1=仅麦克风 2=两者。默认仅扬声器（开箱即用；虚拟麦克风设备非必需）。</summary>
    public int SoundpadMode { get; set; } = 0;

    /// <summary>音板总音量 0–1。</summary>
    public float SoundpadVolume { get; set; } = 0.8f;

    /// <summary>每音效热键：音效 key（分类/名称）→ 组合键串，如 "Ctrl+Alt+1"。</summary>
    public Dictionary<string, string> SoundpadHotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>开启循环播放的音效 key 集合。</summary>
    public List<string> SoundpadLoops { get; set; } = new();

    /// <summary>Hotbar 置顶收藏的音效 key 集合（顺序即展示顺序）。</summary>
    public List<string> SoundpadHotbar { get; set; } = new();

    /// <summary>音量归一化：播放前按峰值增益把各音效对齐到相近响度。</summary>
    public bool SoundpadNormalize { get; set; } = true;

    /// <summary>Block voice：麦克风模式只送音效、不转发真实麦克风（默认 true）。为 false 时启动混音路由转发人声。</summary>
    public bool SoundpadBlockVoice { get; set; } = true;

    /// <summary>混入人声：麦克风模式下捕获真实默认麦克风并混音到虚拟麦克风（与 Block voice 互斥；默认 false）。</summary>
    public bool SoundpadMixMic { get; set; }

    /// <summary>透传热键：音板热键/特殊热键用低层键盘钩子，触发后不吞键，前台游戏也能收到同键。</summary>
    public bool SoundpadPassthrough { get; set; }

    /// <summary>索引热键：Alt+数字 / Ctrl+Alt+数字 播放当前列表第 N 个音效。</summary>
    public bool SoundpadIndexHotkeys { get; set; } = true;

    /// <summary>Numpad 索引热键：小键盘 1–9 播放第 N 个音效（默认关闭，避免占用游戏小键盘）。</summary>
    public bool SoundpadNumpadIndex { get; set; }

    /// <summary>编辑器中最近打开的音效 key（用于快捷重开）。</summary>
    public string? SoundpadEditorLast { get; set; }

    // ---- 音板特殊热键（空串 = 禁用）----
    public string SoundpadHkStopAll { get; set; } = "Ctrl+Alt+End";
    public string SoundpadHkPause { get; set; } = "Ctrl+Alt+Home";
    public string SoundpadHkRandom { get; set; } = "Ctrl+Alt+R";
    public string SoundpadHkPrevious { get; set; } = "Ctrl+Alt+Left";
    public string SoundpadHkVolumeUp { get; set; } = "Ctrl+Alt+Add";
    public string SoundpadHkVolumeDown { get; set; } = "Ctrl+Alt+Subtract";
    public string SoundpadHkMode { get; set; } = "Ctrl+Alt+Shift+M";

    // ---- 全局热键（空字符串 = 禁用该热键）----
    public string HotkeyNextOutput { get; set; } = "Ctrl+Alt+Right";
    public string HotkeyVolumeUp { get; set; } = "Ctrl+Alt+Up";
    public string HotkeyVolumeDown { get; set; } = "Ctrl+Alt+Down";
    public string HotkeyMute { get; set; } = "Ctrl+Alt+M";
    public string HotkeyShowWindow { get; set; } = "Ctrl+Alt+P";
    public string HotkeyShowFlyout { get; set; } = "Ctrl+Alt+F";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio", "ui.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // 设置损坏回退默认，不影响启动
        }
        return new AppSettings();
    }

    /// <summary>修正历史版本遗留的自相矛盾配置（升级迁移，幂等）。</summary>
    private void Normalize()
    {
        // 旧默认把「切换模式」与「静音」都设成 Ctrl+Alt+M：RegisterHotKey 注册同一组合键只能成功一次，
        // 后注册的（切换模式）必然失败。等于旧值时迁移到不冲突的组合。
        if (!string.IsNullOrWhiteSpace(SoundpadHkMode) &&
            string.Equals(SoundpadHkMode, HotkeyMute, StringComparison.OrdinalIgnoreCase))
        {
            SoundpadHkMode = "Ctrl+Alt+Shift+M";
        }

        // Block voice 与「混入人声」互为反义：以 Block voice 为准，修正可能不一致的旧配置。
        SoundpadMixMic = !SoundpadBlockVoice;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写失败不影响运行
        }
    }

    // ---- 开机自启（HKCU\...\Run，无需管理员） ----

    private const string RunValueName = "PerAppAudio";

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(RunValueName) is not null;
    }

    /// <summary>写/删当前用户启动项。返回 false = 注册表操作失败。</summary>
    public static bool SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                return false;
            }

            if (enable)
            {
                // 单文件发布下 Assembly.Location 为空串，用 ProcessPath，兜底 AppContext.BaseDirectory
                string exe = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "PerAppAudio.exe");
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
