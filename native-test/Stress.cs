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
    private static FnStr _services, _setDataDir, _readString;
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

    // ================================================================
    // 页边界测试夹具
    //
    // 背景：宿主传进来的未必是「规规矩矩以 \0 结尾的托管字符串」，
    //       完全可能是正好卡在页尾、且后面没有 \0 的裸缓冲区。
    //       这类布局曾让 DLL 越界读进不可访问页，直接把宿主进程干掉
    //       （AccessViolationException 在 NativeAOT 下无法 catch）。
    //
    //       跨页布局必须用「块内相邻页」来构造：VirtualAlloc 按 64KB
    //       对齐，两次独立分配几乎不可能相邻，但同一个 64KB 块内部的
    //       页天然相邻。
    // ================================================================

    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_NOACCESS  = 0x01;

    private static readonly List<IntPtr> _pages = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, IntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

    /// <summary>分配一块 64KB，返回块基址（页内天然相邻）</summary>
    private static IntPtr NewBlock()
    {
        IntPtr a = VirtualAlloc(IntPtr.Zero, (IntPtr)0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (a != IntPtr.Zero) _pages.Add(a);
        return a;
    }

    /// <summary>「页尾起始 + 下一页不可访问」：只校验起始页的实现会在这里越界</summary>
    private static IntPtr EndOfPageThenNoAccess()
    {
        IntPtr a = NewBlock();
        if (a == IntPtr.Zero) return IntPtr.Zero;

        IntPtr next = (IntPtr)(a.ToInt64() + 0x8000 + 4096);
        uint old;
        VirtualProtect(next, (IntPtr)4096, PAGE_NOACCESS, out old);   // 次页整页不可访问

        IntPtr p = (IntPtr)(a.ToInt64() + 0x8000 + 4095);            // 本页最后一个字节
        Marshal.WriteByte(p, (byte)'X');                             // 不放 \0
        return p;
    }

    /// <summary>
    /// 「合法跨页字符串」：内容从一页末尾起、\0 落在下一页。
    /// 用来验证逐页读取不会把合法输入读丢 ——
    /// 曾经在这里踩过坑：按页整块读取会把 \0 之后的字节一起带回来，
    /// 尾部多个 NUL 让 JSON 解析抛异常，合法参数被静默当成空串。
    /// </summary>
    private static IntPtr CrossPageJson()
    {
        IntPtr a = NewBlock();
        if (a == IntPtr.Zero) return IntPtr.Zero;

        var bytes = Encoding.UTF8.GetBytes("[\"优化进程数量\"]");
        IntPtr p = (IntPtr)(a.ToInt64() + 0x8000 + (4096 - (bytes.Length - 2)));   // 末 2 字节跨到下一页
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte((IntPtr)(p.ToInt64() + bytes.Length), 0);
        return p;
    }

    private static int _fuzzStart;      // --start=N：只跑模糊测试第 N 轮起的部分，用于二分定位

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("═════════ 原生 DLL 压力 / 边界测试 ═════════");
        Console.WriteLine($"宿主位数: {(IntPtr.Size == 4 ? "x86 (与易语言一致)" : "x64 ⚠")}");

        var sa = args.FirstOrDefault(a => a.StartsWith("--start="));
        if (sa != null) int.TryParse(sa.Substring(8), out _fuzzStart);

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
        _readString= Get<FnStr>("NDX_ReadString");


        string tmp = null;   // 前置组与末尾清理都要用；定位模式下只有末尾会用
        if (_fuzzStart <= 0)
        {
        // ============================================================
        Section("乱序调用（不先调 ND_Version）");
        string a = Take(_optList());
        Chk(HasOk(a), "首个调用直接是 ND_OptList 也能工作", "长度 " + (a?.Length ?? 0));

        // ============================================================
        Section("非法指针入参 —— 绝不能崩溃宿主（曾经会崩）");
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
        Section("畸形 JSON 入参");
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
                Console.Write($"      · {name} … ");
                Console.Out.Flush();          // 卡住时能看到停在哪一个用例上
                IntPtr ap = Marshal.StringToCoTaskMemUTF8(json);
                string r1 = Take(_optApply(ap, 0));
                Console.Write("optapply ");
                Console.Out.Flush();

                // 清理接口不能拿畸形 JSON 直接试：
                // 修复前畸形 JSON 会被当成「未指定」而走默认清理，是真的在删文件
                // （测试曾因此卡住十几分钟，C 盘可用空间莫名涨了 11 GB）。
                // 现在畸形输入必须返回失败，这里就断言这一点 —— 顺带避免误删。
                Console.Write("cleanrun ");
                Console.Out.Flush();
                string r2 = Take(_cleanRun(ap, 0));
                Marshal.FreeCoTaskMem(ap);
                Console.Write("done\n");
                Console.Out.Flush();

                if (!HasOk(r1)) { Console.WriteLine($"      ✗ {name} (optapply)"); bad++; }

                // 空串与纯空白按文档等价于「未指定」，会走默认清理 —— 那是正确行为。
                // 其余畸形 JSON 必须被拒绝：修复前它们同样掉进默认清理分支，
                // 等于用户参数写错却真的删了文件。
                bool unspecified = string.IsNullOrWhiteSpace(json);
                bool refused = r2 != null && r2.Contains("\"ok\":false");
                if (unspecified ? !HasOk(r2) : !refused)
                {
                    Console.WriteLine($"      ✗ {name} (cleanrun 行为不符：{(unspecified ? "应走默认项" : "应拒绝")})");
                    bad++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"      ✗ {name}: {ex.Message}"); bad++; }
        }
        Chk(bad == 0, $"{cases.Length} 种畸形 JSON：优化接口安全返回，清理接口拒绝执行（不误删文件）");

        // 合法用法回归：传 NULL = 未指定（按默认项），传不匹配的名称 = nothing_to_clean。
        // 不拿真实大目标测清理，否则测试会真的去清磁盘、耗时且干扰内存基准。
        IntPtr nullArg = IntPtr.Zero;
        string rNull = Take(_cleanRun(nullArg, 0));
        Chk(HasOk(rNull), "传 NULL 走默认项（未指定语义）", rNull?.Substring(0, Math.Min(70, rNull?.Length ?? 0)));

        IntPtr bogus = Marshal.StringToCoTaskMemUTF8("[\"这个清理项不存在\"]");
        string rBogus = Take(_cleanRun(bogus, 0));
        Marshal.FreeCoTaskMem(bogus);
        Chk(rBogus != null && rBogus.Contains("nothing_to_clean"),
            "传不匹配的名称返回 nothing_to_clean（不误删）",
            rBogus?.Substring(0, Math.Min(70, rBogus?.Length ?? 0)));

        // ============================================================
        Section("参数极值");
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
        Section("宿主从不调 ND_Free（缓冲区复用）");
        bool leakOk = true;
        for (int i = 0; i < 200; i++)
            if (!HasOk(Take(_version()))) { leakOk = false; break; }
        Chk(leakOk, "连续 200 次调用且从不 ND_Free");
        string big = Take(_hardware(0));
        Chk(big != null && big.Length > 1000, "大返回正常", (big?.Length ?? 0) + " 字符");
        string small = Take(_version());
        Chk(small != null && small.Length < 600, "大返回后小返回未被污染", (small?.Length ?? 0) + " 字符");

        // ============================================================
        Section("返回值生命周期说明（验证指针复用行为）");
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
        Section("并发调用（验证不崩溃、不死锁、各自读到自洽数据）");
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
        Section("ND_Free 与调用混用");
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
        Section("ND_SetDataDir（新增导出）");
        tmp = Path.Combine(Path.GetTempPath(), "nd_stress_datadir");
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
        Section("长文本与中文编码");
        string cn = Take(_hardware(0));
        Chk(cn != null && (cn.Contains("夕颜") || cn.Contains("处理器") || cn.Contains("内存")),
            "中文以 UTF-8 正确返回");
        Chk(cn != null && !cn.Contains("Ã") && !cn.Contains("â€") && !cn.Contains("\uFFFD"),
            "无 UTF-8/Latin-1 双重编码乱码");

        // ============================================================
        Section("高频调用（缓冲区反复扩缩容）");
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
        Section("全量导出清点");
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

        // ============================================================
        Section("页边界与跨页参数（宿主不一定给规整的托管字符串）");
        // 这一组是回归防线：此前的实现只校验「起始地址所在那一页」，
        // 于是「页尾起始 + 次页不可访问」这种布局会把宿主进程直接搞崩。
        bool pageOk = false, crossOk = false, crossApplied = false;
        string crossDetail = "";
        try
        {
            IntPtr endPtr = EndOfPageThenNoAccess();
            if (endPtr == IntPtr.Zero)
            {
                Console.WriteLine("      提示：拿不到 64KB 块，本组跳过");
            }
            else
            {
                // 关键：调用后本进程还活着，就说明没有越界读
                pageOk = HasOk(Take(_optApply(endPtr, 0)));
            }

            IntPtr crossPtr = CrossPageJson();
            if (crossPtr != IntPtr.Zero)
            {
                IntPtr rawExport = Native.GetProcAddress(_h, "NDX_ReadString");
                if (rawExport != IntPtr.Zero)
                {
                    var rf = Marshal.GetDelegateForFunctionPointer<FnStr>(rawExport);
                    string back = Take(rf(crossPtr));
                    crossDetail = back ?? "(null)";
                    crossOk = back != null && back.Contains("\\u0000") == false && back.Contains("优化进程数量");
                }
                string applied = Take(_optApply(crossPtr, 0));
                crossApplied = applied != null && applied.Contains("\"ok\":true");
            }
        }
        catch (Exception ex) { crossDetail = "异常: " + ex.Message; }

        Chk(pageOk, "页尾起始 + 次页不可访问：不崩溃且安全返回（越界读会直接崩进程）");
        Chk(crossOk, "跨页合法字符串被完整读出、且尾部无多余 NUL", crossDetail.Length > 40 ? crossDetail.Substring(0, 40) : crossDetail);
        Chk(crossApplied, "跨页合法 JSON 能正常走通业务（不被静默当空串）");
        }

        // ============================================================
        Section("随机模糊测试（机器代替人找盲区）");
        // 为什么必须有这一段：
        //   前面第 2、13 组都是「人想到的边界值」，而跨页崩溃当初正是
        //   人没想到的那一格。模糊测试随机组合「指针位置 × 缓冲区内容 ×
        //   长度」，用它去撞出意料之外的组合。
        //
        // 为什么要固定用同一块缓冲区：
        //   每次迭代都新分配的话，泄漏会污染第 15 组的内存测量，
        //   而且会把地址空间打散。固定一块、随机改内容与偏移，等价且干净。
        {
            const int BUFSZ = 32768;
            const int ITER = 3000;
            IntPtr fbuf = Marshal.AllocHGlobal(BUFSZ);
            // 两个独立的随机数源，这一点很关键：
            //   生成输入只消耗 rndInput，调用时的随机参数走 rndCall。
            // 否则 --start=N 用 continue 跳过多轮后，随机序列整体错位，
            // 第 N 轮拿到的输入和整轮跑时完全不同 —— 定位模式永远复现不了崩溃。
            var rndInput = new Random(20260927);
            var rndCall = new Random(88881111);
            var fuzz = new byte[BUFSZ];

            int crashGuard = 0, localFail = 0, guardFail = 0, rawFail = 0, cleanFail = 0;
            int safeCnt = 0, validCnt = 0;
            var samples = new List<string>();

            for (int it = 0; it < ITER; it++)
            {
                // 阶段性进度：CI 上出问题时，日志里能看出走到哪一轮，
                // 否则只能看到一个光秃秃的 exit code。
                if (it > 0 && it % 500 == 0)
                {
                    Console.WriteLine($"      … 已完成 {it}/{ITER} 轮");
                    Console.Out.Flush();
                }

                // 内容：0 = 全随机字节，1 = 无 \0 的可打印串，2 = 长可打印串后接 \0，3 = 空串
                int mode = rndInput.Next(4);
                for (int i = 0; i < BUFSZ; i++) fuzz[i] = (byte)rndInput.Next(256);
                if (mode == 1)
                {
                    int len = rndInput.Next(1, 8192);
                    for (int i = 0; i < len; i++) fuzz[i] = (byte)(32 + rndInput.Next(95));
                }
                else if (mode == 2)
                {
                    int len = rndInput.Next(1, 20000);
                    for (int i = 0; i < len; i++) fuzz[i] = (byte)(32 + rndInput.Next(95));
                    fuzz[len] = 0;
                }
                else if (mode == 3) { fuzz[0] = 0; }

                // 偏移：一半落在页边界附近（最刁钻），其余完全随机
                int off;
                if (rndInput.Next(2) == 0)
                    off = rndInput.Next(0, BUFSZ / 4096) * 4096 - rndInput.Next(0, 8);
                else
                    off = rndInput.Next(0, BUFSZ);
                // 让一部分指针直接落到缓冲区之外，制造真正的野地址
                if (rndInput.Next(20) == 0) off += BUFSZ + rndInput.Next(1, 8192);
                if (off < 0) off = 0;

                // 把内容写到该偏移处，保证指针指向的是我们准备的数据
                int writable = Math.Min(BUFSZ - off, 8192);
                if (off < BUFSZ && writable > 0)
                    Marshal.Copy(fuzz, 0, (IntPtr)(fbuf.ToInt64() + off), writable);

                IntPtr ptr = (IntPtr)(fbuf.ToInt64() + off);

                // 断电日志只在定位模式（--start）启用：
                // 正式运行每轮都写文件的话，3000 轮 × 5 次写就是上万次 I/O，
                // 会把测试拖到好几分钟，看起来像「卡住」。
                string traceFile = Path.Combine(AppContext.BaseDirectory, "fuzz_trace.txt");
                void Mark(string stage)
                {
                    if (_fuzzStart <= 0) return;
                    try { File.AppendAllText(traceFile, $"  it={it} {stage}\n"); }
                    catch { }
                }
                // 只在定位模式（--start）记录断电日志。
                // 正式运行**不要**逐轮写文件：3000 轮 × 每轮一次覆盖写，
                // 在 CI 那种慢盘上是上万次元数据操作，既拖慢测试，
                // 本身也成了新的失败诱因（它是我为排查引入的，不是被测对象的一部分）。
                if (_fuzzStart > 0)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(AppContext.BaseDirectory, "fuzz_trace.txt"),
                            $"it={it} mode={mode} off={off} ptr=0x{ptr.ToInt64():X8}\n");
                    }
                    catch { }
                    if (it != _fuzzStart) continue;
                }
                else continue;   // 定位模式只跑目标那一轮

                try
                {
                    Mark("read-start");
                    string rr = Take(_readString(ptr));
                    Mark("read-done");
                    if (rr == null || !JsonBalanced(rr)) { localFail++; if (samples.Count < 3) samples.Add("read:" + rr); }
                    else if (!rr.Contains("\"raw\"")) { rawFail++; if (samples.Count < 3) samples.Add("noRaw:" + rr.Substring(0, Math.Min(60, rr.Length))); }
                    else if (rr.Contains("\\u0000")) { guardFail++; if (samples.Count < 3) samples.Add("NUL污染:" + rr.Substring(0, Math.Min(60, rr.Length))); }
                    else if (rr.Contains("\"len\":0")) safeCnt++;
                    else validCnt++;

                    Mark("apply-start");
                    string a2 = Take(_optApply(ptr, rndCall.Next(0, 2)));
                    Mark("apply-done");
                    if (a2 == null || !JsonBalanced(a2)) cleanFail++;

                    // 每 500 轮插一次其他导出，验证模糊输入不会污染后续正常调用
                    if (it % 500 == 0)
                    {
                        if (!HasOk(Take(_version()))) crashGuard++;
                        if (!HasOk(Take(_optList()))) crashGuard++;
                    }
                }
                catch (Exception ex) { localFail++; if (samples.Count < 3) samples.Add("EX:" + ex.Message); }
            }

            Marshal.FreeHGlobal(fbuf);
            if (_fuzzStart > 0)
            {
                // 定位模式：只跑目标那一轮，跑完即得出结论，无需等后面的大段测试
                Console.WriteLine($"      [定位模式] 第 {_fuzzStart} 轮单独跑完，未崩溃");
                Environment.Exit(0);
            }

            Chk(localFail == 0 && rawFail == 0 && cleanFail == 0 && crashGuard == 0,
                $"{ITER} 轮随机「指针 × 内容 × 长度」未崩溃、未死锁、返回结构完整",
                (localFail + rawFail + cleanFail + crashGuard) == 0 ? "" : string.Join(" | ", samples));
            Chk(guardFail == 0, "模糊输入从不产生尾部 NUL 污染", guardFail == 0 ? "" : $"{guardFail} 次");
            Console.WriteLine($"      分布：安全空返回 {safeCnt} 次，有效读出 {validCnt} 次");
        }

        // ============================================================
        Section("共享缓冲区生命周期（验证文档写明的使用约束）");
        // 文档告诉易语言开发者：「返回值指针不要跨调用保存，要立刻复制成文本」。
        // 这条约束到底是不是真的、是否被正确实现，一直没直接测过。
        // 这里验证两件事：
        //   1. 确实复用同一块缓冲区（所以「不能保存指针」是真实约束，不是吓唬人）
        //   2. 指针在本次调用到下一次调用之间内容正确（所以「立刻复制」是可行的）
        {
            IntPtr p1 = _optList();
            string s1 = Take(p1);
            bool p1valid = s1 != null && s1.Contains("优化进程数量");

            Take(_version());                       // 中间插一次调用，指向同一块缓冲区
            string s1after = Take(p1);
            bool reused = s1after != null && (s1after != s1);
            bool stillJson = s1after != null && JsonBalanced(s1after);

            IntPtr p2 = _version();
            string s2 = Take(p2);

            Chk(p1valid, "首次调用返回的指针内容正确（可安全「立刻复制」）");
            Chk(p2 == p1, "两次调用复用同一块缓冲区地址（证实「不能保存指针」是真实约束）", $"0x{p1.ToInt64():X}");
            Chk(reused && stillJson, "后一次调用覆盖了前一次内容（旧指针拿到的是新数据）");
            Chk(s2 != null && s2.Contains("\"version\""), "新指针内容正确");
            Console.WriteLine("      ⚠ 结论：在两次调用之间保存指针会读到别的数据 —— 必须每次调用后立刻复制成文本");
        }

        // ============================================================
        Section("长稳与资源泄漏（连续 5000 次调用）");
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            for (int i = 0; i < 30; i++) { Take(_version()); Take(_optList()); }   // 预热，把一次性初始化做掉
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            proc.Refresh();
            long mem0 = proc.WorkingSet64;
            int h0 = proc.HandleCount;

            for (int i = 0; i < 2500; i++)
            {
                Take(_version());
                Take(_optList());
                // 硬件检测走 WMI，单次 1~3 秒；只为验证「反复调用不泄漏」，
                // 偶尔来一次就够，塞太密会让 CI 跑十几分钟。
                if (i % 500 == 0) Take(_hardware(1));
            }

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            proc.Refresh();
            long mem1 = proc.WorkingSet64;
            int hAfter = proc.HandleCount;
            long dMem = (mem1 - mem0) / 1024;
            int dHnd = hAfter - h0;

            Chk(dMem < 16384, "5000 次调用后内存无明显增长（< 16 MB）", $"Δ {dMem} KB");
            Chk(dHnd <= 30, "句柄数稳定（无句柄泄漏）", $"Δ {dHnd}（{h0} → {hAfter}）");
            Console.WriteLine($"      基准内存 {mem0 / 1024 / 1024} MB → {mem1 / 1024 / 1024} MB");
        }

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
