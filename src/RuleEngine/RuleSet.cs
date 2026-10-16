using System.Text;

namespace PerAppAudio.RuleEngine;

/// <summary>场景 Profile 中的一条路由规则（P2 精简 DSL：扁平字段）。</summary>
public sealed class ProfileRule
{
    /// <summary>匹配目标：进程名（chrome.exe）或可执行完整路径。</summary>
    public string Process { get; set; } = string.Empty;

    /// <summary>目标设备：友好名或设备 ID（应用时解析）。</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>手段：auto / a / b。</summary>
    public string Mode { get; set; } = "auto";

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
/// rules.yaml 加载器（P2）。
/// 文件位于 %APPDATA%\PerAppAudio\rules.yaml，schema：
/// <code>
/// profiles:
///   - name: game
///     description: 游戏场景
///     rules:
///       - process: chrome.exe
///         device: VoiceMeeter VAIO3
///         mode: auto
///         volume: 0.8
///         mute: false
/// </code>
/// 解析器为**最小 YAML 子集**自实现（两级列表嵌套 + 标量字段），零第三方依赖；
/// 不支持锚点 / 多行字符串 / 引号内冒号等完整 YAML 特性——超出子集的行会报错并带行号。
/// </summary>
public sealed class RuleSet
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerAppAudio", "rules.yaml");

    public List<Profile> Profiles { get; private set; } = new();

    public static RuleSet Load(string? path = null)
    {
        path ??= DefaultPath;
        var set = new RuleSet();
        if (!File.Exists(path))
        {
            return set;
        }

        set.Profiles = ParseProfiles(File.ReadAllLines(path, Encoding.UTF8), path);
        return set;
    }

    public Profile? Get(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------- 最小 YAML 子集解析 ----------

    private static List<Profile> ParseProfiles(string[] lines, string path)
    {
        var profiles = new List<Profile>();
        Profile? current = null;
        ProfileRule? currentRule = null;

        foreach (var rawLine in lines)
        {
            int lineNo = Array.IndexOf(lines, rawLine) + 1;
            string line = StripComment(rawLine).TrimEnd();
            if (line.Trim().Length == 0) continue;

            int indent = line.Length - line.TrimStart().Length;
            string text = line.Trim();

            if (indent == 0)
            {
                if (text == "profiles:")
                {
                    current = null;
                    currentRule = null;
                    continue;
                }
                throw new FormatException($"{path}:{lineNo} 顶层只支持 'profiles:'，实际是「{text}」");
            }

            if (text.StartsWith("- "))
            {
                string body = text[2..].Trim();
                int itemIndent = indent;
                if (itemIndent <= 2)
                {
                    // '- name: xxx' → 新 Profile
                    if (!body.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException($"{path}:{lineNo} 列表项第一个字段必须是 name:，实际是「{body}」");
                    }
                    current = new Profile { Name = Scalar(body["name:".Length..].Trim()) };
                    profiles.Add(current);
                    currentRule = null;
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

            if (currentRule is not null && indent > 4)
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
