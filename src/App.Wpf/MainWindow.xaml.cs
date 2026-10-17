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
    private System.ComponentModel.ICollectionView? _sessionView;
    private bool _allowClose;
    private List<string> _onlineDevices = new();

    public ObservableCollection<string> Devices { get; } = new();

    public MainWindow()
    {
        // 注意：必须先于 InitializeComponent 创建——XAML 中 CheckBox 的 IsChecked="True"
        // 会在解析阶段立即触发 Checked 事件，其处理器会访问本定时器。
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        InitializeComponent();
        _host = ((App)System.Windows.Application.Current).Host;

        SyncDevices();

        RouteGrid.ItemsSource = _rows;
        PlayingGrid.ItemsSource = _sessions;
        ProfileBox.ItemsSource = _host.Rules.Profiles.Select(p => p.Name).ToList();

        _host.Log += AppendLogThreadSafe;
        _host.RoutesChanged += RefreshThreadSafe;

        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        _sessionView = System.Windows.Data.CollectionViewSource.GetDefaultView(_sessions);
        _sessionView.Filter = SessionFilter;

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
        List<WasapiDevice> online;
        List<string> allNames;
        try
        {
            var all = DeviceManager.EnumerateRenderDevices();
            allNames = all.Select(d => d.FriendlyName).ToList();
            online = all.Where(d => d.State == DeviceState.Active).ToList();
        }
        catch (Exception ex)
        {
            AppendLog($"[设备] 枚举失败: {ex.Message}");
            return;
        }

        _onlineDevices = online.Select(d => d.FriendlyName).ToList();

        // 持久路由指向的离线目标：仍追加进下拉列表（裸名，保证 SelectedItem 能匹配，
        // 防止 ItemsSource 重建把选中值冲成 null 丢数据）。B2 持久化对离线设备同样
        // 有效——设备回归后看门狗自动重挂，所以这些目标必须保持可见可选。
        var offlineTargets = _host.Store.Routes
            .Select(r => r.DeviceName)
            .Where(n => !string.IsNullOrEmpty(n)
                && !_onlineDevices.Contains(n, StringComparer.Ordinal)
                && allNames.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var names = _onlineDevices.Concat(offlineTargets).ToList();

        if (names.SequenceEqual(Devices, StringComparer.Ordinal))
        {
            return;
        }

        Devices.Clear();
        foreach (var n in names)
        {
            Devices.Add(n);
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
            if (string.IsNullOrEmpty(row.DeviceName) || !names.Contains(row.DeviceName, StringComparer.Ordinal))
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
        LogBox.AppendText(line + Environment.NewLine);
        if (AutoScrollCheck?.IsChecked != false)
        {
            LogBox.ScrollToEnd();
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

        StatusText.Text = $"v0.4  ·  路由表 {_host.Store.Routes.Count} 条  |  活动链路 {live.Count} 条  |  日志 {App.LogPath}";
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

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromSender(sender);
        if (row is null)
        {
            return;
        }

        foreach (var process in ProcessFinder.Find(row.Route.ProcessPath))
        {
            using (process)
            {
                _host.Coordinator.RemoveRouteAsync(process.Id, clearEndpoint: true).GetAwaiter().GetResult();
            }
        }
        _host.Store.Remove(row.Route.ProcessPath);
        _rows.Remove(row);
        AppendLog($"[移除] {row.ProcessName}");
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

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

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

    private void RestoreWindowGeometry()
    {
        try
        {
            string path = UiSettingsPath;
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("left", out var l) && root.TryGetProperty("top", out var t))
            {
                Left = l.GetDouble();
                Top = t.GetDouble();
            }
            if (root.TryGetProperty("width", out var w) && root.TryGetProperty("height", out var h))
            {
                Width = Math.Max(MinWidth, w.GetDouble());
                Height = Math.Max(MinHeight, h.GetDouble());
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
            Directory.CreateDirectory(Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath,
                $"{{\"left\":{Left},\"top\":{Top},\"width\":{Width},\"height\":{Height}}}");
        }
        catch
        {
        }
    }

    private static string UiSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio", "ui.json");

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
        set { _deviceName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); }
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
