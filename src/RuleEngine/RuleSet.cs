using System.Text;

namespace PerAppAudio.RuleEngine;

/// <summary>场景 Profile 中的一条路由规则（P2 精简 DSL：扁平字段）。</summary>
public sealed class ProfileRule
{
    /// <summary>匹配目标：进程名（chrome.exe）、可执行完整路径，或通配符（*Valorant*）。</summary>
    public string Process { get; set; } = string.Empty;

    /// <summary>目标设备：别名（devices: 段）/ 设备 ID / 友好名。</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>手段：auto / a / b。留空 = 用全局 default_mode。</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>目标会话音量 0.0–1.0（默认 1.0）。</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>静音（默认 false）。</summary>
    public bool Mute { get; set; }
}

/// <summary>一个场景 Profile：一组规则的总和，一键切换。</summary>
public sealed class Profile
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<ProfileRule> Rules { get; set; } = new();
}

/// <summary>
/// 设备事件触发规则（rules.yaml 的 device_events: 段）：
/// 端点插拔 / 激活状态变化 / 默认设备切换时，命中即应用指定场景 Profile。
/// </summary>
public sealed class DeviceEventRule
{
    /// <summary>触发事件：added / removed / activated / deactivated / default_changed / changed（任意）。</summary>
    public string On { get; set; } = string.Empty;

    /// <summary>设备名匹配片段（友好名子串，不区分大小写）；空 = 任意设备。</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>命中后应用的场景 Profile 名（对应 profiles: 段）。</summary>
    public string Apply { get; set; } = string.Empty;
}

/// <summary>
/// rules.yaml 加载器（P2）。
/// 文件位于 %APPDATA%\PerAppAudio\rules.yaml，schema：
/// <code>
/// default_mode: auto        # 可选：全局默认手段 auto / loopback(a) / endpoint(b)
///                           # 规则未显式写 mode 时用它
///
/// devices:                  # 可选：设备别名，rules 的 device: 可写别名而非完整 ID
///   speaker: "{0.0.0.00000000}.{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}"
///   headset: "Realtek HD Audio 2nd output"
///
/// profiles:
///   - name: game
///     description: 游戏场景
///     rules:
///       - process: chrome.exe                          # 进程名
///         device: speaker                              # 别名 / ID / 友好名
///         mode: auto                                   # 省略则用 default_mode
///         volume: 0.8
///         mute: false
///       - process: "C:/Riot Games/VALORANT/...exe"     # 精确路径
///       - process: "*Valorant*"                        # 通配符（* 多字符，? 单字符）
///
/// device_events:            # 可选：设备事件触发（插耳机自动切场景）
///   - on: activated         # added/removed/activated/deactivated/default_changed/changed
///     device: 耳机           # 名称片段，留空 = 任意设备
///     apply: game            # 命中后应用的 Profile 名
/// </code>
/// 匹配优先级（同一进程命中多条规则时）：**精确路径 &gt; 进程名 &gt; 通配符**。
/// 解析器为**最小 YAML 子集**自实现（两级列表嵌套 + 标量字段），零第三方依赖；
/// 不支持锚点 / 多行字符串 / 引号内冒号等完整 YAML 特性——超出子集的行会报错并带行号。
/// </summary>
public sealed class RuleSet
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio", "rules.yaml");

    public List<Profile> Profiles { get; private set; } = new();

    /// <summary>设备事件触发规则（device_events: 段；无则为空）。</summary>
    public List<DeviceEventRule> DeviceEvents { get; private set; } = new();

    /// <summary>全局默认手段（default_mode:）；规则未显式指定 mode 时使用。默认 auto。</summary>
    public string DefaultMode { get; private set; } = "auto";

    /// <summary>设备别名表（devices: 段）：别名 → 设备 ID 或友好名。</summary>
    public Dictionary<string, string> Devices { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把规则里的 device 值经别名表解析为真实设备引用；非别名则原样返回。</summary>
    public string ResolveDeviceName(string deviceOrAlias)
        => !string.IsNullOrWhiteSpace(deviceOrAlias)
           && Devices.TryGetValue(deviceOrAlias, out var target)
           && !string.IsNullOrWhiteSpace(target)
            ? target
            : deviceOrAlias;

    public static RuleSet Load(string? path = null)
    {
        path ??= DefaultPath;
        var set = new RuleSet();
        if (!File.Exists(path))
        {
            return set;
        }

        set.Profiles = ParseProfiles(File.ReadAllLines(path, Encoding.UTF8), path,
            out var events, out var defaultMode, out var devices);
        set.DeviceEvents = events;
        set.DefaultMode = defaultMode;
        set.Devices = devices;
        return set;
    }

    public Profile? Get(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------- 最小 YAML 子集解析 ----------

    private static List<Profile> ParseProfiles(
        string[] lines, string path,
        out List<DeviceEventRule> deviceEvents,
        out string defaultMode,
        out Dictionary<string, string> devices)
    {
        var profiles = new List<Profile>();
        deviceEvents = new List<DeviceEventRule>();
        defaultMode = "auto";
        devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Profile? current = null;
        ProfileRule? currentRule = null;
        DeviceEventRule? currentEvent = null;
        string section = string.Empty; // "" | "profiles" | "device_events" | "devices"

        // 索引循环而非 foreach + Array.IndexOf：对重复行（两个 mute: false 等 YAML 常见形态）
        // IndexOf 恒返回首个匹配下标，格式报错会把用户指向错误行（M6）。
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var rawLine = lines[lineIdx];
            int lineNo = lineIdx + 1;
            string line = StripComment(rawLine).TrimEnd();
            if (line.Trim().Length == 0) continue;

            int indent = line.Length - line.TrimStart().Length;
            string text = line.Trim();

            if (indent == 0)
            {
                if (text == "profiles:")
                {
                    section = "profiles";
                    current = null;
                    currentRule = null;
                    currentEvent = null;
                    continue;
                }
                if (text == "device_events:")
                {
                    section = "device_events";
                    current = null;
                    currentRule = null;
                    currentEvent = null;
                    continue;
                }
                if (text == "devices:")
                {
                    section = "devices";
                    current = null;
                    currentRule = null;
                    currentEvent = null;
                    continue;
                }
                if (text.StartsWith("default_mode:", StringComparison.OrdinalIgnoreCase))
                {
                    defaultMode = Scalar(text["default_mode:".Length..].Trim());
                    continue;
                }
                throw new FormatException($"{path}:{lineNo} 顶层只支持 'profiles:' / 'device_events:' / 'devices:' / 'default_mode:'，实际是「{text}」");
            }

            if (text.StartsWith("- "))
            {
                string body = text[2..].Trim();
                int itemIndent = indent;
                if (itemIndent <= 2)
                {
                    if (section == "device_events")
                    {
                        // '- on: activated' → 新事件规则
                        if (!body.StartsWith("on:", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FormatException($"{path}:{lineNo} 事件规则第一个字段必须是 on:，实际是「{body}」");
                        }
                        currentEvent = new DeviceEventRule { On = Scalar(body["on:".Length..].Trim()) };
                        deviceEvents.Add(currentEvent);
                        current = null;
                        currentRule = null;
                        continue;
                    }

                    // '- name: xxx' → 新 Profile
                    if (!body.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException($"{path}:{lineNo} 列表项第一个字段必须是 name:，实际是「{body}」");
                    }
                    current = new Profile { Name = Scalar(body["name:".Length..].Trim()) };
                    profiles.Add(current);
                    currentRule = null;
                    currentEvent = null;
                }
                else
                {
                    // 更深缩进的 '- process: xxx' → 新规则
                    if (current is null)
                    {
                        throw new FormatException($"{path}:{lineNo} 规则出现在 Profile 之外");
                    }
                    if (!body.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException($"{path}:{lineNo} 规则第一个字段必须是 process:，实际是「{body}」");
                    }
                    currentRule = new ProfileRule { Process = Scalar(body["process:".Length..].Trim()) };
                    current.Rules.Add(currentRule);
                }
                continue;
            }

            // 普通字段行
            int colon = text.IndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException($"{path}:{lineNo} 无法解析的行「{text}」（期望 key: value）");
            }

            string key = text[..colon].Trim();
            string value = Scalar(text[(colon + 1)..].Trim());

            // devices: 段（别名: 目标）
            if (section == "devices" && current is null && currentRule is null && currentEvent is null)
            {
                if (indent < 2)
                {
                    throw new FormatException($"{path}:{lineNo} devices: 条目需要缩进（别名: 设备ID或名称）");
                }
                devices[key] = value;
                continue;
            }

            if (currentEvent is not null && indent > 2)
            {
                ApplyEventField(currentEvent, key, value, path, lineNo);
            }
            else if (currentRule is not null && indent > 4)
            {
                ApplyRuleField(currentRule, key, value, path, lineNo);
            }
            else if (current is not null)
            {
                switch (key.ToLowerInvariant())
                {
                    case "name": current.Name = value; break;
                    case "description": current.Description = value; break;
                    case "rules": break; // 'rules:' 声明行，规则在后续 '- ' 列表项
                    default:
                        throw new FormatException($"{path}:{lineNo} Profile 不支持字段「{key}」（name/description/rules）");
                }
            }
        }

        return profiles;
    }

    private static void ApplyEventField(DeviceEventRule rule, string key, string value, string path, int lineNo)
    {
        switch (key.ToLowerInvariant())
        {
            case "on": rule.On = value; break;
            case "device": rule.Device = value; break;
            case "apply": rule.Apply = value; break;
            default:
                throw new FormatException($"{path}:{lineNo} 事件规则不支持字段「{key}」（on/device/apply）");
        }
    }

    private static void ApplyRuleField(ProfileRule rule, string key, string value, string path, int lineNo)
    {
        switch (key.ToLowerInvariant())
        {
            case "process": rule.Process = value; break;
            case "device": rule.Device = value; break;
            case "mode": rule.Mode = value; break;
            case "volume":
                if (!float.TryParse(value, out float v))
                {
                    throw new FormatException($"{path}:{lineNo} volume 不是数字「{value}」");
                }
                rule.Volume = Math.Clamp(v, 0f, 1f);
                break;
            case "mute":
                rule.Mute = value is "true" or "yes" or "1";
                break;
            default:
                throw new FormatException($"{path}:{lineNo} 规则不支持字段「{key}」（process/device/mode/volume/mute）");
        }
    }

    private static string StripComment(string line)
    {
        int idx = line.IndexOf('#');
        return idx >= 0 ? line[..idx] : line;
    }

    /// <summary>标量清洗：去引号。</summary>
    private static string Scalar(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value.StartsWith('"') && value.EndsWith('"') || value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }
        return value;
    }
}
