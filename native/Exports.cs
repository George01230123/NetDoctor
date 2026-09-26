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
    // ---------------------------------------------------------------
    private static IntPtr _buf = IntPtr.Zero;
    private static int _cap;

    private static IntPtr Return(string text)
    {
        try
        {
            if (text == null) text = "";
            var bytes = Encoding.UTF8.GetBytes(text);
            int need = bytes.Length + 1;

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
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string Arg(IntPtr utf8)
    {
        if (utf8 == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(utf8) ?? ""; }
        catch { return ""; }
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

    /// <summary>ND_FixDeep() —— 深度修复：重置 Winsock/TCP-IP，完成后必须重启（不会自动重启）</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_FixDeep", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr FixDeep() => Return(NativeApi.FixDeep());

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
        => Return(NativeApi.CleanRun(Arg(namesJson), includeRecycle));

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
        => Return(NativeApi.OptApply(Arg(namesJson), restore));

    // ===============================================================
    // 8. 服务 / 启动项
    // ===============================================================

    /// <summary>ND_Services(filter) —— 枚举第三方服务，filter 传 NULL 或空串为全部</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Services", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Services(IntPtr filter) => Return(NativeApi.Services(Arg(filter)));

    /// <summary>ND_Startup() —— 枚举注册表 Run / 启动文件夹启动项及其启用状态</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Startup", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static IntPtr Startup() => Return(NativeApi.Startup());

    // ===============================================================
    // 9. 内存释放
    // ===============================================================

    /// <summary>ND_Free() —— 释放返回缓冲区。可选，不调用也会在下次调用时复用。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ND_Free", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static void Free()
    {
        try
        {
            if (_buf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buf);
                _buf = IntPtr.Zero;
                _cap = 0;
            }
        }
        catch { }
    }
}
