using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PerAppAudio.RuleEngine;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;
using WinForms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 托盘弹出面板：左键点托盘图标弹出，展示正在播放的会话（点击行循环切设备 / 静音按钮）、
/// 场景快切、打开主窗口 / 退出。失焦自动收起；数据每 3s 自刷新。
/// 单实例静态 Show——复用同一个窗口，不累积。
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    private static TrayFlyoutWindow? _current;

    private readonly System.Collections.ObjectModel.ObservableCollection<FlyoutSession> _sessions = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _dismissTimer;
    private readonly DateTime _openedAt = DateTime.UtcNow;
    private bool _closing;

    // WS_EX_TOOLWINDOW：不进 Alt+Tab 列表
    private const int GwExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    public TrayFlyoutWindow()
    {
        // 定时器先于 InitializeComponent 创建（与 MainWindow 同样的 XAML 初始化期约束）
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };

        InitializeComponent();

        SessionList.ItemsSource = _sessions;

        Loaded += (_, _) =>
        {
            Reload();
            App.AppendFileLog($"[托盘] 面板已打开（会话 {_sessions.Count} 个，位置 {Left:F0},{Top:F0}）");
            _refreshTimer.Start();
            _dismissTimer.Start();
        };
        _refreshTimer.Tick += (_, _) => Reload();
        _dismissTimer.Tick += (_, _) =>
        {
            // 刚弹出的 500ms 内不收起（鼠标可能还在移向面板的路上）
            if (DateTime.UtcNow - _openedAt < TimeSpan.FromMilliseconds(500))
            {
                return;
            }
            if (!IsMouseOver)
            {
                Close();
            }
        };
        Deactivated += (_, _) =>
        {
            if (DateTime.UtcNow - _openedAt >= TimeSpan.FromMilliseconds(500) && !IsMouseOver)
            {
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _dismissTimer.Stop();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };

        // 无激活窗口 + TOOLWINDOW：托盘浮层标准形态
        SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int style = (int)GetWindowLong(h, GwExStyle);
                SetWindowLong(h, GwExStyle, new IntPtr(style | WsExToolWindow));
            }
            catch
            {
                // 扩展样式失败不影响功能
            }
        };
    }

    /// <summary>在鼠标（托盘图标）处弹出；单实例复用。</summary>
    public static void ShowAtTray()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowAtTray));
            return;
        }

        _current?.Close();

        var w = new TrayFlyoutWindow();

        // 位置：吸附鼠标右下方；超出工作区则回弹到边界内
        var cursor = WinForms.Cursor.Position;          // 屏幕像素
        var screen = WinForms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;                  // 像素

        // 像素 → DIP：取任意已显示窗口的呈现源换算；取不到则 1:1
        Matrix toDip = Matrix.Identity;
        var any = app.MainWindow ?? app.Windows.OfType<Window>().FirstOrDefault();
        if (any is not null && PresentationSource.FromVisual(any)?.CompositionTarget is { } target)
        {
            toDip = target.TransformFromDevice;
        }

        var p = toDip.Transform(new System.Windows.Point(cursor.X, cursor.Y));
        var workLt = toDip.Transform(new System.Windows.Point(work.Left, work.Top));
        var workRb = toDip.Transform(new System.Windows.Point(work.Right, work.Bottom));

        w.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double height = w.DesiredSize.Height;

        double left = Math.Min(p.X + 8, workRb.X - w.Width - 8);
        left = Math.Max(left, workLt.X + 8);
        double top = p.Y - height - 8;
        if (top < workLt.Y + 8)
        {
            top = Math.Min(p.Y + 8, workRb.Y - height - 8);
        }

        w.Left = left;
        w.Top = top;
        _current = w;
        w.Show();
    }

    private static AppHost Host =>
        ((App)System.Windows.Application.Current).Host;

    /// <summary>
    /// 面板自测（环境变量 PERAPPAUDIO_TEST_FLYOUT=1 触发）：
    /// 沙箱禁止合成输入事件（SendInput/mouse_event 被丢弃），无法用键鼠驱动 UI，
    /// 因此直接调用各交互的核心方法，验证 打开→数据装载→行点击→静音→场景→关闭 全链路。
    /// </summary>
    public static async void RunSelfTest()
    {
        if (_current is not { } w)
        {
            App.AppendFileLog("[自测] 面板未打开，跳过");
            return;
        }

        try
        {
            await Task.Delay(1500); // 等首次 Reload 稳定

            if (w._sessions.Count > 0)
            {
                var row = w._sessions[0];
                App.AppendFileLog($"[自测] ① 行点击 {row.Name}（PID {row.ProcessId}，当前设备 {row.CurrentDevice}）");
                await w.ActivateSessionAsync(row);
            }
            else
            {
                App.AppendFileLog("[自测] ① 无音频会话，跳过行点击（需先有应用在播放）");
            }

            await Task.Delay(1200);

            if (w._sessions.Count > 0)
            {
                App.AppendFileLog("[自测] ② 静音切换");
                w.ToggleMuteSession(w._sessions[0]);
            }

            await Task.Delay(1200);

            var profiles = Host.Rules.Profiles;
            if (profiles.Count > 0)
            {
                App.AppendFileLog($"[自测] ③ 应用场景「{profiles[0].Name}」");
                await w.ApplyProfileByName(profiles[0].Name);
            }
            else
            {
                App.AppendFileLog("[自测] ③ rules.yaml 无场景，跳过");
            }

            await Task.Delay(1200);
            App.AppendFileLog("[自测] ④ 完成，关闭面板");
            w.Close();
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[自测] 失败: {ex}");
        }
    }

    // ---------- 数据 ----------

    private void Reload()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            var host = Host;

            // 会话（同一 PID 多设备只留一行——与主窗口一致）
            _sessions.Clear();
            var deviceOf = WasapiSessionSource.GetDeviceOfProcesses();
            foreach (var s in WasapiSessionSource.EnumerateSessions()
                .Where(s => s.ProcessId > 0)
                .GroupBy(s => s.ProcessId)
                .OrderByDescending(g => g.Any(x => x.State == SessionState.Active)))
            {
                var first = s.First();
                var vol = WasapiSessionSource.GetSessionVolumeStates(first.ProcessId).FirstOrDefault();
                string name = string.IsNullOrEmpty(Path.GetFileName(first.ProcessPath))
                    ? first.DisplayName
                    : Path.GetFileName(first.ProcessPath);
                _sessions.Add(new FlyoutSession
                {
                    ProcessId = first.ProcessId,
                    Name = name,
                    CurrentDevice = deviceOf.TryGetValue(first.ProcessId, out var dev) ? dev : "-",
                    Volume = vol?.Volume ?? 1f,
                    Muted = vol?.Muted ?? false,
                });
            }

            SessionEmptyHint.Visibility = _sessions.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RebuildProfiles(host);

            var online = DeviceManager.EnumerateRenderDevices(DeviceState.Active);
            int live = host.Coordinator.GetLiveRoutes().Count;
            SummaryText.Text = $"在线设备 {online.Count} · 活动链路 {live} · 会话 {_sessions.Count}";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "状态读取失败";
            App.AppendFileLog($"[托盘] 面板刷新失败: {ex.Message}");
        }
    }

    private void RebuildProfiles(AppHost host)
    {
        ProfileList.Children.Clear();
        if (host.Rules.Profiles.Count == 0)
        {
        ProfileList.Children.Add(new TextBlock
        {
            Text = L10n.Get("HintNoProfiles"),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
            return;
        }

        foreach (var profile in host.Rules.Profiles)
        {
            var button = new WpfButton
            {
                Content = profile.Name,
                Tag = profile.Name,
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 11,
            };
            button.Click += ProfileButton_Click;
            ProfileList.Children.Add(button);
        }
    }

    // ---------- 交互 ----------

    private async void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FlyoutSession row)
        {
            return;
        }

        await ActivateSessionAsync(row);
    }

    /// <summary>行点击核心动作：循环切到下一个在线设备（热键/自测共用）。</summary>
    internal async Task ActivateSessionAsync(FlyoutSession row)
    {
        try
        {
            var result = await SessionActions.CycleToNextDeviceAsync(Host, row.ProcessId, row.Name);
            App.AppendFileLog($"[托盘] {result.Detail}");
            Osd(result.Message, result.Success ? "🔊" : "⚠️");
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[托盘] 切换设备失败: {ex.Message}");
            Osd("切换设备失败", "⚠️");
        }

        Reload();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FlyoutSession row)
        {
            return;
        }

        ToggleMuteSession(row);
    }

    /// <summary>静音切换核心动作。</summary>
    internal void ToggleMuteSession(FlyoutSession row)
    {
        var r = SessionActions.ToggleMute(row.ProcessId);
        if (r is null)
        {
            return;
        }

        var (muted, affected) = r.Value;
        App.AppendFileLog($"[托盘] {row.Name} {(muted ? "静音" : "取消静音")}（{affected} 个会话）");
        Osd($"{row.Name} {(muted ? "已静音" : "已取消静音")}", muted ? "🔇" : "🔊");
        Reload();
    }

    private async void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string name } || string.IsNullOrEmpty(name))
        {
            return;
        }

        await ApplyProfileByName(name);
    }

    /// <summary>场景应用核心动作。</summary>
    internal async Task ApplyProfileByName(string name)
    {
        try
        {
            var host = Host;
            var report = await new ProfileService(host.Store, host.Rules)
                .ApplyAsync(host.Coordinator, name, replace: true).ConfigureAwait(true);
            string msg = $"[托盘] 场景 {report.ProfileName}：命中 {report.MatchedProcesses} 进程，" +
                         $"应用 {report.AppliedRoutes.Count} 条，{report.Elapsed.TotalMilliseconds:F0} ms";
            App.AppendFileLog(msg);
            Osd(string.Format(L10n.Get("OsdProfileApplied"), name), "🎚️");
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[托盘] 场景 {name} 应用失败: {ex.Message}");
            Osd(string.Format(L10n.Get("OsdProfileFailed"), name), "⚠️");
        }

        Reload();
    }

    private void Osd(string message, string icon)
    {
        if (App.Settings.OsdEnabled)
        {
            OsdWindow.Show(message, icon);
        }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        ((App)System.Windows.Application.Current).ShowMainWindow();
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        ((App)System.Windows.Application.Current).ExitApplication();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- P/Invoke（64 位兼容） ----------

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);
}

/// <summary>托盘面板会话行。</summary>
public sealed class FlyoutSession
{
    public int ProcessId { get; init; }
    public string Name { get; set; } = string.Empty;
    public string CurrentDevice { get; set; } = "-";
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    public string VolumeText => Muted ? "🔇" : $"{Volume:P0}";
    public string MuteIcon => Muted ? "🔊" : "🔇";
}
