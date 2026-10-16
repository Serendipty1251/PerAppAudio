using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerAppAudio.RoutingEngine;

namespace PerAppAudio.RuleEngine;

/// <summary>
/// 持久化路由表（P1）。
/// 产品自身的每程序路由记录，落在 %APPDATA%\PerAppAudio\routes.json；
/// 按可执行文件路径绑定（Chrome 等频繁重启、PID 变化的应用重启后仍能命中）。
/// 注意与手段B2 的注册表持久化区分：注册表是 Windows 自己的每程序端点存储（管未来），
/// 本表是产品层的路由意图（含模式/音量等元数据，UI 展示与 A 链路重挂的依据）。
/// </summary>
public sealed class StoredRoute
{
    /// <summary>目标进程可执行文件完整路径（主键；解析失败时退化为 "pid:N"）。</summary>
    public string ProcessPath { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public RoutingMode Mode { get; set; } = RoutingMode.Auto;

    public float Volume { get; set; } = 1f;

    public bool Mute { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>路由表的加载 / 保存 / 增删（单文件 JSON，写时原子替换）。</summary>
public sealed class RouteStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "routes.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<StoredRoute> _routes = new();

    public RouteStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    /// <summary>当前表快照。</summary>
    public IReadOnlyList<StoredRoute> Routes
    {
        get
        {
            lock (_gate)
            {
                return _routes.ToList();
            }
        }
    }

    /// <summary>从磁盘加载（文件不存在返回空表）。</summary>
    public RouteStore Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _routes = new List<StoredRoute>();
                return this;
            }

            var json = File.ReadAllText(_path);
            _routes = JsonSerializer.Deserialize<List<StoredRoute>>(json, JsonOptions) ?? new List<StoredRoute>();
            return this;
        }
    }

    /// <summary>新增或更新（按 ProcessPath 幂等）。</summary>
    public StoredRoute Upsert(StoredRoute route)
    {
        lock (_gate)
        {
            var existing = _routes.FirstOrDefault(r =>
                string.Equals(r.ProcessPath, route.ProcessPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                route.CreatedAt = DateTime.Now;
                _routes.Add(route);
            }
            else
            {
                route.CreatedAt = existing.CreatedAt;
                int index = _routes.IndexOf(existing);
                _routes[index] = route;
            }

            route.UpdatedAt = DateTime.Now;
            SaveLocked();
            return route;
        }
    }

    /// <summary>按进程路径移除；返回是否真的删了。</summary>
    public bool Remove(string processPath)
    {
        lock (_gate)
        {
            int removed = _routes.RemoveAll(r =>
                string.Equals(r.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveLocked();
            }

            return removed > 0;
        }
    }

    /// <summary>按 PID 查表（先解析进程路径，再路径匹配；解析失败退化为 "pid:N" 匹配）。</summary>
    public StoredRoute? FindByProcessId(int processId)
    {
        string path = ResolveProcessPath(processId);
        lock (_gate)
        {
            return _routes.FirstOrDefault(r =>
                string.Equals(r.ProcessPath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>写盘（临时文件 + 原子替换，避免半截 JSON）。</summary>
    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string json = JsonSerializer.Serialize(_routes, JsonOptions);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>进程路径解析；失败（权限/已退出）退化为 "pid:N"。</summary>
    public static string ResolveProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // 访问受限（系统/提权进程）时 MainModule 抛异常
            }

            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            // 兜底：用进程名做弱绑定（比 pid:N 稳，重启后仍可能命中同名进程）
            return process.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch
        {
            return $"pid:{processId}";
        }
    }
}
