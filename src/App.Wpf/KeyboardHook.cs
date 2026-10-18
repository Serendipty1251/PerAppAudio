using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 低层键盘钩子（WH_KEYBOARD_LL）实现「透传热键」：监测到已登记的组合键时触发回调，
/// 但始终调用 CallNextHookEx 放行（返回 0），因此前台游戏/应用也能收到同一按键——
/// 与 RegisterHotKey（会吞掉按键）相反。
/// 仅用于音板热键 / 特殊热键在「透传热键」开启时的场景。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;

    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkShift = 0x10;
    private const uint VkLwin = 0x5B;
    private const uint VkRwin = 0x5C;

    private readonly Dictionary<ComboKey, int> _combos = new();
    private readonly Action<int> _onCombo;
    private readonly HookProc _proc;
    private readonly GCHandle _procHandle;
    private IntPtr _hook;
    private readonly HashSet<uint> _down = new();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Kbdllhookstruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly struct ComboKey : IEquatable<ComboKey>
    {
        public uint Mods { get; }
        public uint Vk { get; }
        public ComboKey(uint mods, uint vk) { Mods = mods; Vk = vk; }
        public bool Equals(ComboKey other) => Mods == other.Mods && Vk == other.Vk;
        public override bool Equals(object? obj) => obj is ComboKey k && Equals(k);
        public override int GetHashCode() => (int)(Mods << 16 | Vk);
    }

    public KeyboardHook(Action<int> onCombo)
    {
        _onCombo = onCombo;
        _proc = HookCallback;
        _procHandle = GCHandle.Alloc(_proc);
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(mod.ModuleName), 0);
    }

    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>设置需透传的组合键（mods 见 HotkeyManager 的 Mod* 常量；vk 见 KeyToVk）。</summary>
    public void SetCombos(IEnumerable<(uint Mods, uint Vk, int Id)> combos)
    {
        _combos.Clear();
        foreach (var (mods, vk, id) in combos)
        {
            _combos[new ComboKey(mods, vk)] = id;
        }
    }

    private static uint CurrentMods(HashSet<uint> down)
    {
        uint m = 0;
        if (down.Contains(VkControl)) m |= 0x0002;
        if (down.Contains(VkMenu)) m |= 0x0001;
        if (down.Contains(VkShift)) m |= 0x0004;
        if (down.Contains(VkLwin) || down.Contains(VkRwin)) m |= 0x0008;
        return m;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WmKeydown or WmSyskeydown)
            {
                var k = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
                uint vk = k.VkCode;
                bool repeat = (k.Flags & 0x30) != 0; // bit 30 = previous key state (repeat)
                if (vk is VkControl or VkMenu or VkShift or VkLwin or VkRwin)
                {
                    _down.Add(vk);
                }
                else
                {
                    if (!repeat)
                    {
                        uint mods = CurrentMods(_down);
                        var combo = new ComboKey(mods, vk);
                        if (_combos.TryGetValue(combo, out int id))
                        {
                            _onCombo(id);
                        }
                    }
                    _down.Remove(vk);
                }
            }
            else if (msg is 0x0101 or 0x0105) // WM_KEYUP / WM_SYSKEYUP
            {
                var k = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
                _down.Remove(k.VkCode);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (_procHandle.IsAllocated)
        {
            _procHandle.Free();
        }
    }
}
