using System.Runtime.InteropServices;
using System.Text;

namespace NetDoctor.Core;

/// <summary>
/// 集中存放 P/Invoke 声明。原生 DLL 与主程序共用这一份。
/// 注意：这里的声明必须能在 NativeAOT 下正常编组，不要放任何界面相关的东西。
/// </summary>
internal static class NativeMethods
{
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

    // ---------------- kernel32：定位宿主 exe ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleFileNameW(IntPtr hModule, StringBuilder lpFilename, int nSize);

    // ---------------- wininet：代理设置刷新 ----------------

    [DllImport("wininet.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr InternetOpenA(string agent, int accessType, string proxy, string proxyBypass, int flags);

    [DllImport("wininet.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern bool InternetSetOptionA(IntPtr hInternet, int option, IntPtr buffer, int length);

    [DllImport("wininet.dll", SetLastError = true)]
    public static extern bool InternetCloseHandle(IntPtr h);

    // ---------------- shell32 / user32：设置变更通知 ----------------

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);
}
