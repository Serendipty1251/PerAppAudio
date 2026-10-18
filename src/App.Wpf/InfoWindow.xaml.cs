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
        var w = new InfoWindow(L.T("AboutTitle"), L.T("AboutSubtitle"));

        w.BodyPanel.Children.Add(Paragraph(L.T("AboutIntro"), muted: true));

        w.BodyPanel.Children.Add(Header(L.T("AboutMethodsHeader")));
        w.BodyPanel.Children.Add(Bullet(L.T("AboutMethodA"), L.T("AboutMethodADesc")));
        w.BodyPanel.Children.Add(Bullet(L.T("AboutMethodB"), L.T("AboutMethodBDesc")));
        w.BodyPanel.Children.Add(Paragraph(L.T("AboutStrategy"), muted: true));

        w.BodyPanel.Children.Add(Header(L.T("AboutProjectHeader")));
        var link = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        link.Inlines.Add(new Run(L.T("AboutGithubLabel")) { Foreground = (System.Windows.Media.Brush)w.FindResource("MutedBrush") });
        var hyper = new Hyperlink(new Run("github.com/Serendipty1251/PerAppAudio"))
        {
            Focusable = false,
        };
        hyper.Click += (_, _) => OpenUrl("https://github.com/Serendipty1251/PerAppAudio");
        link.Inlines.Add(hyper);
        w.BodyPanel.Children.Add(link);

        w.BodyPanel.Children.Add(Paragraph(L.T("AboutLicense"), muted: true));
        return w;
    }

    /// <summary>使用说明对话框：核心用法。</summary>
    public static InfoWindow Usage()
    {
        var w = new InfoWindow(L.T("UsageTitle"), L.T("UsageSubtitle"));

        w.BodyPanel.Children.Add(Header(L.T("UsagePlayingHeader")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageDetect"), L.T("UsageDetectDesc")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageOneClick"), L.T("UsageOneClickDesc")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageAutoRefresh"), L.T("UsageAutoRefreshDesc")));

        w.BodyPanel.Children.Add(Header(L.T("UsageRoutesHeader")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsagePersist"), L.T("UsagePersistDesc")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageMode"), L.T("UsageModeDesc")));

        w.BodyPanel.Children.Add(Header(L.T("UsageSceneHeader")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageProfile"), L.T("UsageProfileDesc")));
        w.BodyPanel.Children.Add(Bullet(L.T("UsageLog"), L.T("UsageLogDesc")));

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
