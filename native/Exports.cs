using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetDoctor.Core;

namespace NetDoctor.Native;

/// <summary>
/// 原生导出层。
///
/// 约定（与 docs/原生接口.md 一致）：
///   · 所有函数 __stdcall，32 位
///   · 返回值统一为 UTF-8 JSON 的 C 字符串指针，形如 {"ok":true,...} 或 {"ok":false,"code":"...","error":"..."}
///   · DLL 内部持有一块复用的缓冲区，返回的指针在下次调用或 ND_Free 之前有效
///   · 调用方用完请调 ND_Free()；易语言里也可以不调，下次调用会自动复用
///   · 任何异常都会被捕获并转成 {"ok":false,...}，不会让宿主崩溃
/// </summary>
internal static class Exports
{
    // ---------------------------------------------------------------
    // 缓冲区：DLL 自己分配、自己复用，避免宿主管理内存出错
    //
    // ⚠ 必须加锁：宿主可能从多个线程同时调用（易语言里很常见）。
    //   若不加锁，两个线程会同时往这一块缓冲区写，
    //   结果是先返回的那个指针内容被后一个调用**撕裂**，
    //   读出来就是乱码或 JSON 不完整。
    //   曾经因为清理"无用字段"时顺手删掉了这把锁，压测立刻抓到并发乱码。
    //
    // 说明：这里只锁"写缓冲区"这一小段（微秒级），
    //       耗时的业务逻辑（几秒的检测/清理）不在锁内，不影响并发能力。
    // ---------------------------------------------------------------
    private static readonly object _bufLock = new();
    private static IntPtr _buf = IntPtr.Zero;
    private static int _cap;

    private static IntPtr Return(string text)
    {
        try
        {
            if (text == null) text = "";
            var bytes = Encoding.UTF8.GetBytes(text);
            int need = bytes.Length + 1;

            lock (_bufLock)
            {
                if (need > _cap)
                {
                    int newCap = Math.Max(need * 2, 64 * 1024);
                    // 先建后拆：必须等新缓冲区分配成功再释放旧的。
                    // 之前是先 Free 再 Alloc，一旦 Alloc 失败（或抛异常），
                    // _buf 就悬空指向已释放内存，后续每次调用都会踩它 ——
                    // 属于 use-after-free，而不是「这次调用失败」这么简单。
                    IntPtr newBuf;
                    try { newBuf = Marshal.AllocHGlobal(newCap); }
                    catch { return IntPtr.Zero; }          // 保持旧缓冲区仍可用

                    IntPtr old = _buf;
                    _buf = newBuf;
                    _cap = newCap;
                    if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
                }
                Marshal.Copy(bytes, 0, _buf, bytes.Length);
                Marshal.WriteByte(_buf, bytes.Length, 0);
                return _buf;
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }


    // ===============================================================
    // 入参指针校验
    //
    // 为什么必须做这件事：
    //   UTF-8 字符串读取要解引用指针去找结尾的 \0。
    //   宿主一旦传了野指针（例如 0xFFFFFFFF、0xDEADBEEF），就会触发访问冲突；
    //   而 .NET / NativeAOT 下 AccessViolationException **无法被 catch 捕获** ——
    //   结果是宿主进程直接崩溃，而不是返回 {"ok":false}。
    //   对易语言来说，就是「一个参数写错，整个工具箱闪退」。
    //
    // 办法：先用 VirtualQuery 问系统这段地址是否已提交且可读，是才去读。
    //       这是确定性的、不依赖 SEH 的做法。
    // ===============================================================

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint MEM_COMMIT   = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD    = 0x100;
    // READONLY | READWRITE | EXECUTE_READ | EXECUTE_READWRITE | WRITECOPY | EXECUTE_WRITECOPY
    private const uint READABLE = 0x02 | 0x04 | 0x20 | 0x40 | 0x08 | 0x80;

    /// <summary>这段地址能否安全按 C 字符串读取</summary>
    private static bool IsReadable(IntPtr p)
    {
        if (p == IntPtr.Zero) return false;
        if (p.ToInt64() < 0x10000) return false;          // 低地址一律拒绝
        if (p.ToInt64() > 0x7FFFFFFF) return false;       // 32 位用户态上限

        try
        {
            var mbi = default(MEMORY_BASIC_INFORMATION);
            IntPtr n = VirtualQuery(p, out mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (n == IntPtr.Zero) return false;
            if (mbi.State != MEM_COMMIT) return false;
            // PAGE_GUARD / PAGE_NOACCESS 必须单独判掉：
            // 这两者是"已提交但不许访问"，State 仍是 MEM_COMMIT，
            // 只查 State 会把它们当成可读，读下去照样崩宿主。
            if ((mbi.Protect & PAGE_NOACCESS) != 0) return false;
            if ((mbi.Protect & PAGE_GUARD) != 0) return false;
            return (mbi.Protect & READABLE) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 安全扫出 C 字符串长度。
    ///
    /// 为什么不自己逐字节读、也不自己判断页边界：
    ///   本进程内直接解引用野指针会触发访问冲突，而 .NET / NativeAOT 下
    ///   AccessViolationException **无法被 catch**，宿主进程会直接消失。
    ///   自己用 VirtualQuery 判断「下一页可不可读」理论上可行，但校验与读取
    ///   之间存在窗口，且对 MEM_FREE / PAGE_GUARD / 大页等情形容易漏判 ——
    ///   实测仍有布局能让宿主崩掉。
    ///
    /// 做法：交给内核。ReadProcessMemory 会**逐页校验**整个范围，
    ///       任何一页不可读就整块失败并返回错误，绝不触发访问冲突。
    ///       这是结构上不可能崩溃的读取方式。
    ///
    /// 循环不变量：每一轮要么推进 total、要么直接 return。
    ///   （曾经写成「推进 total」放在条件分支里，遇到
    ///     ReadProcessMemory 成功但只返回 0 字节的情况就死循环，
    ///     表现为宿主调用后卡住不返回 —— 已按此不变量重写。）
    ///
    /// 32 位下必须防地址回绕：
    ///   起始指针可能已经接近 0xFFFFFFFF（模糊测试随机生成的野地址就是），
    ///   此时 p + total 会溢出 32 位并绕回低地址，
    ///   于是「读一个野指针」变成「读一块合法内存」，行为完全不可预期。
    ///   每一轮都先校验加上偏移后仍落在合法用户态范围内，否则当场收尾。
    ///
    /// 数据全部来自调用方自身进程（P/Invoke 同进程传参），因此不存在
    /// 「读别的进程内存」的权限与隐私问题。
    /// </summary>
    private const long UserMax = 0x7FFF0000L;      // 32 位用户态上限，留一点余量

    private static int ScanString(IntPtr p, int max)
    {
        if (p == IntPtr.Zero || max <= 0) return 0;

        long pbase = p.ToInt64();
        if (pbase < 0x10000L || pbase >= UserMax) return 0;

        IntPtr self = GetCurrentProcess();
        byte[] chunk = new byte[4096];
        int total = 0;

        while (total < max && total < 0x100000)
        {
            long curL = pbase + total;
            if (curL < 0x10000L || curL >= UserMax) return total;   // 防回绕

            IntPtr cur = (IntPtr)curL;

            // 一次只读到「当前所在页的页尾」，绝不跨页请求：
            // 跨页请求会被内核整块拒绝，反而读不到本页末尾的有效数据。
            int pageLeft = 4096 - (int)(curL & 0xFFF);
            int want = Math.Min(pageLeft, max - total);
            if (want <= 0) return total;                   // 页大小异常，保守收尾

            IntPtr got;
            if (!ReadProcessMemory(self, cur, chunk, (IntPtr)want, out got))
            {
                // 本页尾部不可读：缩短到一半再试；
                // 缩到 1 字节仍失败，说明当前位置就是边界，到此为止。
                if (want <= 1) return total;
                want >>= 1;
                if (!ReadProcessMemory(self, cur, chunk, (IntPtr)want, out got))
                    return total;
            }

            int n = (int)got.ToInt64();
            if (n <= 0) return total;                      // 兜底，保证必然推进

            int usable = Math.Min(n, want);
            for (int i = 0; i < usable; i++)
            {
                if (chunk[i] == 0) return total + i;
                total++;
                if (total >= max) return total;
            }

            // 读到的比要的少，说明后面就是不可读边界
            if (n < want) return total;
        }
        return total;
    }

    /// <summary>
    /// 安全读取 UTF-8 入参：非法指针一律当空串，绝不崩溃宿主。
    /// 若需要区分「传了 NULL」和「传了空串」，用 ArgSafeEx。
    /// </summary>
    private static string ArgSafe(IntPtr utf8) => ArgSafeEx(utf8, out _);

    /// <summary>
    /// 同 ArgSafe，但额外告诉调用方原始指针是不是 NULL。
    /// 有些接口 NULL 表示「用默认值」，空串表示「参数非法」，必须区分。
    /// </summary>
    private static string ArgSafeEx(IntPtr utf8, out bool wasNull)
    {
        wasNull = utf8 == IntPtr.Zero;
        if (!IsReadable(utf8)) return "";
        try
        {
            const int cap = 1 << 20;                       // 参数长度上限 1 MB
            // ScanString 用 ReadProcessMemory 读，只会返回「确实读到」的长度，
            // 因此后面的 Marshal.Copy 一定落在可读范围内，不会再越界。
            int len = ScanString(utf8, cap);
            if (len == 0) return "";
            // ScanString 为保证「绝不越界读」，是按页边界整块读取的，
            // 尾巴上可能多带回若干 NUL（跨页且 \0 落在页尾附近时）。
            // 这些多余 NUL 会让 JSON 解析直接抛异常 ——
            // 表现为「字符串读到了却当成空串」，功能静默失效。
            // 所以这里必须按第一个 \0 截断，不能直接用 len。
            var buf = new byte[len];
            Marshal.Copy(utf8, buf, 0, len);

            int real = 0;
            while (real < len && buf[real] != 0) real++;
            if (real == 0) return "";
            return Encoding.UTF8.GetString(buf, 0, real);
        }
        catch
        {
            return "";
        }
    }

    // ===============================================================
    // 1. 版本与自检
    // ===============================================================

    /// <summary>ND_Version() -> {"ok":true,"version":"1.3.0","arch":"x86",...}</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Version", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Version() => Return(NativeApi.Version());

    /// <summary>ND_Ping() -> 恒定返回 "pong"，用于宿主自检 DLL 是否加载成功</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Ping", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Ping() => Return("pong");

    // ===============================================================
    // 2. 网络诊断
    // ===============================================================

    /// <summary>ND_DiagNetwork(dnsTest) —— dnsTest 非 0 时顺带实测每个 DNS，耗时更久</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_DiagNetwork", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr DiagNetwork(int dnsTest) => Return(NativeApi.DiagNetwork(dnsTest));

    // ===============================================================
    // 3. 断网修复
    // ===============================================================

    /// <summary>ND_FixNetwork(mode) —— mode=0 轻量(刷DNS/清代理)，mode=1 一键修复</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_FixNetwork", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr FixNetwork(int mode) => Return(NativeApi.FixNetwork(mode));

    /// <summary>
    /// ND_FixDeep() —— 深度修复：重置 Winsock/TCP-IP，完成后必须重启（不会自动重启）。
    /// 刻意不含 hosts 还原，避免清掉用户自定义条目。
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_FixDeep", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr FixDeep() => Return(NativeApi.FixDeep());

    /// <summary>
    /// ND_RestoreHosts() —— 单独还原 hosts 为 Windows 默认内容。
    /// ⚠ 会清除用户自己添加的所有条目（广告屏蔽、内网域名映射等），不可逆。
    ///   请在宿主界面里明确提示并二次确认后再调用。
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_RestoreHosts", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr RestoreHosts() => Return(NativeApi.RestoreHosts());

    /// <summary>
    /// ND_SetDataDir(dir) —— 指定 DLL 的落盘目录（释放脚本、日志、快照都在这里）。
    ///   · 传 NULL       -> 恢复自动判定（宿主 exe 同级的 .runtime）
    ///   · 传空字符串    -> 返回 {"ok":false,"code":"empty"}
    ///   · 传不可写目录  -> 返回 {"ok":false,"code":"io"}
    /// 典型用途：不让 DLL 往宿主程序目录（如 Program Files）写东西。
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_SetDataDir", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr SetDataDir(IntPtr dir)
    {
        string d = ArgSafeEx(dir, out bool wasNull);
        // NULL = 恢复默认；空串 = 参数非法
        return Return(NativeApi.SetDataDir(wasNull ? null : d, wasNull));
    }

    // ===============================================================
    // 4. DNS
    // ===============================================================

    /// <summary>ND_DnsBenchmark(applyBest) —— 测速 12 个公共 DNS；applyBest 非 0 时自动应用最快的</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_DnsBenchmark", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr DnsBenchmark(int applyBest) => Return(NativeApi.DnsBenchmark(applyBest));

    /// <summary>ND_DnsRestore() —— 恢复为自动获取 DNS</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_DnsRestore", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr DnsRestore() => Return(NativeApi.DnsRestore());

    // ===============================================================
    // 5. 垃圾清理
    // ===============================================================

    /// <summary>ND_CleanScan() —— 扫描各清理目标占用，返回名称/占用/是否默认勾选</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_CleanScan", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr CleanScan() => Return(NativeApi.CleanScan());

    /// <summary>
    /// ND_CleanRun(namesJson, includeRecycle) —— namesJson: UTF-8 JSON 字符串数组。
    /// 传 NULL 或空串表示「未指定」，按默认项清理；
    /// 传了内容但解析不出名称则返回 empty 错误 —— 清理是破坏性操作，
    /// 参数无效时不能猜用户意图去删文件。
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_CleanRun", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr CleanRun(IntPtr namesJson, int includeRecycle)
    {
        string s = ArgSafeEx(namesJson, out bool wasNull);
        return Return(NativeApi.CleanRun(s, includeRecycle, wasNull || string.IsNullOrWhiteSpace(s)));
    }

    // ===============================================================
    // 6. 硬件
    // ===============================================================

    /// <summary>ND_Hardware(compact) —— 硬件检测，返回结构化字段 + 完整文本报告</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Hardware", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Hardware(int compact) => Return(NativeApi.Hardware(compact));

    // ===============================================================
    // 7. Windows 优化
    // ===============================================================

    /// <summary>ND_OptList() —— 枚举 151 项优化及其当前状态</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_OptList", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr OptList() => Return(NativeApi.OptList());

    /// <summary>ND_OptApply(namesJson, restore) —— restore=0 应用，非 0 按官方方案还原</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_OptApply", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr OptApply(IntPtr namesJson, int restore)
        => Return(NativeApi.OptApply(ArgSafe(namesJson), restore));

    // ===============================================================
    // 8. 服务 / 启动项
    // ===============================================================

    /// <summary>ND_Services(filter) —— 枚举第三方服务，filter 传 NULL 或空串为全部</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Services", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Services(IntPtr filter) => Return(NativeApi.Services(ArgSafe(filter)));

    /// <summary>ND_Startup() —— 枚举注册表 Run / 启动文件夹启动项及其启用状态</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Startup", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Startup() => Return(NativeApi.Startup());

    // ===============================================================
    // 9. 内存释放
    // ===============================================================

    /// <summary>
    /// ND_Free() —— 释放返回缓冲区。可选，不调用也会在下次调用时复用。
    ///
    /// ⚠ 必须在同一把锁下释放：否则可能正好释放掉另一个线程正在写的缓冲区，
    ///   导致那块内存被复用后内容错乱。
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Free", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static void Free()
    {
        try
        {
            lock (_bufLock)
            {
                if (_buf != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buf);
                    _buf = IntPtr.Zero;
                    _cap = 0;
                }
            }
        }
        catch { }
    }

    // ===============================================================
    // 诊断专用导出（临时，验证入参读取链路用，验证完删除）
    //
    // 作用：只做「把指针安全读成字符串」这一件事，不做任何业务处理。
    //       这样就能把「入参读取」和「业务解析」两段分开定位 ——
    //       业务接口返回 code=empty 时，无法判断是读不到还是解析不到。
    // ===============================================================

    /// <summary>NDX_ReadString(p) —— 返回 {"raw":"读到的内容","len":长度}，只做安全读取</summary>
    [UnmanagedCallersOnly(EntryPoint = "NDX_ReadString", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr ReadStringProbe(IntPtr p)
    {
        string s = ArgSafe(p);
        return Return("{\"raw\":" + J(s) + ",\"len\":" + s.Length + "}");
    }

    private static string J(string s)
    {
        if (s == null) return "null";
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
