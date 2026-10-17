using System;
using System.Windows;
using System.Windows.Threading;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// OSD 操作提示：右下角半透明浮层，1.6 秒自动消失。
/// 静态 Show(text, icon)——每次调用复用/重建窗口，不累积。
/// </summary>
public partial class OsdWindow : Window
{
    private static OsdWindow? _current;

    public OsdWindow()
    {
        InitializeComponent();
    }

    /// <summary>弹出提示（OSD 关闭时静默失败，不影响主流程）。</summary>
    public static void Show(string message, string icon = "🔊")
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher is not { } dispatcher)
            {
                return;
            }

            dispatcher.Invoke(() =>
            {
                _current?.Close();

                var w = new OsdWindow
                {
                    IconText = { Text = icon },
                    MessageText = { Text = message },
                };

                // 主显示器工作区右下角（避开任务栏）
                var work = SystemParameters.WorkArea;
                w.Left = work.Right - w.Width - 24;
                w.Top = work.Bottom - w.Height - 24;

                _current = w;
                w.Show();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    w.Close();
                    if (ReferenceEquals(_current, w))
                    {
                        _current = null;
                    }
                };
                timer.Start();
            }, DispatcherPriority.Background);
        }
        catch
        {
            // OSD 是纯提示，失败忽略
        }
    }
}
