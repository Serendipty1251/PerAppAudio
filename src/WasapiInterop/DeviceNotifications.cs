using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

/// <summary>
/// IMMNotificationClient 托管实现：把 COM 设备通知转成 .NET 事件。
/// 注意：COM 回调到达线程不确定（RPC 线程池），消费者必须自行做线程安全处理。
/// 实例必须被长期持有（GC 根），否则注册会失效。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceNotifier : IMMNotificationClient
{
    public event Action<string, DeviceState>? StateChanged;
    public event Action<string>? Added;
    public event Action<string>? Removed;
    public event Action<EDataFlow, ERole, string>? DefaultChanged;

    public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
        => StateChanged?.Invoke(pwstrDeviceId, dwNewState);

    public void OnDeviceAdded(string pwstrDeviceId) => Added?.Invoke(pwstrDeviceId);

    public void OnDeviceRemoved(string pwstrDeviceId) => Removed?.Invoke(pwstrDeviceId);

    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        => DefaultChanged?.Invoke(flow, role, pwstrDefaultDeviceId);

    public void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
    {
        // 属性变更（如音量图标）不关心
    }
}

/// <summary>设备通知订阅助手：注册后须同时持有返回的枚举器与客户端（否则被 GC/释放）。</summary>
public static class DeviceNotifications
{
    public static (IMMDeviceEnumerator Enumerator, DeviceNotifier Client) Subscribe(DeviceNotifier client)
    {
        var enumerator = DeviceManager.CreateEnumerator();
        enumerator.RegisterEndpointNotificationCallback(client);
        return (enumerator, client);
    }

    public static void Unsubscribe(IMMDeviceEnumerator? enumerator, DeviceNotifier? client)
    {
        if (enumerator is null || client is null)
        {
            return;
        }

        try
        {
            enumerator.UnregisterEndpointNotificationCallback(client);
        }
        catch
        {
            // 注销失败（枚举器已失效）不影响退出
        }
    }
}
