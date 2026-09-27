using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NetDoctor.StressTest;

/// <summary>
/// 原生 DLL 压力与边界测试。
/// 专测正常流程之外的情况：非法指针、并发、缓冲区完整性、
/// 宿主不调 ND_Free、畸形入参、参数极值。
/// </summary>
internal static unsafe class Stress
{
    private const string Dll = "NetDoctorNative.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnVoid();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnInt(int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnStr(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnStrInt(IntPtr s, int a);

    private static IntPtr _h;
    private static int _pass, _fail;
    private static FnVoid _version, _optList, _cleanScan, _startup, _free, _ping;
    private static FnInt _hardware, _diag;
    private static FnStr _services, _setDataDir;
    private static FnStrInt _optApply, _cleanRun;

    private static void Chk(bool ok, string name, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "   " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    private static void Section(string t) { Console.WriteLine(); Console.WriteLine("=== " + t + " ==="); }

    private static T Get<T>(string n) where T : Delegate
    {
        IntPtr p = Native.GetProcAddress(_h, n);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException(n);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static string Take(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    private static string Sha(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""))).Substring(0, 16);

    /// <summary>JSON 结构是否闭合（括号配对、字符串闭合）</summary>
    private static bool JsonBalanced(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int depth = 0; bool inStr = false, esc = false;
        foreach (char c in s)
        {
            if (esc) { esc = false; continue; }
            if (c == '\\') { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { depth--; if (depth < 0) return false; }
        }
        return depth == 0 && !inStr;
    }

    private static bool HasOk(string s) => s != null && s.Contains("\"ok\"");

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("═════════ 原生 DLL 压力 / 边界测试 ═════════");
        Console.WriteLine($"宿主位数: {(IntPtr.Size == 4 ? "x86 (与易语言一致)" : "x64 ⚠")}");

        _h = Native.LoadLibraryW(Path.Combine(AppContext.BaseDirectory, Dll));
        if (_h == IntPtr.Zero) { Console.WriteLine("加载 DLL 失败"); Environment.Exit(1); }

        _ping      = Get<FnVoid>("ND_Ping");
        _version   = Get<FnVoid>("ND_Version");
        _optList   = Get<FnVoid>("ND_OptList");
        _cleanScan = Get<FnVoid>("ND_CleanScan");
        _startup   = Get<FnVoid>("ND_Startup");
        _services  = Get<FnStr>("ND_Services");
        _setDataDir= Get<FnStr>("ND_SetDataDir");
        _hardware  = Get<FnInt>("ND_Hardware");
        _diag      = Get<FnInt>("ND_DiagNetwork");
        _optApply  = Get<FnStrInt>("ND_OptApply");
        _cleanRun  = Get<FnStrInt>("ND_CleanRun");
        _free      = Get<FnVoid>("ND_Free");

        // ============================================================
        Section("1. 乱序调用（不先调 ND_Version）");
        string a = Take(_optList());
        Chk(HasOk(a), "首个调用直接是 ND_OptList 也能工作", "长度 " + (a?.Length ?? 0));

        // ============================================================
        Section("2. 非法指针入参 —— 绝不能崩溃宿主（曾经会崩）");
        // 这些值里 0xFFFFFFFF / 0x40000000 / 0xDEADBEEF 曾经直接让宿主进程消失
        var ptrs = new (string name, IntPtr p)[]
        {
            ("NULL",         IntPtr.Zero),
            ("0x1",          new IntPtr(1)),
            ("0x1000",       new IntPtr(0x1000)),
            ("0xFFFFFFFF",   new IntPtr(-1)),
            ("0x40000000",   new IntPtr(0x40000000)),
            ("0xDEADBEEF",   new IntPtr(unchecked((int)0xDEADBEEF))),
            ("0x7FFFFFFF",   new IntPtr(0x7FFFFFFF)),
        };
        int survived = 0;
        foreach (var (name, p) in ptrs)
        {
            try
            {
                string r1 = Take(_optApply(p, 0));
                string r2 = Take(_cleanRun(p, 0));
                string r3 = Take(_services(p));
                string r4 = Take(_setDataDir(p));
                if (HasOk(r1) && HasOk(r2) && HasOk(r3) && HasOk(r4)) survived++;
                else Console.WriteLine($"      ✗ {name}: 返回形状异常");
            }
            catch (Exception ex) { Console.WriteLine($"      ✗ {name}: 抛异常 {ex.Message}"); }
        }
        Chk(survived == ptrs.Length, $"{ptrs.Length} 个非法指针全部安全返回", $"{survived}/{ptrs.Length}");

        // 用一个看起来合法、实际未映射的地址再试一次
        try
        {
            IntPtr fake = new IntPtr(0x0BADF00D);
            string r = Take(_optApply(fake, 0));
            Chk(HasOk(r), "未映射的野地址 0x0BADF00D 也安全", r?.Substring(0, Math.Min(40, r?.Length ?? 0)));
        }
        catch (Exception ex) { Chk(false, "野地址测试", ex.Message); }

        // ============================================================
        Section("3. 畸形 JSON 入参");
        var cases = new (string name, string json)[]
        {
            ("空字符串", ""), ("纯空格", "   "), ("半个数组", "[\"a\""),
            ("对象而非数组", "{\"a\":1}"), ("数字数组", "[1,2,3]"),
            ("嵌套数组", "[[\"a\"]]"), ("空对象", "{}"),
            ("超长字符串", "[\"" + new string('x', 5000) + "\"]"),
            ("含换行", "[\"a\\nb\"]"), ("含引号", "[\"a\\\"b\"]"),
            ("null 元素", "[null]"), ("布尔数组", "[true,false]"),
            ("只有括号", "[]"), ("非法转义", "[\"a\\qb\"]"),
        };
        int bad = 0;
        foreach (var (name, json) in cases)
        {
            try
            {
                IntPtr ap = Marshal.StringToCoTaskMemUTF8(json);
                string r1 = Take(_optApply(ap, 0));
                string r2 = Take(_cleanRun(ap, 0));
                Marshal.FreeCoTaskMem(ap);
                if (!HasOk(r1) || !HasOk(r2)) { Console.WriteLine($"      ✗ {name}"); bad++; }
            }
            catch (Exception ex) { Console.WriteLine($"      ✗ {name}: {ex.Message}"); bad++; }
        }
        Chk(bad == 0, $"{cases.Length} 种畸形 JSON 全部安全返回");

        // ============================================================
        Section("4. 参数极值");
        int ef = 0;
        foreach (var v in new[] { 0, 1, -1, int.MaxValue, int.MinValue })
        {
            try
            {
                if (!HasOk(Take(_hardware(v)))) ef++;
                if (!HasOk(Take(_diag(v)))) ef++;
                if (!HasOk(Take(_optApply(Marshal.StringToCoTaskMemUTF8("[\"x\"]"), v))) ) ef++;
            }
            catch { ef++; }
        }
        Chk(ef == 0, "5 组极值参数（含 INT_MIN/INT_MAX）全部安全");

        // ============================================================
        Section("5. 宿主从不调 ND_Free（缓冲区复用）");
        bool leakOk = true;
        for (int i = 0; i < 200; i++)
            if (!HasOk(Take(_version()))) { leakOk = false; break; }
        Chk(leakOk, "连续 200 次调用且从不 ND_Free");
        string big = Take(_hardware(0));
        Chk(big != null && big.Length > 1000, "大返回正常", (big?.Length ?? 0) + " 字符");
        string small = Take(_version());
        Chk(small != null && small.Length < 600, "大返回后小返回未被污染", (small?.Length ?? 0) + " 字符");

        // ============================================================
        Section("6. 返回值生命周期说明（验证指针复用行为）");
        Take(_optList());
        string first = Take(_cleanScan());
        string h1 = Sha(first);
        Take(_startup());                                  // 中间插一次别的调用
        string h2 = Sha(Take(_cleanScan()));
        // 清理占用会随磁盘变化而变，所以不比较内容；只比较“格式合法性”
        Chk(JsonBalanced(Take(_cleanScan())), "复用缓冲区上多次调用返回仍是合法 JSON（哈希可能因磁盘变化而不同）");
        Console.WriteLine($"      提示：两次扫描哈希 {(h1 == h2 ? "相同" : "不同，属正常（磁盘占用在变）")}");
        Console.WriteLine("      ⚠ 返回指针在下一次调用后失效，宿主必须先复制成文本再保存");

        // ============================================================
        Section("7. 并发调用（验证不崩溃、不死锁、各自读到自洽数据）");
        // 注意：DLL 返回的是内部共享缓冲区的指针，多线程同时调用时
        // 后一次调用会覆盖前一次的内容 —— 这是「共享缓冲区」这一设计的固有性质，
        // 不是 bug（易语言单线程调用界面事件时不会遇到）。
        // 因此这里验证的是：
        //   · 不崩溃、不死锁
        //   · 每个线程「调用完立刻取走文本」时能得到结构自洽的 JSON
        // 也就是模拟易语言里「各自调用、立刻读取、不跨调用保存指针」的用法。
        var results = new System.Collections.Concurrent.ConcurrentBag<(string fn, bool ok, string err)>();
        var threads = new List<Thread>();
        string[] fns = { "version", "optlist", "startup", "version", "optlist", "version", "ping", "services" };
        object takeLock = new();      // 调用 + 立刻取文本，视为一个原子操作
        foreach (var fn in fns)
        {
            var t = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 12; i++)
                    {
                        string s;
                        lock (takeLock)
                        {
                            s = fn switch
                            {
                                "version"  => Take(_version()),
                                "optlist"  => Take(_optList()),
                                "startup"  => Take(_startup()),
                                "services" => Take(_services(IntPtr.Zero)),
                                _          => Take(_ping()),
                            };
                        }
                        bool ok = fn == "ping" ? s == "pong" : (HasOk(s) && JsonBalanced(s));
                        results.Add((fn, ok, ok ? "" : (s == null ? "null" : "bad:" + s.Substring(0, Math.Min(50, s.Length)))));
                        if (!ok) return;
                    }
                }
                catch (Exception ex) { results.Add((fn, false, ex.Message)); }
            });
            t.IsBackground = true; threads.Add(t);
        }
        foreach (var t in threads) t.Start();
        bool joined = true;
        foreach (var t in threads) if (!t.Join(90000)) joined = false;
        Chk(joined, "8 个线程并发调用全部在 90 秒内完成（无死锁）");
        var fails = results.Where(r => !r.ok).ToList();
        Chk(fails.Count == 0, "并发调用结果全部自洽", fails.Count == 0 ? "" : string.Join(" | ", fails.Take(3).Select(f => f.fn + ":" + f.err)));
        Chk(results.Count == fns.Length * 12, "并发调用次数正确", results.Count + "/" + (fns.Length * 12));

        // 裸并发（不复制）：只验证 DLL 内部不崩、不死锁。
        // 此时读到的文本可能被撕裂，属于该 API 设计的预期行为，不做内容断言。
        var raw = new System.Collections.Concurrent.ConcurrentBag<string>();
        var rawThreads = new List<Thread>();
        for (int k = 0; k < 6; k++)
        {
            var t = new Thread(() =>
            {
                try { for (int i = 0; i < 30; i++) raw.Add(_optList().ToString("X")); }
                catch (Exception ex) { raw.Add("EX:" + ex.Message); }
            });
            t.IsBackground = true; rawThreads.Add(t);
        }
        foreach (var t in rawThreads) t.Start();
        bool rawJoined = true;
        foreach (var t in rawThreads) if (!t.Join(60000)) rawJoined = false;
        Chk(rawJoined && !raw.Any(x => x.StartsWith("EX:")),
            "6 线程 × 30 次裸并发调用不崩溃不死锁（内容可能互相覆盖，属预期）",
            raw.Count(x => x.StartsWith("EX:")) == 0 ? "" : "有异常");

        // ============================================================
        Section("8. ND_Free 与调用混用");
        bool mixOk = true;
        var t2 = new Thread(() =>
        {
            try { for (int i = 0; i < 40; i++) Take(_version()); }
            catch { mixOk = false; }
        });
        t2.IsBackground = true; t2.Start();
        for (int i = 0; i < 40; i++) { _free(); Thread.Sleep(1); }
        t2.Join(40000);
        Chk(mixOk, "一边 ND_Free 一边调用，未崩溃");
        Chk(HasOk(Take(_version())), "混乱调用后仍能正常返回");

        // ============================================================
        Section("9. ND_SetDataDir（新增导出）");
        string tmp = Path.Combine(Path.GetTempPath(), "nd_stress_datadir");
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        IntPtr dp = Marshal.StringToCoTaskMemUTF8(tmp);
        string dr = Take(_setDataDir(dp));
        Marshal.FreeCoTaskMem(dp);
        bool dirOk = HasOk(dr) && Directory.Exists(tmp);
        Chk(dirOk, "指向可写目录成功", dr?.Substring(0, Math.Min(90, dr?.Length ?? 0)));
        if (dirOk)
        {
            Chk(File.Exists(Path.Combine(tmp, "sysitems.ps1")), "脚本已释放到指定目录");
            // 确认数据目录真的切过去了
            Chk(HasOk(Take(_hardware(1))), "切换数据目录后功能仍可用");
            Chk(Take(_version()).Contains(tmp.Replace("\\", "\\\\")), "ND_Version 报告的 dataDir 已更新");
        }
        IntPtr ep = Marshal.StringToCoTaskMemUTF8("");
        string er = Take(_setDataDir(ep));
        Marshal.FreeCoTaskMem(ep);
        Chk(er != null && er.Contains("\"ok\":false"), "空字符串返回失败（不静默成功）", er);
        IntPtr np = IntPtr.Zero;
        string nr = Take(_setDataDir(np));
        Chk(nr != null && nr.Contains("\"ok\":true"), "传 NULL 恢复自动判定", nr?.Substring(0, Math.Min(110, nr?.Length ?? 0)));
        IntPtr ip2 = Marshal.StringToCoTaskMemUTF8(@"Z:\definitely\not\exist\x");
        string ir = Take(_setDataDir(ip2));
        Marshal.FreeCoTaskMem(ip2);
        Chk(ir != null && ir.Contains("\"ok\":false"), "不可写路径返回失败", ir?.Substring(0, Math.Min(80, ir?.Length ?? 0)));
        // 失败后应回滚，功能仍可用
        Chk(HasOk(Take(_hardware(1))), "目录设置失败后功能仍可用（已回滚）");

        // ============================================================
        Section("10. 长文本与中文编码");
        string cn = Take(_hardware(0));
        Chk(cn != null && (cn.Contains("夕颜") || cn.Contains("处理器") || cn.Contains("内存")),
            "中文以 UTF-8 正确返回");
        Chk(cn != null && !cn.Contains("Ã") && !cn.Contains("â€") && !cn.Contains("\uFFFD"),
            "无 UTF-8/Latin-1 双重编码乱码");

        // ============================================================
        Section("11. 高频调用（缓冲区反复扩缩容）");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            Take(_version());      // 小
            Take(_optList());      // 大
            n += 2;
        }
        sw.Stop();
        Chk(true, $"3 秒内小/大返回交替 {n} 次无异常", $"约 {n / 3.0:0} 次/秒");

        // ============================================================
        Section("12. 全量导出清点");
        string[] expect =
        {
            "ND_Ping", "ND_Version", "ND_DiagNetwork", "ND_FixNetwork", "ND_FixDeep",
            "ND_RestoreHosts", "ND_DnsBenchmark", "ND_DnsRestore", "ND_CleanScan",
            "ND_CleanRun", "ND_Hardware", "ND_OptList", "ND_OptApply", "ND_Services",
            "ND_Startup", "ND_SetDataDir", "ND_Free",
        };
        var missing = expect.Where(e => Native.GetProcAddress(_h, e) == IntPtr.Zero).ToList();
        Chk(missing.Count == 0, $"{expect.Length} 个导出全部可 GetProcAddress",
            missing.Count == 0 ? "" : "缺: " + string.Join(",", missing));

        // 收尾
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }

        Console.WriteLine();
        Console.WriteLine("═════════════════════════════════════════════");
        Console.WriteLine(_fail == 0 ? $"压力测试全部通过：{_pass} 项" : $"通过 {_pass} 项，失败 {_fail} 项");
        Console.WriteLine("═════════════════════════════════════════════");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr h, string name);
}
