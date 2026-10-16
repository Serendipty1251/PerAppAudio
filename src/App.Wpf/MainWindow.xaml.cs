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
    private bool _allowClose;

    public ObservableCollection<string> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _host = ((App)System.Windows.Application.Current).Host;

        foreach (var d in DeviceManager.EnumerateRenderDevices())
        {
            Devices.Add(d.FriendlyName);
        }

        RouteGrid.ItemsSource = _rows;
        AddDeviceBox.ItemsSource = Devices;
        ProfileBox.ItemsSource = _host.Rules.Profiles.Select(p => p.Name).ToList();

        _host.Log += AppendLogThreadSafe;
        _host.RoutesChanged += RefreshThreadSafe;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Loaded += (_, _) => Refresh();
        Closing += Window_Closing;
    }

    // ---------- 展示 ----------

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
        LogBox.ScrollToEnd();
    }

    private void AppendLogThreadSafe(string line)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AppendLog(line));

    private void RefreshThreadSafe()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);

    private void Refresh()
    {
        var (probeA, probeB2) = _host.Probes;
        ProbeText.Text = $"A: {(probeA.Available ? "可用" : "不可用")}  |  B2: {(probeB2.Available ? "可用" : "不可用")}";

        _host.Store.Load();
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
            string status = routeLive.ProcessId == 0
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

        // 活动链路
        LiveBox.Items.Clear();
        foreach (var l in live)
        {
            LiveBox.Items.Add($"PID {l.ProcessId}  {Path.GetFileName(l.ProcessPath)} → {l.TargetDevice}  " +
                $"[{(l.LoopbackActive ? "A✅" : "A—")} {(l.EndpointApplied ? "B2✅" : "B2—")}]");
        }

        StatusText.Text = $"路由表 {_host.Store.Routes.Count} 条  |  活动链路 {live.Count} 条  |  日志 {App.LogPath}";
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

    private void AddRoute_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PidBox.Text.Trim(), out int pid) || pid <= 0)
        {
            System.Windows.MessageBox.Show(this, "请输入有效 PID（任务管理器可查）", "PerAppAudio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AddDeviceBox.SelectedItem is not string deviceName)
        {
            return;
        }

        string processPath;
        try
        {
            using var process = Process.GetProcessById(pid);
            processPath = RouteStore.ResolveProcessPath(pid);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"进程不存在: {ex.Message}", "PerAppAudio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = DeviceManager.ResolveDevice(deviceName);
        var stored = new StoredRoute
        {
            ProcessPath = processPath,
            DeviceId = device.Id,
            DeviceName = device.FriendlyName,
            Mode = RoutingMode.Auto,
            Volume = 1f,
        };
        _host.Store.Upsert(stored);

        var command = new RoutingCommand(pid, processPath, new AudioDeviceRef(device.FriendlyName, device.Id), 1f, false);
        _host.Coordinator.ApplyRouteAsync(command, RoutingMode.Auto).GetAwaiter().GetResult();
        AppendLog($"[添加] PID {pid}（{Path.GetFileName(processPath)}）→ {device.FriendlyName}");
        PidBox.Clear();
        Refresh();
    }

    // ---------- 场景 ----------

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
