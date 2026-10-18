using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 界面本地化（P2 #25）：嵌入 JSON 资源（zh-CN 默认 / en），
/// XAML 用 <c>{loc:Loc Key}</c> 标记扩展绑定，代码用 <c>L10n.Get("Key")</c>。
/// 切换语言时 Load(culture) 一次性重建字典并广播 Indexer 变更——
/// 所有 {loc:Loc} 绑定即时刷新，无需重启窗口。
/// 缺 key 时返回 key 本身（便于发现漏翻项）。
/// </summary>
public sealed class L10n : INotifyPropertyChanged
{
    private static readonly Lazy<L10n> InstanceLazy = new(() => new L10n());
    public static L10n Instance => InstanceLazy.Value;

    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _culture = "zh-CN";

    /// <summary>当前语言码（zh-CN / en）。</summary>
    public string Culture => _culture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private L10n()
    {
    }

    /// <summary>索引器（供 {loc:Loc} 绑定 Source=L10n.Instance）。</summary>
    public string this[string key] => Get(key);

    public static string Get(string key)
    {
        var dict = Instance._strings;
        return dict.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : key;
    }

    /// <summary>
    /// 载入指定语言。culture 为空/unknown 回落 zh-CN。
    /// 返回是否成功载入（失败时保留旧字典，不破坏界面）。
    /// </summary>
    public static bool Load(string? culture)
    {
        culture = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture.Trim();
        if (!culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            culture = "zh-CN";
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "PerAppAudio.App.Wpf.Strings.zh-CN.json"
                : "PerAppAudio.App.Wpf.Strings.en.json";

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"嵌入资源不存在: {resourceName}");
            using var reader = new StreamReader(stream);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
                ?? throw new InvalidOperationException("语言文件解析为空");

            Instance._strings = dict;
            Instance._culture = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
            // Indexer 变更广播：所有 {loc:Loc} 绑定刷新
            Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(null));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>可用语言列表（设置页下拉用）。</summary>
    public static IReadOnlyList<(string Code, string Display)> Languages { get; } = new[]
    {
        ("zh-CN", "中文"),
        ("en", "English"),
    };
}

/// <summary>
/// XAML 本地化标记扩展：{loc:Loc SomeKey} → 绑定 L10n.Instance[key]（OneWay，随语言切换刷新）。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = L10n.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>本地化静态助手（代码里短写法）。</summary>
public static class L
{
    public static string T(string key) => L10n.Get(key);
}
