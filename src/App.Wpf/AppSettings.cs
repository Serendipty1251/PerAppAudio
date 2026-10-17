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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            // 设置损坏回退默认，不影响启动
        }
        return new AppSettings();
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
