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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, IntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr addr, IntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_NOACCESS  = 0x01;

    private static readonly List<IntPtr> _keep = new();

    /// <summary>
    /// 跨页攻击布局：整块 64KB（VirtualAlloc 保证 64KB 对齐，页内天然相邻），
    /// 让字符串从块内某一页的最后一个字节开始，紧接着把下一页设成不可访问。
    /// 起始地址可读 → 只校验起始页的实现在这里会越界读进 NOACCESS 页。
    /// </summary>
    private static IntPtr CrossPage()
    {
        const int OFF = 0x8000;                    // 块内偏移，页对齐
        IntPtr a = VirtualAlloc(IntPtr.Zero, (IntPtr)0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (a == IntPtr.Zero) return IntPtr.Zero;
        _keep.Add(a);

        IntPtr nextPage = (IntPtr)(a.ToInt64() + OFF + 4096);
        uint old;
        VirtualProtect(nextPage, (IntPtr)4096, PAGE_NOACCESS, out old);   // 次页整页不可访问

        IntPtr p = (IntPtr)(a.ToInt64() + OFF + 4095);   // 该页最后一个字节
        Marshal.WriteByte(p, (byte)'X');                 // 无 \0 终止符
        return p;
    }

    /// <summary>页面内偏移 4094 起放 "XY"，即邻近页尾但不是最后一个字节</summary>
    private static IntPtr NearPageEnd()
    {
        IntPtr a = VirtualAlloc(IntPtr.Zero, (IntPtr)4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (a == IntPtr.Zero) return IntPtr.Zero;
        _keep.Add(a);
        IntPtr p = (IntPtr)(a.ToInt64() + 4094);
        Marshal.WriteByte(p, (byte)'X');
        Marshal.WriteByte((IntPtr)(p.ToInt64() + 1), (byte)'Y');
        return p;
    }

    /// <summary>
    /// 正常跨页字符串：从一页的中间开始、\0 落在下一页。
    /// 内容用**真实存在的**优化项名，这样"是否被完整读出"有语义证据：
    ///   读全了 → 返回里含该优化项名
    ///   读漏了 → 报"未指定优化项名称"
    /// </summary>
    private static IntPtr CrossPageValid()
    {
        const int OFF = 0x8000;
        const string s = "[\"优化进程数量\"]";
        IntPtr a = VirtualAlloc(IntPtr.Zero, (IntPtr)0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (a == IntPtr.Zero) return IntPtr.Zero;
        _keep.Add(a);

        var bytes = Encoding.UTF8.GetBytes(s);
        int startInPage = 4096 - (bytes.Length - 2);          // 让最后 2 字节落到下一页
        IntPtr p = (IntPtr)(a.ToInt64() + OFF + startInPage);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte((IntPtr)(p.ToInt64() + bytes.Length), 0);
        return p;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static void Diag(IntPtr p)
    {
        Console.WriteLine($"  分配基址 0x{p.ToInt64() - 4094:X}   探测指针 0x{p.ToInt64():X}");
        Console.WriteLine($"  基址是否 64KB 对齐: {((p.ToInt64() - 4094) & 0xFFFF) == 0}");

        // 手工问系统：从指针起的若干页，各自的提交状态与保护属性
        for (int i = -1; i <= 2; i++)
        {
            IntPtr q = (IntPtr)((p.ToInt64() & ~0xFFF) + i * 4096);
            MEMORY_BASIC_INFORMATION mbi;
            IntPtr n = VirtualQuery(q, out mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (n == IntPtr.Zero) { Console.WriteLine($"  页 {i,+2}: VirtualQuery 失败"); continue; }
            Console.WriteLine($"  页 {i,+2}: 基址=0x{q.ToInt64():X} State=0x{mbi.State:X} Protect=0x{mbi.Protect:X} 区域=0x{mbi.RegionSize.ToInt64():X}");
        }

        // 用 IPC 把这段内存读回来，确认"数据布局"与"宿主能看见的内容"一致。
        // 这一步不碰 DLL，纯验证测试夹具本身是否摆对了。
        var back = new byte[80];
        IntPtr got;
        bool rok = ReadProcessMemory(GetCurrentProcess(), p, back, (IntPtr)back.Length, out got);
        if (rok)
        {
            int n = (int)got.ToInt64();
            int z = 0; while (z < n && back[z] != 0) z++;
            Console.WriteLine($"  读回 {n} 字节，\\0 在偏移 {z}: \"{Encoding.UTF8.GetString(back, 0, z)}\"");
        }
        else
        {
            Console.WriteLine("  读回失败（夹具本身有问题）");
        }

        // 只报告布局事实，绝不真的越界读：
        // 这里读崩了的话崩的是探测工具自己，会掩盖「DLL 是否安全」的结论。
        IntPtr end = (IntPtr)(p.ToInt64() + 2);
        MEMORY_BASIC_INFORMATION e;
        IntPtr en = VirtualQuery(end, out e, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
        Console.WriteLine($"  字符串结束位置 0x{end.ToInt64():X}: " +
            (en == IntPtr.Zero ? "VirtualQuery 失败"
             : $"State=0x{e.State:X} Protect=0x{e.Protect:X} → " +
               (e.State == MEM_COMMIT && (e.Protect & PAGE_NOACCESS) == 0 ? "可读" : "不可读（越界读必崩）")));
    }

    /// <summary>独立证明：从该指针往后读 3 字节是否真会崩（不调 DLL）</summary>
    private static void RawCrash(IntPtr p)
    {
        Console.Write("  直接读偏移 2（越界）… ");
        Console.Out.Flush();
        byte b = Marshal.ReadByte((IntPtr)(p.ToInt64() + 2));
        Console.WriteLine($"居然没崩，读到 0x{b:X2}");
    }

    /// <summary>地址回绕分析：不做任何读取，只用 IPC / VirtualQuery 判断可读性</summary>
    private static void DiagWrap(IntPtr p)
    {
        long v = p.ToInt64();
        Console.WriteLine($"  指针 0x{v:X}  距 32 位上限还差 0x{0x100000000L - v:X} 字节");
        Report(v);
        Report(v + 4);          // 偏移几字节后
        Report(v + 4096);       // 再往前一页
        Console.WriteLine("  ↑ 若偏移后的地址「可读」，说明此处一旦发生 32 位回绕，");
        Console.WriteLine("    就会从读野指针变成读合法内存 —— 行为不可预期。");

        void Report(long a)
        {
            long wrapped = a & 0xFFFFFFFFL;
            bool didWrap = a > 0xFFFFFFFFL;
            var mbi = default(MEMORY_BASIC_INFORMATION);
            IntPtr n = VirtualQuery((IntPtr)wrapped, out mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            string state = n == IntPtr.Zero ? "VirtualQuery失败"
                : $"State=0x{mbi.State:X} Protect=0x{mbi.Protect:X} " +
                  (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_NOACCESS) == 0 ? "可读" : "不可读");
            Console.WriteLine($"    0x{a:X} → 实际查询 0x{wrapped:X}{(didWrap ? " (已回绕!)" : "")}  {state}");
        }
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        char which = args.Length > 0 ? args[0][0] : 'a';

        IntPtr h = LoadLibraryW(Path.Combine(AppContext.BaseDirectory, Dll));
        if (h == IntPtr.Zero) { Console.WriteLine("LOAD_FAIL"); return; }

        // 'w'：只测「指针 → 字符串」这一段，绕开全部业务解析
        if (which == 'w')
        {
            IntPtr hp = GetProcAddress(h, "NDX_ReadString");
            if (hp == IntPtr.Zero) { Console.WriteLine("EXPORT_FAIL: 没有 NDX_ReadString"); return; }
            var pf = Marshal.GetDelegateForFunctionPointer<FnStrInt>(hp);
            IntPtr vp = CrossPageValid();
            if (vp == IntPtr.Zero) { Console.WriteLine("布局构造失败"); return; }
            Diag(vp);
            Console.WriteLine("  --- 调用 NDX_ReadString（只读字符串）---");
            Console.Out.Flush();
            IntPtr vr = pf(vp, 0);
            Console.WriteLine($"  存活，返回: {(vr == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUTF8(vr))}");
            return;
        }

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
            'p' => NearPageEnd(),             // 页尾起始、无 \0 终止符（不跨页）
            'q' => CrossPage(),               // 页尾起始、下一页不可访问（跨页攻击）
            'y' => CrossPageValid(),          // 正常跨页字符串（应被正确读出）
            // 地址回绕：起始指针已接近 32 位地址空间上限，
            // 若实现里用 p + 偏移 计算地址而不检查溢出，就会绕回低地址 ——
            // 于是「读一个野指针」变成「读一块合法内存」，行为完全不可预期。
            // 这两个值由模糊测试撞出来后补上，此前 10 种布局都没覆盖到。
            'A' => new IntPtr(unchecked((int)0xFFFFFFF0)),
            'B' => new IntPtr(unchecked((int)0x7FFFFFF0)),
            _   => IntPtr.Zero,
        };

        Console.WriteLine($"探测 指针=0x{arg.ToInt64():X}  （关键字 '{which}'）");
        if (arg == IntPtr.Zero && "p q y".Contains(which))
        {
            Console.WriteLine("  布局构造失败，本项跳过");
            return;
        }
        if ("p q y".Contains(which)) Diag(arg);
        if ("AB".Contains(which)) DiagWrap(arg);
        if (which == 'n')
        {
            // 基线：普通托管字符串，走同一条调用路径
            IntPtr np = Marshal.StringToCoTaskMemUTF8("[\"x\"]");
            Console.WriteLine("  --- 调用 DLL（普通托管字符串 \"[\\\"x\\\"]\"）---");
            Console.Out.Flush();
            IntPtr nr = fn(np, 0);
            Console.WriteLine($"  存活，返回: {(nr == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUTF8(nr))}");
            return;
        }
        if (which == 'r') { RawCrash(arg); return; }
        Console.WriteLine("  --- 开始调用 DLL ---");
        Console.Out.Flush();

        // 如果真的崩了，进程会在这里死掉，我们就知道是这个指针
        IntPtr r = fn(arg, 0);
        string s = r == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringUTF8(r) ?? "(decode fail)");
        Console.WriteLine($"  存活，返回: {s.Substring(0, Math.Min(80, s.Length))}");
    }
}
