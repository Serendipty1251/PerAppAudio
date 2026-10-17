using System.IO;
using System.Windows;
using System.Windows.Threading;
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
                "PerAppAudio 已在运行（单实例）。请查看系统托盘图标。",
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
                string msg = $"[热键] 已注册 {ok}/6";
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
    }

    public void ShowMainWindow() => _window?.ShowFromTray();

    private void InitTray()
    {
        _trayMenu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindow();
        _trayMenu.Items.Add(showItem);

        var profileItem = new WinForms.ToolStripMenuItem("场景");
        _trayMenu.Items.Add(profileItem);
        _trayMenu.Opening += (_, _) => RebuildProfileMenu(profileItem);

        _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PerAppAudio 每应用音频路由",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        // 左键单击：弹出快捷面板（正在播放/场景/退出）；双击仍走主窗口
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                TrayFlyoutWindow.ShowAtTray();
            }
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

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

        profileItem.DropDownItems.Add("（rules.yaml 中无场景）").Enabled = false;
        foreach (var profile in _host.Rules.Profiles)
        {
            var name = profile.Name;
            var item = new WinForms.ToolStripMenuItem($"{name}（{profile.Rules.Count} 规则）");
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
        _window?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
