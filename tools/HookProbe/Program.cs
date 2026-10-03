using System.Runtime.InteropServices;

internal static class Program
{
    private static HookProc? callback;

    public static void Main()
    {
        callback = (code, message, data) =>
        {
            int key = code >= 0 ? Marshal.ReadInt32(data) : 0;
            if (code >= 0) Console.WriteLine($"HOOK message=0x{message:X} vk=0x{key:X}");
            return code >= 0 && key == 0xAE ? 1 : CallNextHookEx(nint.Zero, code, message, data);
        };
        nint hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        Console.WriteLine($"HOOK_HANDLE 0x{hook:X} error={Marshal.GetLastWin32Error()}");
        uint mainThread = GetCurrentThreadId();
        var sender = new Thread(() =>
        {
            Thread.Sleep(500);
            keybd_event(0xAE, 0, 0, UIntPtr.Zero);
            keybd_event(0xAE, 0, 2, UIntPtr.Zero);
            Thread.Sleep(500);
            PostThreadMessage(mainThread, 0x0012, nint.Zero, nint.Zero);
        });
        sender.Start();
        while (GetMessage(out Message _, nint.Zero, 0, 0) > 0) { }
        sender.Join();
        _ = UnhookWindowsHookEx(hook);
        GC.KeepAlive(callback);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll", SetLastError=true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
