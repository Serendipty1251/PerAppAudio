namespace PerAppAudio.WasapiInterop;

/// <summary>
/// 设备展示名去歧义：同名端点（如两个「扬声器 (Realtek)」）追加设备 ID 尾段，
/// 保证下拉框/列表里可区分。**只影响展示层**——存储（routes.json/rules.yaml）、
/// 解析（ResolveDevice）、路由命令一律使用裸 FriendlyName，零回归风险。
/// 无同名设备时展示名 = 裸名，行为与之前完全一致。
/// </summary>
public static class DeviceNaming
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _bareToDisplay = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _displayToBare = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用当前可见设备集合刷新映射（UI 每次同步设备时调用）。</summary>
    public static void Refresh(IReadOnlyList<WasapiDevice> visible)
    {
        var toDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toBare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in visible.GroupBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            bool duplicated = group.Count() > 1;
            foreach (var device in group)
            {
                string display = duplicated ? $"{device.FriendlyName}（{Tail(device.Id)}）" : device.FriendlyName;
                toDisplay[device.FriendlyName] = display;
                toBare[display] = device.FriendlyName;
            }
        }

        lock (Gate)
        {
            _bareToDisplay = toDisplay;
            _displayToBare = toBare;
        }
    }

    /// <summary>裸名 → 展示名（无同名设备时原样返回）。</summary>
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

    private static string Tail(string id) => id.Length <= 6 ? id : id[^6..];
}
