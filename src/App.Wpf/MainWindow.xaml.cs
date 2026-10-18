using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PerAppAudio.RoutingEngine;
using PerAppAudio.RuleEngine;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;
using ComboBox = System.Windows.Controls.ComboBox;
using WpfButton = System.Windows.Controls.Button;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 主控制台：路由表编辑（设备/手段/音量）+ 活动链路 + 场景切换 + 日志。
/// 常驻核心由 App.AppHost 提供；本窗口只做展示与命令下发。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _refreshCoalesce;
    private readonly DispatcherTimer _volumeDebounce;
    private readonly SoundPadService _soundpad = new();
    private RouteRow? _volumePendingRow;
    private readonly ObservableCollection<RouteRow> _rows = new();
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<LiveRouteRow> _liveRows = new();
    private readonly ObservableCollection<LogEntry> _logEntries = new();
    private System.ComponentModel.ICollectionView? _sessionView;
    private System.ComponentModel.ICollectionView? _logView;
    private bool _allowClose;
    private List<string> _onlineDevices = new();
    private AppSettings _settings = AppSettings.Load();

    public ObservableCollection<string> Devices { get; } = new();

    public MainWindow()
    {
        // 注意：必须先于 InitializeComponent 创建——XAML 中 CheckBox 的 IsChecked="True"
        // 会在解析阶段立即触发 Checked 事件，其处理器会访问本定时器。
        // 自动刷新默认关闭（IsChecked="False"），间隔 10s：避免抢焦点/打扰用户选设备。
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        // 刷新合并尾沿定时器（300ms）：扫描/设备事件连续到达时只刷一次
        _refreshCoalesce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshCoalesce.Tick += RefreshCoalesce_Tick;
        // 音量滑条防抖（150ms）：拖动过程中连续 ValueChanged 会引发 apply 风暴
        _volumeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _volumeDebounce.Tick += VolumeDebounce_Tick;
        InitializeComponent();
        _host = ((App)System.Windows.Application.Current).Host;

        SyncDevices();

        RouteGrid.ItemsSource = _rows;
        PlayingGrid.ItemsSource = _sessions;
        LiveGrid.ItemsSource = _liveRows;
        ProfileBox.ItemsSource = _host.Rules.Profiles.Select(p => p.Name).ToList();

        _host.Log += AppendLogThreadSafe;
        _host.RoutesChanged += ScheduleRefresh;
        _host.DeviceListChanged += ScheduleRefresh;

        _refreshTimer.Tick += (_, _) => Refresh();

        // 刷新策略来自设置（默认 10s / 关闭）
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.RefreshSeconds));
        AutoRefreshCheck.IsChecked = _settings.AutoRefresh;
        _refreshTimer.IsEnabled = _settings.AutoRefresh;

        _sessionView = System.Windows.Data.CollectionViewSource.GetDefaultView(_sessions);
        _sessionView.Filter = SessionFilter;

        _logView = System.Windows.Data.CollectionViewSource.GetDefaultView(_logEntries);
        _logView.Filter = LogFilter;
        LogList.ItemsSource = _logEntries;
        LoadSettingsPage();

        Loaded += (_, _) => { RestoreWindowGeometry(); Refresh(); RefreshSoundButtons(); };
        Closed += (_, _) => { SaveWindowGeometry(); _soundpad.Dispose(); };
        Closing += Window_Closing;

        // 音板播放状态变化（播放线程触发 → marshalling 回 UI 线程刷新按钮高亮）
        _soundpad.StateChanged += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshSoundButtons);

        // 切到音板页时重扫（用户可能刚往文件夹放了新文件）
        MainTabs.SelectionChanged += (_, _) =>
        {
            if (MainTabs.SelectedIndex == 4)
            {
                RefreshSoundButtons();
            }
        };
    }

    // ---------- 展示 ----------

    /// <summary>
    /// 同步设备下拉列表（构造时 + 每次刷新做一次，支持设备热插拔）。
    /// 仅在设备集合真的变化时才重建，避免重置 ComboBox 的选中状态。
    /// </summary>
    private void SyncDevices()
    {
        List<WasapiDevice> all;
        List<WasapiDevice> online;
        try
        {
            all = DeviceManager.EnumerateRenderDevices().ToList();
            online = all.Where(d => d.State == DeviceState.Active).ToList();
        }
        catch (Exception ex)
        {
            AppendLog($"[设备] 枚举失败: {ex.Message}");
            return;
        }

        _onlineDevices = online.Select(d => d.FriendlyName).ToList();

        // 持久路由指向的离线目标：仍追加进下拉列表（保证 SelectedItem 能匹配，
        // 防止列表重建把选中值冲成 null 丢数据）。B2 持久化对离线设备同样
        // 有效——设备回归后看门狗自动重挂，所以这些目标必须保持可见可选。
        var offlineTargets = all
            .Where(d => d.State != DeviceState.Active
                && _host.Store.Routes.Any(r => string.Equals(r.DeviceName, d.FriendlyName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 可见设备 = 在线 + 离线路由目标。同名端点在此集合内去歧义（追加 ID 尾段），
        // 仅影响展示：下拉框/「当前设备」列经 DeviceNameConverter 显示，存储与解析仍用裸名。
        var visible = online.Concat(offlineTargets).ToList();
        DeviceNaming.Refresh(visible);

        var names = visible.Select(d => DeviceNaming.Display(d.FriendlyName)).ToList();
        var bareNames = visible.Select(d => d.FriendlyName).ToList();

        // 集合比较（忽略顺序）：COM 设备枚举顺序可能抖动，顺序敏感比较会误判「有变化」，
        // 导致每次刷新都重建列表。仅顺序不同时直接返回，不碰集合。
        var current = Devices.ToList();
        bool sameSet = current.Count == names.Count
            && !names.Except(current, StringComparer.Ordinal).Any();
        if (sameSet)
        {
            return;
        }

        // 增量更新：只删消失项、追加新增项，绝不 Clear()。
        // WPF 对「未选中项」的增删不会关闭已打开的下拉弹窗；而 Clear() 会瞬间清空
        // 打开中的下拉框——这就是「未选择时选项消失」的直接根因。
        foreach (var gone in current.Where(n => !names.Contains(n, StringComparer.Ordinal)).ToList())
        {
            Devices.Remove(gone);
        }
        foreach (var add in names.Where(n => !current.Contains(n, StringComparer.Ordinal)))
        {
            Devices.Add(add);
        }

        AppendLog($"[设备] 在线 {_onlineDevices.Count} 个；路由目标离线 {offlineTargets.Count} 个" +
            (offlineTargets.Count > 0 ? $"（{string.Join("、", offlineTargets)}）" : ""));
        App.AppendFileLog($"[设备] 在线 {_onlineDevices.Count} 个；路由目标离线 {offlineTargets.Count} 个" +
            (offlineTargets.Count > 0 ? $"（{string.Join("、", offlineTargets)}）" : ""));

        // 在线列表变化后选中项可能失效：正在播放行回退到默认在线设备，避免 null 进入路由
        var fallback = online.FirstOrDefault(d => d.IsDefaultConsole)?.FriendlyName
            ?? online.FirstOrDefault()?.FriendlyName
            ?? string.Empty;
        foreach (var row in _sessions)
        {
            if (string.IsNullOrEmpty(row.DeviceName) || !bareNames.Contains(row.DeviceName, StringComparer.Ordinal))
            {
                row.DeviceName = fallback;
            }
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>音板服务（自测/调试入口）。</summary>
    public SoundPadService SoundPad => _soundpad;

    public void AllowClose() => _allowClose = true;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void AppendLog(string line)
    {
        _logEntries.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), line, LogEntry.Classify(line)));

        // 上限 2000 条：看门狗/刷新高频写日志，防止长跑内存无限增长
        while (_logEntries.Count > 2000)
        {
            _logEntries.RemoveAt(0);
        }

        if (AutoScrollCheck?.IsChecked != false)
        {
            LogList.ScrollIntoView(_logEntries[^1]);
        }
    }

    private void AppendLogThreadSafe(string line)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AppendLog(line));

    private void RefreshThreadSafe()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);

    private void Refresh()
    {
        _host.Store.Load();
        SyncDevices();

        var (probeA, probeB2) = _host.Probes;
        ProbeText.Text = string.Format(L10n.Get("ProbeFormat"),
            probeA.Available ? "✅" : "❌", probeB2.Available ? "✅" : "❌");

        var live = _host.Coordinator.GetLiveRoutes();

        // 路由表 → 行（保留已有行的输入状态，仅更新数据）
        var rowsByPath = _rows.ToDictionary(r => r.Route.ProcessPath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stored in _host.Store.Routes)
        {
            seen.Add(stored.ProcessPath);
            if (!rowsByPath.TryGetValue(stored.ProcessPath, out var row))
            {
                row = new RouteRow(stored);
                _rows.Add(row);
            }
            else
            {
                row.Route = stored;
            }

            var routeLive = live.FirstOrDefault(l =>
                string.Equals(l.ProcessPath, stored.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(l.ProcessPath), Path.GetFileName(stored.ProcessPath), StringComparison.OrdinalIgnoreCase));
            bool deviceOnline = _onlineDevices.Contains(stored.DeviceName, StringComparer.Ordinal);
            string status = !deviceOnline
                ? L10n.Get("RouteStatusOffline")
                : routeLive.ProcessId == 0
                    ? L10n.Get("RouteStatusWaiting")
                    : routeLive.LoopbackActive
                        ? (routeLive.EndpointApplied ? "A+B2 ✅" : "A ✅")
                        : (routeLive.EndpointApplied ? "B2 ✅" : "…");
            row.Status = status;
        }

        foreach (var key in rowsByPath.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var row = rowsByPath[key];
            _rows.Remove(row);
        }

        // 「正在播放」列表：会话枚举（NAudio，真实 PID）+ 保留用户选择
        RefreshPlayingSessions();

        // 活动链路列表
        RefreshLiveLinks();

        StatusText.Text = string.Format(L10n.Get("StatusFormat"),
            _host.Store.Routes.Count, live.Count, App.LogPath);
    }

    /// <summary>刷新「活动链路」页：直接重建（行数少，状态每次刷新都变）。</summary>
    private void RefreshLiveLinks()
    {
        _liveRows.Clear();
        foreach (var l in _host.Coordinator.GetLiveRoutes()
            .OrderByDescending(l => l.LoopbackActive)
            .ThenBy(l => l.ProcessId))
        {
            _liveRows.Add(new LiveRouteRow
            {
                ProcessId = l.ProcessId,
                ProcessName = string.IsNullOrEmpty(Path.GetFileName(l.ProcessPath)) ? $"PID {l.ProcessId}" : Path.GetFileName(l.ProcessPath),
                DeviceName = l.TargetDevice,
                ModeText = l.Mode switch
                {
                    RoutingMode.Loopback => "a",
                    RoutingMode.Endpoint => "b",
                    _ => "auto",
                },
                LoopbackText = l.LoopbackActive ? (l.AwaitingDevice ? L10n.Get("LivePaused") : L10n.Get("LiveRunning")) : "—",
                EndpointText = l.EndpointApplied ? L10n.Get("LiveWritten") : "—",
                AppliedAtText = l.AppliedAt.ToString("HH:mm:ss"),
            });
        }

        LiveEmptyHint.Visibility = _liveRows.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>「断开」：拆除单条活动链路（A + B2 覆盖），保留路由表记录。</summary>
    private async void BreakLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LiveRouteRow row)
        {
            return;
        }

        try
        {
            var result = await _host.Coordinator.RemoveRouteAsync(row.ProcessId, clearEndpoint: true);
            AppendLog($"[断开] {row.ProcessName}（PID {row.ProcessId}）：{result.Message}");
            App.AppendFileLog($"[断开] {row.ProcessName}（PID {row.ProcessId}）：{result.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"[断开] {row.ProcessName} 失败: {ex.Message}");
        }

        Refresh();
    }

    // ---------- 刷新合并（UI 卡顿修复） ----------

    /// <summary>
    /// 非用户触发的刷新统一走这里：300ms 尾沿合并。
    /// 全量刷新约 300ms（会话枚举），扫描/设备事件/看门狗可能连续到达，
    /// 直接刷会让 UI 线程被反复占满——合并后一次burst只刷一次。
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_refreshCoalesce.IsEnabled)
        {
            return;
        }
        _refreshCoalesce.Start();
    }

    private void RefreshCoalesce_Tick(object? sender, EventArgs e)
    {
        _refreshCoalesce.Stop();
        Refresh();
    }

    // ---------- 路由行操作 ----------

    private RouteRow? RowFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as RouteRow;

    private Task PersistRow(RouteRow row)
    {
        _host.Store.Upsert(row.Route);
        return ApplyToRunning(row);
    }

    /// <summary>行改动立即对运行中匹配进程生效（异步，不阻塞 UI 线程）。</summary>
    private async Task ApplyToRunning(RouteRow row)
    {
        foreach (var process in ProcessFinder.Find(row.Route.ProcessPath))
        {
            using (process)
            {
                var device = DeviceManager.ResolveDevice(row.Route.DeviceName);
                var command = new RoutingCommand(
                    process.Id,
                    RouteStore.ResolveProcessPath(process.Id),
                    new AudioDeviceRef(device.FriendlyName, device.Id),
                    row.Route.Volume,
                    row.Route.Mute);
                var report = await _host.Coordinator.ApplyRouteAsync(command, row.Route.Mode);
                AppendLog($"[改] {row.ProcessName} → {row.Route.DeviceName} [{row.Route.Mode}] vol {row.Route.Volume:F2} | " +
                    $"B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}");
            }
        }
        Refresh();
    }

    private async void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = RowFromSender(sender);
        if (row is null || row.Route.DeviceName == (sender as ComboBox)?.SelectedItem as string)
        {
            return;
        }

        row.Route.DeviceName = (string)((ComboBox)sender).SelectedItem;
        var device = DeviceManager.ResolveDevice(row.Route.DeviceName);
        row.Route.DeviceId = device.Id;
        await PersistRow(row);
    }

    private async void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = RowFromSender(sender);
        if (row is null)
        {
            return;
        }

        var newMode = (string)((ComboBoxItem)((ComboBox)sender).SelectedItem).Tag;
        if (row.Route.Mode == ParseMode(newMode))
        {
            return;
        }

        row.Route.Mode = ParseMode(newMode);
        await PersistRow(row);
    }

    /// <summary>音量滑条：只更新绑定值，150ms 防抖后才真正应用（避免拖动时 apply 风暴）。</summary>
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var row = RowFromSender(sender);
        if (row is null || Math.Abs(row.Route.Volume - (float)e.NewValue) < 0.001f)
        {
            return;
        }

        row.Route.Volume = (float)e.NewValue;
        _volumePendingRow = row;
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    private async void VolumeDebounce_Tick(object? sender, EventArgs e)
    {
        _volumeDebounce.Stop();
        if (_volumePendingRow is not { } row)
        {
            return;
        }
        _volumePendingRow = null;
        await PersistRow(row);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromSender(sender);
        if (row is null)
        {
            return;
        }

        // 双保险拆链：①按进程路径匹配所有活动路由（路径解析不一致也能拆到）；
        // ②再对运行中的匹配进程逐个拆（覆盖 _live 之外的存量链路）。
        int torn = await _host.Coordinator.RemoveRoutesByPathAsync(row.Route.ProcessPath);
        foreach (var process in ProcessFinder.Find(row.Route.ProcessPath))
        {
            using (process)
            {
                await _host.Coordinator.RemoveRouteAsync(process.Id, clearEndpoint: true);
            }
        }

        _host.Store.Remove(row.Route.ProcessPath);
        _rows.Remove(row);
        AppendLog($"[移除] {row.ProcessName}（拆除活动链路 {torn} 条）");
        Refresh();
    }

    // ---------- 场景 ----------

    private async void ApplySession_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as SessionRow;
        if (row is null || row.ProcessId <= 0)
        {
            return;
        }

        if (string.IsNullOrEmpty(row.DeviceName))
        {
            AppendLog("[应用] 未选择输出设备（未检测到渲染设备）");
            return;
        }

        var device = DeviceManager.ResolveDevice(row.DeviceName);
        string processPath = RouteStore.ResolveProcessPath(row.ProcessId);

        _host.Store.Upsert(new StoredRoute
        {
            ProcessPath = processPath,
            DeviceId = device.Id,
            DeviceName = device.FriendlyName,
            Mode = RoutingMode.Auto,
            Volume = row.Volume,
        });

        var command = new RoutingCommand(row.ProcessId, processPath,
            new AudioDeviceRef(device.FriendlyName, device.Id), row.Volume, false);
        var report = await _host.Coordinator.ApplyRouteAsync(command, RoutingMode.Auto);
        AppendLog($"[应用] {row.ProcessName}（PID {row.ProcessId}）→ {device.FriendlyName} vol {row.Volume:F2} | " +
            $"B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}");
        Refresh();
    }

    /// <summary>刷新「正在播放」列表：保留用户已选的设备/音量。</summary>
    private void RefreshPlayingSessions()
    {
        var sessions = WasapiSessionSource.EnumerateSessions();
        var seen = new HashSet<int>();

        foreach (var s in sessions)
        {
            if (s.ProcessId <= 0)
            {
                continue; // 系统声音不显示
            }
            seen.Add(s.ProcessId);

            var row = _sessions.FirstOrDefault(r => r.ProcessId == s.ProcessId);
            if (row is null)
            {
                row = new SessionRow
                {
                    ProcessId = s.ProcessId,
                    ProcessName = string.IsNullOrEmpty(Path.GetFileName(s.ProcessPath)) || s.ProcessPath.StartsWith("(")
                        ? s.DisplayName
                        : Path.GetFileName(s.ProcessPath),
                    DisplayName = s.DisplayName,
                    DeviceName = GetDefaultDevice(),
                    Volume = 1f,
                };
                _sessions.Add(row);
            }

            row.StateKind = s.State;
        }

        foreach (var stale in _sessions.Where(r => !seen.Contains(r.ProcessId)).ToList())
        {
            _sessions.Remove(stale);
        }

        // 当前设备（会话所在设备）
        var deviceOf = WasapiSessionSource.GetDeviceOfProcesses();
        foreach (var row in _sessions)
        {
            row.CurrentDevice = deviceOf.TryGetValue(row.ProcessId, out var dev) ? dev : "-";
        }

        PlayingEmptyHint.Visibility = _sessions.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        _sessionView?.Refresh();
    }

    private static string GetDefaultDevice()
    {
        // 只从在线设备里选默认：幽灵端点（NotPresent/Unplugged）不能作为默认输出
        var devices = DeviceManager.EnumerateRenderDevices(DeviceState.Active);
        if (devices.Count == 0)
        {
            return string.Empty;
        }

        var d = devices.FirstOrDefault(x => x.IsDefaultConsole) ?? devices[0];
        return d.FriendlyName;
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name)
        {
            return;
        }

        _ = ApplyProfileAsync(name, replace: true);
    }

    public async Task ApplyProfileAsync(string name, bool replace)
    {
        try
        {
            var report = await new ProfileService(_host.Store, _host.Rules)
                .ApplyAsync(_host.Coordinator, name, replace).ConfigureAwait(false);
            AppendLog($"[场景] {report.ProfileName}：命中 {report.MatchedProcesses} 进程，应用 {report.AppliedRoutes.Count} 条，{report.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            AppendLog($"[场景] 失败: {ex.Message}");
        }
        RefreshThreadSafe();
    }

    // ---------- 交互增强 ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _sessionView?.Refresh();

    private void Filter_Changed(object sender, RoutedEventArgs e) => _sessionView?.Refresh();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshTimer is null) return; // XAML 初始化期字段尚未就绪
        _refreshTimer.IsEnabled = AutoRefreshCheck?.IsChecked == true;
    }

    private void RefreshNow_Click(object sender, RoutedEventArgs e) => Refresh();

    // ---------- 全局热键动作 ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>当前前台窗口的进程（排除自身）。无音频会话时返回 null。</summary>
    private (int Pid, string Name)? ForegroundApp()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return ((int)pid, p.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>热键分发（HotkeyManager 回调，可能在非 UI 线程——全部 Dispatcher 回来）。</summary>
    public void DispatchHotkey(int id)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // 音板热键（ID >= 100）：按音效名播放/停止
            if (App.Hotkeys is { } mgr && mgr.IsSoundpadHotkey(id))
            {
                string? name = mgr.GetSoundpadName(id);
                if (name is not null)
                {
                    PlaySoundByName(name);
                }
                return;
            }

            switch (id)
            {
                case HotkeyManager.IdNextOutput: HotkeyNextOutput(); break;
                case HotkeyManager.IdVolumeUp: HotkeyVolume(+0.05f); break;
                case HotkeyManager.IdVolumeDown: HotkeyVolume(-0.05f); break;
                case HotkeyManager.IdMute: HotkeyMuteToggle(); break;
                case HotkeyManager.IdShowWindow: ShowFromTray(); break;
                case HotkeyManager.IdShowFlyout: TrayFlyoutWindow.ShowAtTray(); break;
            }
        });
    }

    private void Osd(string message, string icon)
    {
        if (App.Settings.OsdEnabled)
        {
            OsdWindow.Show(message, icon);
        }
    }

    /// <summary>UI 日志 + 文件日志双写（热键动作需要落盘留痕）。</summary>
    private void LogBoth(string line)
    {
        AppendLog(line);
        App.AppendFileLog(line);
    }

    /// <summary>把前台应用的音频送到下一个在线输出设备（循环），并写入持久路由。</summary>
    private async void HotkeyNextOutput()
    {
        var fg = ForegroundApp();
        if (fg is null)
        {
            Osd(L10n.Get("OsdNoForeground"), "⚠️");
            return;
        }

        var (pid, name) = fg.Value;
        try
        {
            var result = await SessionActions.CycleToNextDeviceAsync(_host, pid, name);
            LogBoth($"[热键] {result.Detail}");
            Osd(result.Message, result.Success ? "🔊" : "⚠️");
        }
        catch (Exception ex)
        {
            LogBoth($"[热键] {name} 切换设备失败: {ex.Message}");
            Osd(L10n.Get("OsdSwitchFailed"), "⚠️");
        }

        RefreshThreadSafe();
    }

    /// <summary>前台应用会话音量增减（delta 有符号，5% 步进）。</summary>
    private void HotkeyVolume(float delta)
    {
        var fg = ForegroundApp();
        if (fg is null)
        {
            Osd(L10n.Get("OsdNoForeground"), "⚠️");
            return;
        }

        var (pid, name) = fg.Value;
        var r = SessionActions.NudgeVolume(pid, delta);
        if (r is null)
        {
            Osd(string.Format(L10n.Get("OsdNoSessionApp"), name), "⚠️");
            return;
        }

        var (before, after, affected) = r.Value;
        LogBoth($"[热键] {name} 音量 {before:F2} → {after:F2}（{affected} 个会话）");
        Osd(string.Format(L10n.Get("OsdVolume"), name, after), after == 0 ? "🔇" : "🔊");
    }

    /// <summary>前台应用静音切换。</summary>
    private void HotkeyMuteToggle()
    {
        var fg = ForegroundApp();
        if (fg is null)
        {
            Osd(L10n.Get("OsdNoForeground"), "⚠️");
            return;
        }

        var (pid, name) = fg.Value;
        var r = SessionActions.ToggleMute(pid);
        if (r is null)
        {
            Osd(string.Format(L10n.Get("OsdNoSessionApp"), name), "⚠️");
            return;
        }

        var (muted, affected) = r.Value;
        LogBoth($"[热键] {name} {(muted ? "静音" : "取消静音")}（{affected} 个会话）");
        Osd(string.Format(L10n.Get(muted ? "OsdMuted" : "OsdUnmuted"), name), muted ? "🔇" : "🔊");
    }

    // ---------- 菜单栏 ----------

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
    }

    private void MenuRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void MenuClearLog_Click(object sender, RoutedEventArgs e) => ClearLog_Click(sender, e);

    private void MenuReapplyAll_Click(object sender, RoutedEventArgs e) => ReapplyAll_Click(sender, e);

    private void MenuTabPlaying_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;

    private void MenuTabRoutes_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private void MenuTabLive_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;

    private void MenuTabLog_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;

    private void MenuTabSoundpad_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;

    // ---------- 音板（SoundPad） ----------

    /// <summary>从当前 UI 选择解析音板播放目标（三态模式）。返回 null = 当前选择不可用（已提示）。</summary>
    private PlayTargets? ResolveSoundpadTargets()
    {
        int mode = SoundpadModeBox.SelectedIndex; // 0 仅扬声器 / 1 仅麦克风 / 2 两者
        string? speakerId = null;
        string? micId = null;

        if (mode != 1)
        {
            speakerId = SoundPadService.ResolveSpeakerDeviceId(
                DeviceNaming.Bare(SoundpadDeviceBox.SelectedItem as string ?? _settings.SoundpadDevice));
        }
        if (mode != 0)
        {
            micId = SoundPadService.ResolveMicDeviceId(
                DeviceNaming.Bare(SoundpadMicDeviceBox.SelectedItem as string ?? _settings.SoundpadMicDevice));
            if (micId is null)
            {
                ShowMicHint(true);
                return null;
            }
        }

        ShowMicHint(false);
        if (speakerId is null && micId is null)
        {
            AppendLog("[音板] 无可用输出设备");
            return null;
        }
        return new PlayTargets(speakerId, micId);
    }

    private void ShowMicHint(bool show)
    {
        SoundpadMicHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMicHint()
    {
        bool micNeeded = SoundpadModeBox.SelectedIndex != 0;
        bool hasMic = SoundPadService.FindVirtualMicDevices().Count > 0;
        ShowMicHint(micNeeded && !hasMic);
    }

    /// <summary>重建音板按钮网格（含右键热键 / 循环菜单、循环标记）。</summary>
    private void RefreshSoundButtons()
    {
        SoundButtons.Children.Clear();
        var sounds = _soundpad.Rescan();

        foreach (var sound in sounds)
        {
            bool playing = _soundpad.IsPlaying(sound.Name);
            bool loop = _settings.SoundpadLoops.Contains(sound.Name, StringComparer.OrdinalIgnoreCase);
            _settings.SoundpadHotkeys.TryGetValue(sound.Name, out var hotkey);

            var button = new WpfButton
            {
                Content = loop ? sound.Name + " [循环]" : sound.Name,
                Style = (Style)FindResource(playing ? "PrimaryButton" : "GhostButton"),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 12,
                Tag = sound,
                ToolTip = string.IsNullOrEmpty(hotkey)
                    ? sound.FilePath
                    : $"{sound.FilePath}\n热键：{hotkey}",
            };
            button.Click += SoundButton_Click;

            var menu = new ContextMenu();
            var hkItem = new MenuItem
            {
                Header = string.IsNullOrEmpty(hotkey) ? "设置热键…" : $"热键：{hotkey}（点击修改）",
            };
            hkItem.Click += (_, _) => OpenSoundHotkey(sound);
            var loopItem = new MenuItem
            {
                Header = loop ? "关闭循环播放" : "开启循环播放",
            };
            loopItem.Click += (_, _) => ToggleSoundLoop(sound);
            menu.Items.Add(hkItem);
            menu.Items.Add(loopItem);
            button.ContextMenu = menu;

            SoundButtons.Children.Add(button);
        }

        SoundpadEmptyHint.Visibility = sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SoundButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SoundItem sound)
        {
            return;
        }
        PlaySound(sound);
    }

    /// <summary>播放/停止一个音效（按当前模式与循环设置）。</summary>
    private void PlaySound(SoundItem sound)
    {
        if (_soundpad.IsPlaying(sound.Name))
        {
            _soundpad.Stop(sound.Name);
            return;
        }
        PlayTargets? targets = ResolveSoundpadTargets();
        if (targets is null)
        {
            return;
        }
        bool loop = _settings.SoundpadLoops.Contains(sound.Name, StringComparer.OrdinalIgnoreCase);
        _soundpad.Toggle(sound, targets, _settings.SoundpadVolume, loop);
    }

    /// <summary>按音效名播放/停止（音板热键入口，自动从音效库匹配）。</summary>
    private void PlaySoundByName(string name)
    {
        var sound = _soundpad.Rescan().FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sound is null)
        {
            return;
        }
        PlaySound(sound);
    }

    private void OpenSoundHotkey(SoundItem sound)
    {
        _settings.SoundpadHotkeys.TryGetValue(sound.Name, out var existing);
        var dlg = new HotkeyCaptureWindow(sound.Name, existing ?? string.Empty);
        if (dlg.ShowDialog() == true)
        {
            if (string.IsNullOrWhiteSpace(dlg.Gesture))
            {
                _settings.SoundpadHotkeys.Remove(sound.Name);
            }
            else
            {
                _settings.SoundpadHotkeys[sound.Name] = dlg.Gesture;
            }
            _settings.Save();
            App.Hotkeys?.RegisterAll(App.Settings);
            RefreshSoundButtons();
            AppendLog(string.IsNullOrWhiteSpace(dlg.Gesture)
                ? $"[音板] 已清除 {sound.Name} 热键"
                : $"[音板] {sound.Name} 热键 = {dlg.Gesture}");
        }
    }

    private void ToggleSoundLoop(SoundItem sound)
    {
        var loopSet = _settings.SoundpadLoops;
        if (loopSet.Contains(sound.Name, StringComparer.OrdinalIgnoreCase))
        {
            loopSet.RemoveAll(x => string.Equals(x, sound.Name, StringComparison.OrdinalIgnoreCase));
            AppendLog($"[音板] {sound.Name} {L10n.Get("SpLoopOff")}");
        }
        else
        {
            loopSet.Add(sound.Name);
            AppendLog($"[音板] {sound.Name} {L10n.Get("SpLoopOn")}");
        }
        _settings.Save();
        RefreshSoundButtons();
    }

    private void SoundpadStopAll_Click(object sender, RoutedEventArgs e)
    {
        _soundpad.StopAll();
    }

    private void SoundpadOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", SoundPadService.SoundsDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[音板] 打开音效文件夹失败: {ex.Message}");
        }
    }

    private void SoundpadMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        _settings.SoundpadMode = SoundpadModeBox.SelectedIndex;
        _settings.Save();
        ResolveSoundpadTargets(); // 立即重算麦克风可用性提示
    }

    private void SoundpadDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        _settings.SoundpadDevice = DeviceNaming.Bare(SoundpadDeviceBox.SelectedItem as string ?? string.Empty);
        _settings.Save();
    }

    private void SoundpadMicDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        string sel = SoundpadMicDeviceBox.SelectedItem as string ?? string.Empty;
        _settings.SoundpadMicDevice = string.Equals(sel, L10n.Get("SpMicAuto"), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : DeviceNaming.Bare(sel);
        _settings.Save();
        ResolveSoundpadTargets();
    }

    private void SoundpadVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings)
        {
            return;
        }
        _settings.SoundpadVolume = (float)Math.Clamp(e.NewValue, 0, 1);
        _settings.Save();
        SoundpadVolumeText.Text = $"{(int)(_settings.SoundpadVolume * 100)}%";
    }

    /// <summary>刷新麦克风设备下拉（虚拟设备列表 + 自动检测项）。</summary>
    private void RefreshMicDevices()
    {
        var list = new List<string> { L10n.Get("SpMicAuto") };
        foreach (var d in SoundPadService.FindVirtualMicDevices())
        {
            list.Add(d.FriendlyName);
        }
        SoundpadMicDeviceBox.ItemsSource = list;

        string sel = string.IsNullOrEmpty(_settings.SoundpadMicDevice)
            ? L10n.Get("SpMicAuto")
            : _settings.SoundpadMicDevice;
        SoundpadMicDeviceBox.SelectedItem = list.FirstOrDefault(x =>
            string.Equals(x, sel, StringComparison.OrdinalIgnoreCase)) ?? list[0];
    }

    private void MenuMinimizeToTray_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>退出：经 App.ExitApplication 走 AllowClose + Shutdown，正常拆除路由与托盘。</summary>
    private void MenuExit_Click(object sender, RoutedEventArgs e)
        => ((App)System.Windows.Application.Current).ExitApplication();

    private void MenuUsage_Click(object sender, RoutedEventArgs e) => InfoWindow.Usage().ShowDialog();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) => InfoWindow.About().ShowDialog();

    /// <summary>用记事本打开数据文件（路由表 / 规则 / 日志），便于查看与编辑。</summary>
    private void MenuOpenRoutes_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(RouteStore.DefaultPath, L10n.Get("LabelRoutesFile"));

    private void MenuOpenRules_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(RuleSet.DefaultPath, L10n.Get("LabelRulesFile"));

    private void MenuOpenLog_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(App.LogPath, L10n.Get("LabelLogFile"));

    private static void OpenInNotepad(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show(string.Format(L10n.Get("MsgFileNotFound"), label, path),
                    "PerAppAudio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(string.Format(L10n.Get("MsgOpenFailed"), label, ex.Message),
                "PerAppAudio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        _logView?.Refresh();
    }

    private async void ReapplyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var stored in _host.Store.Routes)
        {
            foreach (var process in ProcessFinder.Find(stored.ProcessPath))
            {
                using (process)
                {
                    var device = DeviceManager.ResolveDevice(stored.DeviceName);
                    var command = new RoutingCommand(process.Id, stored.ProcessPath,
                        new AudioDeviceRef(device.FriendlyName, device.Id), stored.Volume, stored.Mute);
                    var report = await _host.Coordinator.ApplyRouteAsync(command, stored.Mode);
                    AppendLog($"[重应用] {Path.GetFileName(stored.ProcessPath)}（PID {process.Id}）→ {device.FriendlyName} | " +
                        $"B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}");
                }
            }
        }
        Refresh();
    }

    /// <summary>会话列表过滤：搜索文本 + 仅播放中。</summary>
    private bool SessionFilter(object item)
    {
        if (item is not SessionRow row)
        {
            return false;
        }

        if (OnlyPlayingCheck?.IsChecked == true && row.StateKind != PerAppAudio.SessionManager.SessionState.Active)
        {
            return false;
        }

        string text = SearchBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        return row.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 日志页 ----------

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();

    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e) => _logView?.Refresh();

    /// <summary>日志过滤：搜索文本 + 级别下拉（全部/信息/警告/错误）。</summary>
    private bool LogFilter(object item)
    {
        if (item is not LogEntry entry)
        {
            return false;
        }

        int level = LogLevelBox?.SelectedIndex ?? 0;
        if (level == 1 && entry.Level != LogLevel.Info)
        {
            return false;
        }
        if (level == 2 && entry.Level != LogLevel.Warn)
        {
            return false;
        }
        if (level == 3 && entry.Level != LogLevel.Error)
        {
            return false;
        }

        string text = LogSearchBox?.Text?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(text)
            || entry.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 设置页 ----------

    /// <summary>把设置载入控件（构造时一次；与 SettingChanged 的保存方向相反，用 _loadingSettings 防循环）。</summary>
    private void LoadSettingsPage()
    {
        _loadingSettings = true;
        try
        {
            StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
            OsdEnabledCheck.IsChecked = _settings.OsdEnabled;
            DefaultAutoRefreshCheck.IsChecked = _settings.AutoRefresh;
            RefreshIntervalBox.SelectedIndex = _settings.RefreshSeconds switch
            {
                <= 2 => 0,
                <= 5 => 1,
                <= 30 => 3,
                _ => 2,
            };
            HotkeyNextOutputBox.Text = _settings.HotkeyNextOutput;
            HotkeyVolumeUpBox.Text = _settings.HotkeyVolumeUp;
            HotkeyVolumeDownBox.Text = _settings.HotkeyVolumeDown;
            HotkeyMuteBox.Text = _settings.HotkeyMute;
            HotkeyShowWindowBox.Text = _settings.HotkeyShowWindow;
            HotkeyShowFlyoutBox.Text = _settings.HotkeyShowFlyout;
            LanguageBox.SelectedIndex = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            // 音板控件（设置里存裸名，下拉框是展示名）
            SoundpadModeBox.SelectedIndex = Math.Clamp(_settings.SoundpadMode, 0, 2);
            if (!string.IsNullOrEmpty(_settings.SoundpadDevice))
            {
                SoundpadDeviceBox.SelectedItem = Devices.FirstOrDefault(d =>
                    string.Equals(DeviceNaming.Bare(d), _settings.SoundpadDevice, StringComparison.OrdinalIgnoreCase));
            }
            RefreshMicDevices();
            SoundpadVolumeSlider.Value = _settings.SoundpadVolume;
            SoundpadVolumeText.Text = $"{(int)(_settings.SoundpadVolume * 100)}%";
            UpdateMicHint();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private bool _loadingSettings;

    /// <summary>设置控件改动：立即保存并应用（刷新策略 / 自启 / OSD）。</summary>
    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.OsdEnabled = OsdEnabledCheck.IsChecked == true;
        _settings.AutoRefresh = DefaultAutoRefreshCheck.IsChecked == true;
        _settings.RefreshSeconds = RefreshIntervalBox.SelectedIndex switch
        {
            0 => 2,
            1 => 5,
            3 => 30,
            _ => 10,
        };
        _settings.Save();

        // 即时应用
        if (_settings.StartWithWindows && !AppSettings.SetStartWithWindows(true))
        {
            AppendLog("[设置] 开机自启写入失败（注册表不可写）");
        }
        if (!_settings.StartWithWindows)
        {
            AppSettings.SetStartWithWindows(false);
        }
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.RefreshSeconds));
        AutoRefreshCheck.IsChecked = _settings.AutoRefresh;
        _refreshTimer.IsEnabled = _settings.AutoRefresh;
        App.Settings = _settings;
    }

    /// <summary>语言切换：保存设置 → 重载语言字典（所有 {loc:Loc} 绑定即时刷新）。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.Language = LanguageBox.SelectedIndex == 1 ? "en" : "zh-CN";
        _settings.Save();
        L10n.Load(_settings.Language);
        App.Settings = _settings;
        AppendLog($"[设置] 界面语言 → {_settings.Language}");
    }

    /// <summary>保存热键配置并通知热键管理器重注册。</summary>
    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _settings.HotkeyNextOutput = HotkeyNextOutputBox.Text.Trim();
        _settings.HotkeyVolumeUp = HotkeyVolumeUpBox.Text.Trim();
        _settings.HotkeyVolumeDown = HotkeyVolumeDownBox.Text.Trim();
        _settings.HotkeyMute = HotkeyMuteBox.Text.Trim();
        _settings.HotkeyShowWindow = HotkeyShowWindowBox.Text.Trim();
        _settings.HotkeyShowFlyout = HotkeyShowFlyoutBox.Text.Trim();
        _settings.Save();

        if (App.Hotkeys is { } hotkeys)
        {
            hotkeys.RegisterAll(_settings);
            AppendLog("[热键] 已按新配置重新注册");
        }
        else
        {
            AppendLog("[热键] 热键管理器未就绪，配置已保存，重启后生效");
        }
    }

    // ---------- 窗口几何（随 AppSettings 持久化） ----------

    private void RestoreWindowGeometry()
    {
        try
        {
            if (double.IsNaN(_settings.Left) || double.IsNaN(_settings.Top))
            {
                return;
            }

            // 多显示器/分辨率变化后坐标可能离屏，兜底不回填
            if (_settings.Left < -10000 || _settings.Top < -10000 ||
                _settings.Left > 30000 || _settings.Top > 30000)
            {
                return;
            }

            Left = _settings.Left;
            Top = _settings.Top;
            if (_settings.Width >= MinWidth)
            {
                Width = _settings.Width;
            }
            if (_settings.Height >= MinHeight)
            {
                Height = _settings.Height;
            }
        }
        catch
        {
            // 设置损坏不影响启动
        }
    }

    private void SaveWindowGeometry()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Width = Width;
                _settings.Height = Height;
            }
            _settings.Save();
        }
        catch
        {
        }
    }

    private static RoutingMode ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "a" or "loopback" => RoutingMode.Loopback,
        "b" or "endpoint" => RoutingMode.Endpoint,
        _ => RoutingMode.Auto,
    };
}

/// <summary>路由表行视图模型（编辑即持久）。</summary>
public sealed class RouteRow : INotifyPropertyChanged
{
    private StoredRoute _route;
    private string _status = "…";

    public RouteRow(StoredRoute route) => _route = route;

    public StoredRoute Route
    {
        get => _route;
        set
        {
            _route = value;
            OnPropertyChanged(nameof(ProcessName));
            OnPropertyChanged(nameof(DeviceName));
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(Volume));
        }
    }

    public string ProcessName => Path.GetFileName(_route.ProcessPath);

    public string DeviceName
    {
        get => _route.DeviceName;
        set
        {
            // 同 SessionRow：忽略 null/空回写，防止设备列表更新瞬间选中值被清掉
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            _route.DeviceName = value;
            OnPropertyChanged();
        }
    }

    public string Mode
    {
        get => _route.Mode switch
        {
            RoutingMode.Loopback => "a",
            RoutingMode.Endpoint => "b",
            _ => "auto",
        };
        set
        {
            _route.Mode = value switch
            {
                "a" => RoutingMode.Loopback,
                "b" => RoutingMode.Endpoint,
                _ => RoutingMode.Auto,
            };
            OnPropertyChanged();
        }
    }

    public float Volume
    {
        get => _route.Volume;
        set
        {
            _route.Volume = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>「活动链路」行：Coordinator 持有中的实时路由快照。</summary>
public sealed class LiveRouteRow
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string ModeText { get; init; } = "auto";
    public string LoopbackText { get; init; } = "—";
    public string EndpointText { get; init; } = "—";
    public string AppliedAtText { get; init; } = string.Empty;
}

/// <summary>日志级别（由内容自动分类：[严重]/❌=错误，⚠️/失败=警告，其余=信息）。</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>日志行（时间 + 原文 + 级别），供日志页搜索/级别过滤。</summary>
public sealed record LogEntry(string Time, string Text, LogLevel Level)
{
    public static LogLevel Classify(string line) =>
        line.Contains("[严重]", StringComparison.Ordinal) || line.Contains('❌')
            ? LogLevel.Error
            : line.Contains('⚠') || line.Contains("失败", StringComparison.Ordinal)
                ? LogLevel.Warn
                : LogLevel.Info;

    public System.Windows.Media.Brush LevelBrush => Level switch
    {
        LogLevel.Error => System.Windows.Media.Brushes.IndianRed,
        LogLevel.Warn => System.Windows.Media.Brushes.DarkOrange,
        _ => System.Windows.Media.Brushes.Black,
    };
}

/// <summary>「正在播放」会话行（自动检测列表）。</summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    private string _deviceName = string.Empty;
    public string DeviceName
    {
        get => _deviceName;
        set
        {
            // 防御绑定回写 null/空：设备列表增量更新的瞬间 SelectedItem 可能被置 null，
            // 若回写会清空用户已选设备（表现为「未选择时选项/选中消失」）。空值一律忽略。
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            _deviceName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApply));
        }
    }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set { _volume = value; OnPropertyChanged(); }
    }

    private PerAppAudio.SessionManager.SessionState _stateKind;

    /// <summary>会话状态（枚举真相源；展示与颜色均由此派生，语言切换不错乱）。</summary>
    public PerAppAudio.SessionManager.SessionState StateKind
    {
        get => _stateKind;
        set
        {
            _stateKind = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(SortRank));
        }
    }

    public string State => _stateKind switch
    {
        PerAppAudio.SessionManager.SessionState.Active => L10n.Get("StatePlaying"),
        PerAppAudio.SessionManager.SessionState.Expired => L10n.Get("StateExpired"),
        _ => L10n.Get("StateSilent"),
    };

    public System.Windows.Media.Brush StateBrush => _stateKind switch
    {
        PerAppAudio.SessionManager.SessionState.Active => System.Windows.Media.Brushes.ForestGreen,
        PerAppAudio.SessionManager.SessionState.Expired => System.Windows.Media.Brushes.Gray,
        _ => System.Windows.Media.Brushes.Orange,
    };

    private string _currentDevice = "-";
    public string CurrentDevice
    {
        get => _currentDevice;
        set { _currentDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); }
    }

    /// <summary>目标设备与当前设备相同时无需改道（按钮置灰）。</summary>
    public bool CanApply => !string.Equals(DeviceName, CurrentDevice, StringComparison.OrdinalIgnoreCase);

    /// <summary>排序权重：播放中优先。</summary>
    public int SortRank => _stateKind == PerAppAudio.SessionManager.SessionState.Active ? 0 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
