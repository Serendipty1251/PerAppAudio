using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 热键捕获对话框：用户按下组合键即填入（与 HotkeyManager 的手势字符串格式一致：
/// "Ctrl+Alt+1" / "F5" / "Ctrl+Shift+A"）。「清除」= 解绑。
/// </summary>
public partial class HotkeyCaptureWindow : Window
{
    /// <summary>捕获到的手势；空串 = 用户选择清除。</summary>
    public string Gesture { get; private set; } = string.Empty;

    public HotkeyCaptureWindow(string soundName, string currentGesture)
    {
        InitializeComponent();
        PromptText.Text = string.Format(L10n.Get("SpHotkeyPrompt"), soundName);
        GestureBox.Text = currentGesture;
        Gesture = currentGesture;
        Loaded += (_, _) => GestureBox.Focus();
    }

    private void GestureBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 纯修饰键不捕获
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        string gesture = KeyToGesture(key);
        if (gesture.Length == 0)
        {
            return;
        }

        GestureBox.Text = gesture;
        Gesture = gesture;
        e.Handled = true;
    }

    /// <summary>Key → HotkeyManager 可解析的手势片段（与 KeyToVk 的命名对齐）。</summary>
    private static string KeyToGesture(Key key)
    {
        string mods = string.Empty;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            mods += "Ctrl+";
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            mods += "Alt+";
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            mods += "Shift+";
        }

        string k = key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            // 小键盘保留独立标识（VK_NUMPAD0..9），不再折叠成主键盘数字
            Key.NumPad0 => "NumPad0",
            Key.NumPad1 => "NumPad1",
            Key.NumPad2 => "NumPad2",
            Key.NumPad3 => "NumPad3",
            Key.NumPad4 => "NumPad4",
            Key.NumPad5 => "NumPad5",
            Key.NumPad6 => "NumPad6",
            Key.NumPad7 => "NumPad7",
            Key.NumPad8 => "NumPad8",
            Key.NumPad9 => "NumPad9",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            _ => key.ToString(),
        };

        return mods + k;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Gesture = string.Empty;
        GestureBox.Text = string.Empty;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
