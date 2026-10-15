namespace PerAppAudio.SessionManager;

/// <summary>一个音频渲染会话的快照。</summary>
/// <remarks>
/// P0-1 起由 WASAPI <c>IAudioSessionEnumerator</c> 填充；
/// 规则绑定进程路径 + 签名而非 PID（抗进程重启）。
/// </remarks>
public sealed record AudioSessionInfo(
    int ProcessId,
    string ProcessPath,
    string SessionId,
    string DisplayName,
    SessionState State);

public enum SessionState
{
    Inactive,
    Active,
    Expired,
}

/// <summary>会话感知源：枚举当前音频渲染会话，监听生命周期。</summary>
public interface IAudioSessionSource
{
    IReadOnlyList<AudioSessionInfo> EnumerateSessions();
}
