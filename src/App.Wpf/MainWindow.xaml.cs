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

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 主控制台：路由表编辑（设备/手段/音量）+ 活动链路 + 场景切换 + 日志。
/// 常驻核心由 App.AppHost 提供；本窗口只做展示与命令下发。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
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
        InitializeComponent();
        _host = ((App)System.Windows.Application.Current).Host;

        SyncDevices();

        RouteGrid.ItemsSource = _rows;
        PlayingGrid.ItemsSource = _sessions;
        LiveGrid.ItemsSource = _liveRows;
        ProfileBox.ItemsSource = _host.Rules.Profiles.Select(p => p.Name).ToList();

        _host.Log += AppendLogThreadSafe;
        _host.RoutesChanged += RefreshThreadSafe;
        _host.DeviceListChanged += RefreshThreadSafe;

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

        Loaded += (_, _) => { RestoreWindowGeometry(); Refresh(); };
        Closed += (_, _) => SaveWindowGeometry();
        Closing += Window_Closing;
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
        ProbeText.Text = $"A: {(probeA.Available ? "可用" : "不可用")}  |  B2: {(probeB2.Available ? "可用" : "不可用")}";

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
                ? "设备离线"
                : routeLive.ProcessId == 0
                    ? "等待启动"
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

        StatusText.Text = $"v0.4  ·  路由表 {_host.Store.Routes.Count} 条  |  活动链路 {live.Count} 条  |  日志 {App.LogPath}";
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
                LoopbackText = l.LoopbackActive ? (l.AwaitingDevice ? "暂停(等设备)" : "✅ 运行中") : "—",
                EndpointText = l.EndpointApplied ? "✅ 已写" : "—",
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

    // ---------- 路由行操作 ----------

    private RouteRow? RowFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as RouteRow;

    private void PersistRow(RouteRow row)
    {
        _host.Store.Upsert(row.Route);
        ApplyToRunning(row);
    }

    /// <summary>行改动立即对运行中匹配进程生效。</summary>
    private void ApplyToRunning(RouteRow row)
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
                var report = _host.Coordinator.ApplyRouteAsync(command, row.Route.Mode).GetAwaiter().GetResult();
                AppendLog($"[改] {row.ProcessName} → {row.Route.DeviceName} [{row.Route.Mode}] vol {row.Route.Volume:F2} | " +
                    $"B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}");
            }
        }
        Refresh();
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = RowFromSender(sender);
        if (row is null || row.Route.DeviceName == (sender as ComboBox)?.SelectedItem as string)
        {
            return;
        }

        row.Route.DeviceName = (string)((ComboBox)sender).SelectedItem;
        var device = DeviceManager.ResolveDevice(row.Route.DeviceName);
        row.Route.DeviceId = device.Id;
        PersistRow(row);
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
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
        PersistRow(row);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var row = RowFromSender(sender);
        if (row is null || Math.Abs(row.Route.Volume - (float)e.NewValue) < 0.001f)
        {
            return;
        }

        row.Route.Volume = (float)e.NewValue;
        PersistRow(row);
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

    private void ApplySession_Click(object sender, RoutedEventArgs e)
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
        var report = _host.Coordinator.ApplyRouteAsync(command, RoutingMode.Auto).GetAwaiter().GetResult();
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

            row.State = s.State switch
            {
                PerAppAudio.SessionManager.SessionState.Active => "播放中",
                PerAppAudio.SessionManager.SessionState.Expired => "已退出",
                _ => "静默",
            };
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
            Osd("未取到前台应用", "⚠️");
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
            Osd("切换设备失败", "⚠️");
        }

        RefreshThreadSafe();
    }

    /// <summary>前台应用会话音量增减（delta 有符号，5% 步进）。</summary>
    private void HotkeyVolume(float delta)
    {
        var fg = ForegroundApp();
        if (fg is null)
        {
            Osd("未取到前台应用", "⚠️");
            return;
        }

        var (pid, name) = fg.Value;
        var r = SessionActions.NudgeVolume(pid, delta);
        if (r is null)
        {
            Osd($"{name} 没有音频会话", "⚠️");
            return;
        }

        var (before, after, affected) = r.Value;
        LogBoth($"[热键] {name} 音量 {before:F2} → {after:F2}（{affected} 个会话）");
        Osd($"{name} 音量 {after:P0}", after == 0 ? "🔇" : "🔊");
    }

    /// <summary>前台应用静音切换。</summary>
    private void HotkeyMuteToggle()
    {
        var fg = ForegroundApp();
        if (fg is null)
        {
            Osd("未取到前台应用", "⚠️");
            return;
        }

        var (pid, name) = fg.Value;
        var r = SessionActions.ToggleMute(pid);
        if (r is null)
        {
            Osd($"{name} 没有音频会话", "⚠️");
            return;
        }

        var (muted, affected) = r.Value;
        LogBoth($"[热键] {name} {(muted ? "静音" : "取消静音")}（{affected} 个会话）");
        Osd($"{name} {(muted ? "已静音" : "已取消静音")}", muted ? "🔇" : "🔊");
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

    private void MenuMinimizeToTray_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>退出：经 App.ExitApplication 走 AllowClose + Shutdown，正常拆除路由与托盘。</summary>
    private void MenuExit_Click(object sender, RoutedEventArgs e)
        => ((App)System.Windows.Application.Current).ExitApplication();

    private void MenuUsage_Click(object sender, RoutedEventArgs e) => InfoWindow.Usage().ShowDialog();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) => InfoWindow.About().ShowDialog();

    /// <summary>用记事本打开数据文件（路由表 / 规则 / 日志），便于查看与编辑。</summary>
    private void MenuOpenRoutes_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(RouteStore.DefaultPath, "路由表");

    private void MenuOpenRules_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(RuleSet.DefaultPath, "规则文件");

    private void MenuOpenLog_Click(object sender, RoutedEventArgs e)
        => OpenInNotepad(App.LogPath, "日志文件");

    private static void OpenInNotepad(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show($"未找到{label}：\n{path}\n\n（程序尚未生成过该文件）",
                    "PerAppAudio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打开{label}失败：{ex.Message}",
                "PerAppAudio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        _logView?.Refresh();
    }

    private void ReapplyAll_Click(object sender, RoutedEventArgs e)
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
                    var report = _host.Coordinator.ApplyRouteAsync(command, stored.Mode).GetAwaiter().GetResult();
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

        if (OnlyPlayingCheck?.IsChecked == true && row.State != "播放中")
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

    private string _state = "静默";
    public string State
    {
        get => _state;
        set
        {
            _state = value;
            StateBrush = value switch
            {
                "播放中" => System.Windows.Media.Brushes.ForestGreen,
                "已退出" => System.Windows.Media.Brushes.Gray,
                _ => System.Windows.Media.Brushes.Orange,
            };
            OnPropertyChanged();
        }
    }

    private System.Windows.Media.Brush _stateBrush = System.Windows.Media.Brushes.Orange;
    public System.Windows.Media.Brush StateBrush
    {
        get => _stateBrush;
        private set { _stateBrush = value; OnPropertyChanged(); }
    }

    private string _currentDevice = "-";
    public string CurrentDevice
    {
        get => _currentDevice;
        set { _currentDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); }
    }

    /// <summary>目标设备与当前设备相同时无需改道（按钮置灰）。</summary>
    public bool CanApply => !string.Equals(DeviceName, CurrentDevice, StringComparison.OrdinalIgnoreCase);

    /// <summary>排序权重：播放中优先。</summary>
    public int SortRank => State == "播放中" ? 0 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
