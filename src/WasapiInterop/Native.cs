using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

/// <summary>
/// 进程 Loopback 异步激活基础设施。
/// 关键坑（已实测文献证实）：IActivateAudioInterfaceCompletionHandler 实现
/// 必须同时实现 IAgileObject（空标记接口），否则 ActivateAudioInterfaceAsync
/// 直接返回 E_ILLEGAL_METHOD_CALL。
/// 另一坑：LibraryImport 不支持 COM 接口参数，必须用经典 DllImport。
/// </summary>
internal static class NativeMethods
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] ref PROPVARIANT activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);
}

/// <summary>IAgileObject 标记接口（无方法，声明 COM 对象可跨套件调用）。</summary>
[ComImport]
[Guid(Guids.IID_IAgileObject)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject
{
}

[ComImport]
[Guid(Guids.IID_IActivateAudioInterfaceCompletionHandler)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid(Guids.IID_IActivateAudioInterfaceAsyncOperation)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

/// <summary>
/// 进程 Loopback 激活的完成处理器：等待 ActivateCompleted，取出 IAudioClient。
/// 同时实现 IAgileObject 以满足 COM 敏捷性要求。
/// </summary>
internal sealed class ProcessLoopbackActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out int hr, out object activated);
            if (hr != 0)
            {
                _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("ActivateAudioInterfaceAsync failed", hr));
                return;
            }

            _tcs.TrySetResult((IAudioClient)activated);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    public Task<IAudioClient> WaitAsync(CancellationToken ct)
    {
        return ct.CanBeCanceled ? _tcs.Task.WaitAsync(ct) : _tcs.Task;
    }
}

/// <summary>构造进程 Loopback 激活参数（VT_BLOB 包裹 AUDIOCLIENT_ACTIVATION_PARAMS）。</summary>
public static class AudioClientActivation
{
    /// <summary>
    /// 激活指定进程（含子进程树）的 Loopback IAudioClient。
    /// </summary>
    public static async Task<IAudioClient> ActivateProcessLoopbackAsync(uint targetProcessId, CancellationToken ct)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = targetProcessId,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
            },
        };

        var blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf(activationParams));
        var propVariant = new PROPVARIANT();
        var handler = new ProcessLoopbackActivationHandler();
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(activationParams, blobPtr, false);
            propVariant.vt = PROPVARIANT.VT_BLOB;
            propVariant.cbSize = (uint)Marshal.SizeOf(activationParams);
            propVariant.pBlobData = blobPtr;

            NativeMethods.ActivateAudioInterfaceAsync(
                Guids.VirtualAudioDeviceProcessLoopback,
                Guid.Parse(Guids.IID_IAudioClient),
                ref propVariant,
                handler,
                out operation);

            return await handler.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (operation is not null)
            {
                // 必须释放 activationOperation，否则同会话内无法再次激活（MS 示例的已知泄漏点）
                _ = Marshal.ReleaseComObject(operation);
            }
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>读取设备友好名（PKEY_Device_FriendlyName = a45c254e-df1c-4efd-8020-67d146a850e0, pid 14）。</summary>
    public static string? ReadDeviceFriendlyName(IMMDevice device)
    {
        var key = new PROPERTYKEY
        {
            fmtid = Guid.Parse("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14,
        };
        var pv = new PROPVARIANT();
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store);
            store.GetValue(ref key, out pv);
            if (pv.vt == PROPVARIANT.VT_LPWSTR && pv.pwszVal != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(pv.pwszVal);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pv.pwszVal != IntPtr.Zero)
            {
                _ = NativeMethods.PropVariantClear(ref pv);
            }
        }
    }
}
