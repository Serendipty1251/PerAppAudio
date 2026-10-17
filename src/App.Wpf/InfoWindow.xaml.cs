using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 通用信息对话框：关于 / 使用说明共用骨架，内容由工厂方法构造。
/// </summary>
public partial class InfoWindow : Window
{
    private InfoWindow(string title, string subtitle)
    {
        InitializeComponent();
        this.Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }

    /// <summary>关于对话框：版本 / 机制 / 链接 / 许可。</summary>
    public static InfoWindow About()
    {
        var w = new InfoWindow("关于 PerAppAudio", "v0.4.0");

        w.BodyPanel.Children.Add(Paragraph("每应用音频输出路由——把不同应用的音频分别送到不同输出设备。", muted: true));

        w.BodyPanel.Children.Add(Header("两种控制手段"));
        w.BodyPanel.Children.Add(Bullet("手段 A · 进程 Loopback 实时改道", "30–60ms，免驱动，改当前正在播放的流"));
        w.BodyPanel.Children.Add(Bullet("手段 B · 端点引导", "免驱动免注入，持久化每程序默认输出设备，应用启动即生效"));
        w.BodyPanel.Children.Add(Paragraph("默认自动策略：优先 B，失败 2 秒内回退 A。", muted: true));

        w.BodyPanel.Children.Add(Header("项目"));
        var link = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        link.Inlines.Add(new Run("GitHub：") { Foreground = (System.Windows.Media.Brush)w.FindResource("MutedBrush") });
        var hyper = new Hyperlink(new Run("github.com/Serendipty1251/PerAppAudio"))
        {
            Focusable = false,
        };
        hyper.Click += (_, _) => OpenUrl("https://github.com/Serendipty1251/PerAppAudio");
        link.Inlines.Add(hyper);
        w.BodyPanel.Children.Add(link);

        w.BodyPanel.Children.Add(Paragraph("MIT License · 纯本地运行，零网络请求，无遥测", muted: true));
        return w;
    }

    /// <summary>使用说明对话框：核心用法。</summary>
    public static InfoWindow Usage()
    {
        var w = new InfoWindow("使用说明", "PerAppAudio v0.4.0");

        w.BodyPanel.Children.Add(Header("正在播放"));
        w.BodyPanel.Children.Add(Bullet("自动检测", "列出当前有音频会话的应用（让应用播放一次声音即出现）"));
        w.BodyPanel.Children.Add(Bullet("一键改道", "选输出设备 →「应用」：立即改道并写入持久路由表"));
        w.BodyPanel.Children.Add(Bullet("自动刷新", "默认关闭，需要时勾选（10 秒间隔）；也可随时点「刷新」"));

        w.BodyPanel.Children.Add(Header("路由表"));
        w.BodyPanel.Children.Add(Bullet("持久路由", "应用启动即按此表自动路由（2–4 秒内生效），重启后保持"));
        w.BodyPanel.Children.Add(Bullet("手段可选", "自动 / A 实时 / B 持久；改动即时对运行中的进程生效"));

        w.BodyPanel.Children.Add(Header("场景与日志"));
        w.BodyPanel.Children.Add(Bullet("场景切换", "在 rules.yaml 定义场景，用顶部「场景」或托盘一键切换"));
        w.BodyPanel.Children.Add(Bullet("日志", "每次操作与失败原因均记录在日志页，可经工具菜单打开日志文件"));

        return w;
    }

    private static TextBlock Paragraph(string text, bool muted = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
        };
        return tb;
    }

    private static TextBlock Header(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 2),
        };
        return tb;
    }

    private static TextBlock Bullet(string title, string desc)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12,
        };
        tb.Inlines.Add(new Run("• " + title + "："));
        tb.Inlines.Add(new Run(desc));
        return tb;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开失败不阻塞对话框
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
