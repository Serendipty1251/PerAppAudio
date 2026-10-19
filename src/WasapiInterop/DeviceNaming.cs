using System.Collections.Generic;
using System.Linq;

namespace PerAppAudio.WasapiInterop;

/// <summary>
/// 设备展示名去歧义：同名端点（如两个「扬声器 (Realtek)」）追加设备 ID 尾段，
/// 保证下拉框里每个设备一条且互不相同。**只影响展示层**——存储（routes.json/rules.yaml）、
/// 解析（ResolveDevice）、路由命令一律使用裸 FriendlyName。
/// 无同名设备时展示名 = 裸名，行为与之前完全一致。
/// </summary>
public static class DeviceNaming
{
    private static readonly object Gate = new();

    /// <summary>设备 ID → 展示名（每个设备独立一条，互不覆盖）。</summary>
    private static Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>裸名 → 展示名（同名设备取第一个，供选中值/列展示得到可匹配的条目）。</summary>
    private static Dictionary<string, string> _bareToDisplay = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>展示名 → 裸名（ComboBox 选中值回写时用）。</summary>
    private static Dictionary<string, string> _displayToBare = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用当前可见设备集合刷新映射（UI 每次同步设备时调用）。</summary>
    public static void Refresh(IReadOnlyList<WasapiDevice> visible)
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bareToDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displayToBare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in visible.GroupBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            bool duplicated = group.Count() > 1;
            foreach (var device in group)
            {
                // 同名设备追加 ID 尾段，保证每个设备的展示名唯一。
                // 旧实现用「裸名 → 展示名」建表，同名设备会互相覆盖成同一条，进而导致下拉框出现重复条目。
                string display = duplicated
                    ? $"{device.FriendlyName} #{Tail(device.Id)}"
                    : device.FriendlyName;

                byId[device.Id] = display;
                if (!displayToBare.ContainsKey(display))
                {
                    displayToBare[display] = device.FriendlyName;
                }
                if (!bareToDisplay.ContainsKey(device.FriendlyName))
                {
                    bareToDisplay[device.FriendlyName] = display;
                }
            }
        }

        lock (Gate)
        {
            _byId = byId;
            _bareToDisplay = bareToDisplay;
            _displayToBare = displayToBare;
        }
    }

    /// <summary>设备 → 展示名（下拉框按设备枚举时用，每个设备一条且唯一）。</summary>
    public static string Display(WasapiDevice device)
    {
        lock (Gate)
        {
            return _byId.TryGetValue(device.Id, out var display) ? display : device.FriendlyName;
        }
    }

    /// <summary>裸名 → 展示名（无同名设备时原样返回；同名时返回第一个设备的展示名，保证可被选中匹配）。</summary>
    public static string Display(string bareName)
    {
        if (string.IsNullOrEmpty(bareName))
        {
            return bareName;
        }

        lock (Gate)
        {
            return _bareToDisplay.TryGetValue(bareName, out var display) ? display : bareName;
        }
    }

    /// <summary>展示名 → 裸名（ComboBox 选中值回写时用）。</summary>
    public static string Bare(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return displayName;
        }

        lock (Gate)
        {
            return _displayToBare.TryGetValue(displayName, out var bare) ? bare : displayName;
        }
    }

    /// <summary>取端点 ID 中最具区分度的尾段：GUID 后 8 位十六进制（大写，不含右花括号）。</summary>
    private static string Tail(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        int close = id.LastIndexOf('}');
        string core = close > 0 ? id[..close] : id;
        return core.Length <= 8 ? core.ToUpperInvariant() : core[^8..].ToUpperInvariant();
    }
}
