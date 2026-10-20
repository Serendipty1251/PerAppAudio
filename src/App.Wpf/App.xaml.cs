using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PerAppAudio.RuleEngine;
using PerAppAudio.SessionManager;
using WinForms = System.Windows.Forms;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 应用入口：单实例互斥 + AppHost 常驻核心 + 托盘。
/// </summary>
public partial class App : System.Windows.Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _trayMenu;

    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio", "app.log");

    public AppHost Host => _host ?? throw new InvalidOperationException("AppHost 未初始化");

    /// <summary>全局设置（UI 线程外只读消费；写入走 MainWindow 设置页）。</summary>
    public static AppSettings Settings { get; set; } = AppSettings.Load();

    /// <summary>热键管理器（OnStartup 创建；无热键系统上为 null）。</summary>
    public static HotkeyManager? Hotkeys { get; private set; }

    /// <summary>UI 侧日志直写文件（与 Log 事件落盘同一格式；供 MainWindow 等无 Log 事件访问权的组件用）。</summary>
    public static void AppendFileLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志落盘失败不影响主流程
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // COM RCW 预热必须在任何自研设备枚举（MainWindow 构造里会调 DeviceManager）之前：
        // NAudio 与自研 COM 共享 CLSID_MMDeviceEnumerator，顺序颠倒会 InvalidCastException。
        WasapiSessionSource.Warmup();

        // 全局异常兜底：写日志 + 提示，避免静默退出
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportCrash("UI 线程异常", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ReportCrash("非 UI 线程异常", ex);
            }
        };

        _host = AppHost.TryAcquireSingleInstance();
        if (_host is null)
        {
            System.Windows.MessageBox.Show(
                L10n.Get("MsgAlreadyRunning"),
                "PerAppAudio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 常驻日志：内存 + 落盘（%APPDATA%\PerAppAudio\app.log）
        _host.Log += msg =>
        {
            string line = $"{DateTime.Now:HH:mm:ss} {msg}";
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch
                {
                    // 日志落盘失败不影响主流程
                }
            });
        };

        // 界面语言：建窗口前载入（XAML 的 {loc:Loc} 绑定即时取到当前语言）
        if (!L10n.Load(Settings.Language))
        {
            L10n.Load("zh-CN");
        }
        AppendFileLog($"[i18n] 界面语言={L10n.Instance.Culture}（{L10n.Get("TabSettings")}/{L10n.Get("BtnApply")}）");

        _window = new MainWindow();
        _window.Show();

        InitTray();

        // 全局热键（失败不影响主流程）
        try
        {
            Hotkeys = HotkeyManager.TryCreate(_window, _window.DispatchHotkey);
            if (Hotkeys is { } hk)
            {
                var (ok, failed) = hk.RegisterAll(Settings);
                string msg = $"[热键] 已注册 {ok} 个";
                if (failed.Count > 0)
                {
                    msg += "，失败：" + string.Join("、", failed);
                }
                _window.AppendLog(msg);
                AppendFileLog(msg);
            }
        }
        catch (Exception ex)
        {
            Hotkeys = null;
            _window.AppendLog($"[热键] 注册失败: {ex.Message}");
        }

        // 托盘面板自测模式：沙箱禁合成输入（SendInput 被丢弃），无法用键鼠驱动 UI，
        // 用环境变量触发「打开→行点击→静音→场景→关闭」全链路自测。
        if (Environment.GetEnvironmentVariable("PERAPPAUDIO_TEST_FLYOUT") == "1")
        {
            AppendFileLog("[自测] PERAPPAUDIO_TEST_FLYOUT=1：2 秒后打开托盘面板并自测");
            var testTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            testTimer.Tick += (_, _) =>
            {
                testTimer.Stop();
                TrayFlyoutWindow.ShowAtTray();
                TrayFlyoutWindow.RunSelfTest();
            };
            testTimer.Start();
        }

        _ = _host.StartAsync();

        // 睡眠/唤醒：唤醒后重建音频路由（睡眠会静默杀死 WASAPI 链路，且看门狗不会自愈）
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // 自测模式：沙箱禁合成键鼠事件，无法点托盘「退出」——
        // 用环境变量触发 ExitApplication（含退出回退），验证回退功能全链路。
        if (Environment.GetEnvironmentVariable("PERAPPAUDIO_TEST_EXIT") == "1")
        {
            AppendFileLog("[自测] PERAPPAUDIO_TEST_EXIT=1：5 秒后触发退出（含回退）");
            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            exitTimer.Tick += (_, _) =>
            {
                exitTimer.Stop();
                ExitApplication();
            };
            exitTimer.Start();
        }
    }

    // ---- 睡眠 / 唤醒 ----
    // 睡眠会让 WASAPI 采集/渲染客户端失效：A 链路实际已死，但看门狗的
    // 「进程存活 + 设备存在」检查都不会触发（_live 仍标记 LoopbackActive=true）
    // → 路由静默失效且永不自愈。唤醒后延迟几秒（等设备/会话重新就绢）显式重建。
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _host is null)
        {
            return;
        }

        AppendFileLog("[电源] 系统唤醒 → 3 秒后重建全部音频路由");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = RebuildRoutesAfterResumeAsync();
            };
            timer.Start();
        }));
    }

    private async Task RebuildRoutesAfterResumeAsync()
    {
        try
        {
            int n = await _host!.RebuildAllRoutesAsync();
            _window?.AppendLog($"[电源] 唤醒后已重建 {n} 条音频路由");
            AppendFileLog($"[电源] 唤醒后已重建 {n} 条音频路由");
        }
        catch (Exception ex)
        {
            AppendFileLog($"[电源] 唤醒后重建路由失败: {ex.Message}");
        }
    }

    public void ShowMainWindow() => _window?.ShowFromTray();

    private void InitTray()
    {
        _trayMenu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem(L10n.Get("BtnOpenMain"));
        showItem.Click += (_, _) => ShowMainWindow();
        _trayMenu.Items.Add(showItem);

        var profileItem = new WinForms.ToolStripMenuItem(L10n.Get("LabelProfile"));
        _trayMenu.Items.Add(profileItem);
        _trayMenu.Opening += (_, _) => RebuildProfileMenu(profileItem);

        _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        var exitItem = new WinForms.ToolStripMenuItem(L10n.Get("BtnExit"));
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitItem);

        _tray = CreateTrayIcon();
        HookTaskbarCreated();
    }

    /// <summary>创建托盘图标（独立成方法：资源管理器重启后需要重建）。</summary>
    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = L10n.Get("TrayTooltip"),
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        // 左键单击：弹出快捷面板（正在播放/场景/退出）；双击仍走主窗口
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                TrayFlyoutWindow.ShowAtTray();
            }
        };
        icon.DoubleClick += (_, _) => ShowMainWindow();
        return icon;
    }

    /// <summary>
    /// 监听 TaskbarCreated 注册消息：资源管理器重启后托盘图标会消失（进程还活着），
    /// 收到即重建。这是 WPF/WinForms 托盘程序的必做项，否则用户只能杀进程重启。
    /// </summary>
    private void HookTaskbarCreated()
    {
        if (_window is null)
        {
            return;
        }

        uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        void Attach()
        {
            if (PresentationSource.FromVisual(_window) is HwndSource source)
            {
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == (int)taskbarCreated)
                    {
                        RebuildTrayIcon();
                    }
                    return IntPtr.Zero;
                });
            }
        }

        if (PresentationSource.FromVisual(_window) is not null)
        {
            Attach();
        }
        else
        {
            _window.SourceInitialized += (_, _) => Attach();
        }
    }

    /// <summary>重建托盘图标（先建后删，避免中途没有图标）。</summary>
    private void RebuildTrayIcon()
    {
        try
        {
            AppendFileLog("[托盘] 检测到资源管理器重启 → 重建托盘图标");
            var old = _tray;
            _tray = CreateTrayIcon();
            old?.Dispose();
        }
        catch (Exception ex)
        {
            AppendFileLog($"[托盘] 重建图标失败: {ex.Message}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private static void ReportCrash(string title, Exception ex)
    {
        string line = $"[严重] {title}: {ex}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
        }

        System.Windows.MessageBox.Show($"{title}：{ex.Message}\n\n详见日志：{LogPath}",
            "PerAppAudio", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>加载应用图标（托盘 / 窗口共用）；缺失时回退系统图标。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "app-icon.ico"),
            Path.Combine(Directory.GetCurrentDirectory(), "app-icon.ico"),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    return new System.Drawing.Icon(path);
                }
            }
            catch
            {
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void RebuildProfileMenu(WinForms.ToolStripMenuItem profileItem)
    {
        profileItem.DropDownItems.Clear();
        if (_host is null)
        {
            return;
        }

        profileItem.DropDownItems.Add(L10n.Get("HintNoProfiles")).Enabled = false;
        foreach (var profile in _host.Rules.Profiles)
        {
            var name = profile.Name;
            var item = new WinForms.ToolStripMenuItem(
                string.Format(L10n.Get("ProfileItemFormat"), name, profile.Rules.Count));
            item.Click += async (_, _) =>
            {
                try
                {
                    await _window!.ApplyProfileAsync(name, replace: true);
                }
                catch (Exception ex)
                {
                    _window!.AppendLog($"[场景] {name} 应用失败: {ex.Message}");
                }
            };
            profileItem.DropDownItems.Add(item);
        }
    }

    public void ExitApplication()
    {
        // 退出回退：把被路由应用的输出还原为系统默认（默认开，设置页可关）。
        // 必须在 Shutdown 前同步执行完毕：OnExit 里的 _host.Dispose() 只拆 A 不清 B2（源代码有意保留持久化）。
        if (Settings.RestoreOnExit && _host is not null)
        {
            try
            {
                int n = _host.RestoreAllRoutesAsync().GetAwaiter().GetResult();
                _window?.AppendLog($"[回退] 已还原 {n} 条路由→系统默认");
            }
            catch (Exception ex)
            {
                AppendFileLog($"[回退] 还原失败: {ex.Message}");
            }
        }

        _window?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
