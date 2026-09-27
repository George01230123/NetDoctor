using System.Runtime.InteropServices;
using System.Text;

namespace NetDoctor.CrashProbe;

/// <summary>
/// 最小复现：逐个测试非法指针，找出到底哪一个让宿主进程崩溃。
/// 每次崩溃进程就没了，所以用命令行参数一次只测一个。
/// </summary>
internal static class Probe
{
    private const string Dll = "NetDoctorNative.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnStrInt(IntPtr s, int a);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string p);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr h, string n);

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        char which = args.Length > 0 ? args[0][0] : 'a';

        IntPtr h = LoadLibraryW(Path.Combine(AppContext.BaseDirectory, Dll));
        if (h == IntPtr.Zero) { Console.WriteLine("LOAD_FAIL"); return; }

        IntPtr p = GetProcAddress(h, "ND_OptApply");
        if (p == IntPtr.Zero) { Console.WriteLine("EXPORT_FAIL"); return; }
        var fn = Marshal.GetDelegateForFunctionPointer<FnStrInt>(p);

        IntPtr arg = which switch
        {
            'z' => IntPtr.Zero,               // NULL
            '1' => new IntPtr(1),             // 0x1
            'm' => new IntPtr(-1),            // 0xFFFFFFFF
            's' => new IntPtr(0x1000),        // 低地址未映射
            'h' => new IntPtr(unchecked((int)0xDEADBEEF)),  // 野地址
            't' => new IntPtr(1L << 30),      // 中间地址，多半未映射
            _   => IntPtr.Zero,
        };

        Console.WriteLine($"探测 指针=0x{arg.ToInt64():X}  （关键字 '{which}'）");
        Console.Out.Flush();

        // 如果真的崩了，进程会在这里死掉，我们就知道是这个指针
        IntPtr r = fn(arg, 0);
        string s = r == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringUTF8(r) ?? "(decode fail)");
        Console.WriteLine($"  存活，返回: {s.Substring(0, Math.Min(80, s.Length))}");
    }
}
