using System;
using System.Globalization;
using System.Windows.Data;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 设备名双向转换：绑定值是裸 FriendlyName（存储/解析用），界面显示去歧义展示名
/// （同名设备追加 ID 尾段）。用于两个设备下拉框与「当前设备」列。
/// </summary>
public sealed class DeviceNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? DeviceNaming.Display(s) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? DeviceNaming.Bare(s) : value;
}
