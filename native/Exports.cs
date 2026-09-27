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
                    if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                    _buf = Marshal.AllocHGlobal(newCap);
                    _cap = newCap;
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
            int len = 0;
            while (len < cap && Marshal.ReadByte(utf8, len) != 0) len++;
            if (len == 0) return "";
            var buf = new byte[len];
            Marshal.Copy(utf8, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
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

    /// <summary>ND_CleanRun(namesJson, includeRecycle) —— namesJson: UTF-8 JSON 字符串数组，空则用默认项</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_CleanRun", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr CleanRun(IntPtr namesJson, int includeRecycle)
        => Return(NativeApi.CleanRun(ArgSafe(namesJson), includeRecycle));

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
}
