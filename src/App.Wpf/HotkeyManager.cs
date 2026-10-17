using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 全局热键管理器：RegisterHotKey + 主窗口 WndProc 钩子。
/// 只负责「注册/解析/分发」，具体动作由 MainWindow 注入（复用路由与音量能力）。
/// </summary>
public sealed class HotkeyManager
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    public const int IdNextOutput = 1;
    public const int IdVolumeUp = 2;
    public const int IdVolumeDown = 3;
    public const int IdMute = 4;
    public const int IdShowWindow = 5;
    public const int IdShowFlyout = 6;

    private readonly Window _window;
    private readonly Action<int> _dispatch;
    private HwndSource? _source;
    private bool _hooked;

    private HotkeyManager(Window window, Action<int> dispatch)
    {
        _window = window;
        _dispatch = dispatch;
    }

    /// <summary>创建并挂 WndProc 钩子。宿主窗口未加载 SourceInitialized 时返回 null。</summary>
    public static HotkeyManager? TryCreate(Window window, Action<int> dispatch)
    {
        var mgr = new HotkeyManager(window, dispatch);
        if (!mgr.Hook())
        {
            return null;
        }
        return mgr;
    }

    private bool Hook()
    {
        if (_hooked)
        {
            return true;
        }

        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source is null)
        {
            // 窗口尚未加载：等 SourceInitialized
            _window.SourceInitialized += (_, _) =>
            {
                _source = PresentationSource.FromVisual(_window) as HwndSource;
                _source?.AddHook(WndProc);
                _hooked = _source is not null;
            };
            return true; // 稍后 RegisterAll 前EnsureHook
        }

        _source.AddHook(WndProc);
        _hooked = true;
        return true;
    }

    private void EnsureHook()
    {
        if (_hooked)
        {
            return;
        }
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source is not null)
        {
            _source.AddHook(WndProc);
            _hooked = true;
        }
    }

    /// <summary>按设置注册全部热键。返回 (成功数, 失败ID列表)。</summary>
    public (int Ok, List<string> Failed) RegisterAll(AppSettings settings)
    {
        var failed = new List<string>();
        EnsureHook();
        if (_source is null)
        {
            // 窗口尚未 SourceInitialized：等 Loaded 再注册（一次性）
            _window.Loaded -= OnWindowLoadedRetry;
            _window.Loaded += OnWindowLoadedRetry;
            _pendingSettings = settings;
            return (0, failed);
        }

        var hwnd = _source?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return (0, failed);
        }

        UnregisterAll();

        TryRegister(hwnd, IdNextOutput, "切换输出", settings.HotkeyNextOutput, failed);
        TryRegister(hwnd, IdVolumeUp, "音量+", settings.HotkeyVolumeUp, failed);
        TryRegister(hwnd, IdVolumeDown, "音量-", settings.HotkeyVolumeDown, failed);
        TryRegister(hwnd, IdMute, "静音", settings.HotkeyMute, failed);
        TryRegister(hwnd, IdShowWindow, "显示窗口", settings.HotkeyShowWindow, failed);
        TryRegister(hwnd, IdShowFlyout, "托盘面板", settings.HotkeyShowFlyout, failed);

        return (6 - failed.Count, failed);
    }

    private AppSettings? _pendingSettings;

    private void OnWindowLoadedRetry(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= OnWindowLoadedRetry;
        if (_pendingSettings is { } pending)
        {
            _pendingSettings = null;
            RegisterAll(pending);
        }
    }

    private void UnregisterAll()
    {
        var hwnd = _source?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        foreach (int id in new[] { IdNextOutput, IdVolumeUp, IdVolumeDown, IdMute, IdShowWindow, IdShowFlyout })
        {
            UnregisterHotKey(hwnd, id);
        }
    }

    private void TryRegister(IntPtr hwnd, int id, string label, string? gesture, List<string> failed)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return; // 空 = 主动禁用，不计失败
        }
        if (!TryParse(gesture, out uint mods, out uint vk))
        {
            failed.Add($"{label}({gesture} 无法解析)");
            return;
        }
        if (!RegisterHotKey(hwnd, id, mods | ModNoRepeat, vk))
        {
            failed.Add($"{label}({gesture} 被占用)");
        }
    }

    /// <summary>解析 "Ctrl+Alt+Right" / "Ctrl+Shift+A" / "F5"。</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        string key = parts[^1];
        foreach (var mod in parts[..^1])
        {
            switch (mod.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": case "windows": modifiers |= ModWin; break;
                default: return false;
            }
        }

        vk = KeyToVk(key);
        return vk != 0;
    }

    private static uint KeyToVk(string key)
    {
        string k = key.ToUpperInvariant();
        return k switch
        {
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" => 0x21,
            "PGDN" => 0x22,
            _ when k.Length == 1 && k[0] is >= 'A' and <= 'Z' => k[0],
            _ when k.Length == 1 && k[0] is >= '0' and <= '9' => k[0],
            _ when k.Length == 2 && k[0] == 'F' && k[1] is >= '1' and <= '9' => (uint)(0x70 + (k[1] - '0')),
            _ when k.Length == 3 && k[0] == 'F' && k[1] == '1' && k[2] is >= '0' and <= '2' => (uint)(0x70 + 10 + (k[2] - '0')),
            _ => 0,
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            _dispatch(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }
}
