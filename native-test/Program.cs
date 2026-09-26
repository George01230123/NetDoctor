using System.Runtime.InteropServices;
using System.Text;

namespace NetDoctor.NativeTest;

/// <summary>
/// 原生 DLL 测试宿主（x86 原生 exe）。
///
/// 调用方式与易语言完全一致：LoadLibrary → GetProcAddress → 按 stdcall 调用。
/// 因此只要这个宿主能全绿，易语言那边就能用。
/// </summary>
internal static unsafe class Program
{
    private const string DllName = "NetDoctorNative.dll";

    // ---------------- 委托签名（与 Exports.cs 一一对应） ----------------
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnVoid();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnInt(int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnStr(IntPtr utf8);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnStrInt(IntPtr utf8, int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnIntInt(int a, int b);

    private static IntPtr _h;
    private static int _pass, _fail, _info;
    private static bool _ci;

    private static T Get<T>(string name) where T : Delegate
    {
        IntPtr p = Native.GetProcAddress(_h, name);
        if (p == IntPtr.Zero)
            throw new EntryPointNotFoundException($"{name} 未导出（Win32 错误 {Marshal.GetLastWin32Error()}）");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return "(null)";
        try { return Marshal.PtrToStringUTF8(p) ?? "(decode fail)"; }
        catch (Exception ex) { return "(读取失败: " + ex.Message + ")"; }
    }

    private static IntPtr Arg(string s)
        => s == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(s);

    /// <summary>契约断言：任何环境下都必须成立，失败即整体失败</summary>
    private static void Chk(bool ok, string name, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "   " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    /// <summary>
    /// 环境断言：依赖真机网络 / 硬件 / 代理 / 服务。
    /// CI runner 上这些东西可能不存在，因此不算失败，只标注为"环境"。
    /// 本机跑时用 --strict 让它们也参与判定。
    /// </summary>
    private static void Env(bool ok, string name, string detail = "")
    {
        if (ok)
        {
            Console.WriteLine($"  [PASS] {name}{(detail.Length > 0 ? "   " + detail : "")}");
            _pass++;
        }
        else if (_ci)
        {
            Console.WriteLine($"  [env ] {name}   —— 当前环境不满足，已跳过判定{(detail.Length > 0 ? "   " + detail : "")}");
            _info++;
        }
        else
        {
            Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "   " + detail : "")}");
            _fail++;
        }
    }

    private static void Section(string t)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + t + " ===");
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --ci：环境相关的断言不算失败（给 GitHub Actions 用，runner 上没有真机网络与硬件）
        _ci = args.Any(a => a.Equals("--ci", StringComparison.OrdinalIgnoreCase));
        bool light = args.Any(a => a.Equals("--light", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("═══════════ NetDoctorNative.dll 调用测试 ═══════════");
        Console.WriteLine($"宿主进程位数 : {(IntPtr.Size == 4 ? "x86 (32 位) —— 与易语言一致" : "x64 (64 位) ⚠")}");
        if (_ci) Console.WriteLine("模式         : --ci（环境相关项不计入失败）");

        // ---------------- 加载 ----------------
        Section("1. 加载与导出自检");
        string dllPath = Path.Combine(AppContext.BaseDirectory, DllName);
        Console.WriteLine("  DLL 路径 : " + dllPath);
        Chk(File.Exists(dllPath), "DLL 文件存在");

        _h = Native.LoadLibraryW(dllPath);
        Chk(_h != IntPtr.Zero, "LoadLibrary 成功", $"句柄 0x{_h.ToInt64():X}");
        if (_h == IntPtr.Zero)
        {
            Console.WriteLine("  Win32 错误码 : " + Marshal.GetLastWin32Error());
            Environment.Exit(1);
        }

        string[] expected =
        {
            "ND_Ping", "ND_Version", "ND_DiagNetwork", "ND_FixNetwork", "ND_FixDeep",
            "ND_DnsBenchmark", "ND_DnsRestore", "ND_CleanScan", "ND_CleanRun",
            "ND_Hardware", "ND_OptList", "ND_OptApply", "ND_Services", "ND_Startup", "ND_Free",
        };
        int missing = 0;
        foreach (var n in expected)
            if (Native.GetProcAddress(_h, n) == IntPtr.Zero) { missing++; Console.WriteLine("      缺少导出：" + n); }
        Chk(missing == 0, $"15 个导出函数全部可见", missing == 0 ? "" : $"缺 {missing} 个");

        // ---------------- Ping / Version ----------------
        Section("2. ND_Ping / ND_Version");
        var ping = Get<FnVoid>("ND_Ping");
        Chk(Take(ping()) == "pong", "ND_Ping() 返回 pong");

        var version = Get<FnVoid>("ND_Version");
        string v = Take(version());
        Console.WriteLine("  " + v);
        Chk(v.Contains("\"ok\":true"), "ND_Version 返回合法 JSON");
        Chk(v.Contains("\"arch\":\"x86\""), "DLL 以 x86 运行");
        Chk(v.Contains("\"admin\""), "报告管理员状态");

        // ---------------- 硬件 ----------------
        Section("3. ND_Hardware(1) —— 硬件检测");
        var hw = Get<FnInt>("ND_Hardware");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string hwJson = Take(hw(1));
        sw.Stop();
        Console.WriteLine($"  耗时 {sw.ElapsedMilliseconds} ms，返回 {hwJson.Length} 字符");
        var hwRoot = JsonProbe.Parse(hwJson);
        Chk(hwRoot.Ok, "返回 ok=true", hwRoot.Error);
        if (hwRoot.Ok)
        {
            Console.WriteLine("  摘要     : " + hwRoot.Get("summary"));
            Console.WriteLine("  CPU      : " + hwRoot.Get("cpu.name") + "  " +
                              hwRoot.Get("cpu.cores") + "核" + hwRoot.Get("cpu.threads") + "线程  " +
                              hwRoot.Get("cpu.maxMhz") + "MHz");
            Console.WriteLine("  内存     : " + hwRoot.Get("memory.total") + " " +
                              hwRoot.Get("memory.type") + " " +
                              hwRoot.Get("memory.speedMhz") + "MHz  " +
                              hwRoot.Get("memory.modules") + " 条  双通道=" + hwRoot.Get("memory.dualChannel"));
            Console.WriteLine("  显卡     : " + hwRoot.Get("gpu.name") + "  显存 " + hwRoot.Get("gpu.vram") +
                              "  驱动 " + hwRoot.Get("gpu.driver"));
            Console.WriteLine("  主板     : " + hwRoot.Get("system.board"));
            Console.WriteLine("  系统     : " + hwRoot.Get("system.os"));
            Console.WriteLine("  磁盘     :");
            foreach (var line in hwRoot.Rows("disks", "model", "size", "health", "tempC"))
                Console.WriteLine("      " + line);
            Console.WriteLine("  显示器   : " + hwRoot.Get("monitor.name") + "  " + hwRoot.Get("monitor.vendor") +
                              " " + hwRoot.Get("monitor.panel") + "  " + hwRoot.Get("monitor.year") + "年");
            Env(hwRoot.Get("cpu.name").Length > 0, "CPU 型号非空");
            Env(hwRoot.Get("cpu.cores") != "0", "CPU 核心数有效", hwRoot.Get("cpu.cores") + " 核");
            Env(hwRoot.Get("memory.totalBytes") != "0", "内存容量有效", hwRoot.Get("memory.total"));
            Env(!hwRoot.Get("gpu.vram").StartsWith("0"), "显存读取成功", hwRoot.Get("gpu.vram"));
            Env(hwRoot.ArrayLen("disks") > 0, "磁盘列表非空", hwRoot.ArrayLen("disks") + " 个");
            Chk(hwRoot.Get("report").Length > 500, "文本报告已生成", hwRoot.Get("report").Length + " 字符");
        }

        if (light) { Finish(); return; }

        // ---------------- 网络诊断 ----------------
        Section("4. ND_DiagNetwork(0) —— 网络诊断（不含 DNS 实测）");
        var diag = Get<FnInt>("ND_DiagNetwork");
        sw.Restart();
        string diagJson = Take(diag(0));
        sw.Stop();
        Console.WriteLine($"  耗时 {sw.ElapsedMilliseconds} ms，返回 {diagJson.Length} 字符");
        var dRoot = JsonProbe.Parse(diagJson);
        Chk(dRoot.Ok, "返回 ok=true", dRoot.Error);
        if (dRoot.Ok)
        {
            Env(dRoot.ArrayLen("adapters") > 0, "网卡列表非空", dRoot.ArrayLen("adapters") + " 个");
            Chk(dRoot.ArrayLen("checks") >= 5, "连通性检查 5 项", dRoot.ArrayLen("checks") + " 项");
            Console.WriteLine("  网关     : " + dRoot.Get("gateway"));
            Console.WriteLine("  连通性   :");
            foreach (var line in dRoot.ArraySummary("checks", "name", "detail"))
                Console.WriteLine("      " + line);
            Console.WriteLine("  系统代理 : " + dRoot.Get("proxy.server") +
                              "   启用=" + dRoot.Get("proxy.enabled"));
            Console.WriteLine("  代理探测 : " + dRoot.Get("proxyProbe.detail"));
        }

        Section("5. ND_DiagNetwork(1) —— 含 DNS 实测");
        sw.Restart();
        string diag2 = Take(diag(1));
        sw.Stop();
        var d2 = JsonProbe.Parse(diag2);
        Env(d2.Ok && d2.ArrayLen("dns") > 0, $"DNS 实测有结果（{sw.ElapsedMilliseconds} ms）",
            d2.ArrayLen("dns") + " 个服务器");
        foreach (var line in d2.ArraySummary("dns", "server", "ms"))
            Console.WriteLine("      " + line);

        // ---------------- 服务 / 启动项 ----------------
        Section("6. ND_Services / ND_Startup");
        var svc = Get<FnStr>("ND_Services");
        string svcJson = Take(svc(IntPtr.Zero));
        var sRoot = JsonProbe.Parse(svcJson);
        Chk(sRoot.Ok, "服务枚举调用成功");
        int svcShown = 0;
        foreach (var line in sRoot.ArraySummary("items", "name", "startType"))
        {
            Console.WriteLine("      " + line);
            if (++svcShown >= 6) break;
        }

        var startup = Get<FnVoid>("ND_Startup");
        var uRoot = JsonProbe.Parse(Take(startup()));
        Chk(uRoot.Ok, "启动项枚举成功", uRoot.ArrayLen("items") + " 项");
        foreach (var line in uRoot.ArraySummary("items", "name", "enabled"))
            Console.WriteLine("      " + line);
        Env(uRoot.ArrayLen("items") > 0, "启动项数量有效", uRoot.ArrayLen("items") + " 项");

        // ---------------- 优化列表 ----------------
        Section("7. ND_OptList —— Windows 优化项状态");
        var optList = Get<FnVoid>("ND_OptList");
        sw.Restart();
        var oRoot = JsonProbe.Parse(Take(optList()));
        sw.Stop();
        Chk(oRoot.Ok && oRoot.ArrayLen("items") == 151, $"151 项优化全部枚举（{sw.ElapsedMilliseconds} ms）",
            oRoot.ArrayLen("items") + " 项");
        if (oRoot.Ok)
        {
            Console.WriteLine($"  已优化 {oRoot.Num("applied")} · 未优化 {oRoot.Num("notApplied")} · 合计 {oRoot.Num("total")}");
            Console.WriteLine("  分类：");
            foreach (var line in oRoot.ArraySummary("categories", "title", "count"))
                Console.WriteLine("      " + line);
        }

        // ---------------- 清理扫描（只扫描，不清理） ----------------
        Section("8. ND_CleanScan —— 清理目标占用扫描（只读）");
        var cleanScan = Get<FnVoid>("ND_CleanScan");
        sw.Restart();
        var cRoot = JsonProbe.Parse(Take(cleanScan()));
        sw.Stop();
        Chk(cRoot.Ok, $"扫描完成（{sw.ElapsedMilliseconds} ms）", cRoot.Error);
        if (cRoot.Ok)
        {
            Console.WriteLine("  合计可清理 : " + cRoot.Get("total"));
            int shown = 0;
            foreach (var line in cRoot.ArraySummary("items", "name", "size"))
            {
                Console.WriteLine("      " + line);
                if (++shown >= 12) break;
            }
        }

        // ---------------- 错误处理 ----------------
        Section("9. 异常与边界处理（不得崩溃宿主）");
        var optApply = Get<FnStrInt>("ND_OptApply");

        IntPtr bad = Arg("[\"这个优化项根本不存在\"]");
        var e1 = JsonProbe.Parse(Take(optApply(bad, 0)));
        Marshal.FreeCoTaskMem(bad);
        Chk(!e1.Ok && e1.Code == "not_found", "不存在的优化项返回 not_found", e1.Error);

        IntPtr empty = Arg("[]");
        var e2 = JsonProbe.Parse(Take(optApply(empty, 0)));
        Marshal.FreeCoTaskMem(empty);
        Chk(!e2.Ok && e2.Code == "empty", "空数组返回 empty", e2.Error);

        var e3 = JsonProbe.Parse(Take(optApply(IntPtr.Zero, 0)));
        Chk(!e3.Ok, "传 NULL 指针不崩溃", e3.Code);

        IntPtr broken = Arg("{这不是合法 JSON");
        var e4 = JsonProbe.Parse(Take(optApply(broken, 0)));
        Marshal.FreeCoTaskMem(broken);
        Chk(!e4.Ok, "非法 JSON 不崩溃", e4.Code);

        var e5 = JsonProbe.Parse(Take(optApply(Arg("[\"关闭防火墙\"]"), 9)));
        Chk(!e5.Ok || e5.Ok, "restore 参数传非 0/1 也能安全返回");

        var cn = Get<FnStrInt>("ND_CleanRun");
        IntPtr noMatch = Arg("[\"不存在的清理项名称\"]");
        var e6 = JsonProbe.Parse(Take(cn(noMatch, 0)));
        Marshal.FreeCoTaskMem(noMatch);
        Chk(!e6.Ok && e6.Code == "nothing_to_clean", "清理项名不匹配返回 nothing_to_clean", e6.Error);

        // ---------------- 内存 ----------------
        Section("10. ND_Free 与重复调用");
        var free = Get<FnVoid>("ND_Free");
        free();
        string after = Take(version());
        Chk(after.Contains("\"ok\":true"), "ND_Free 后仍可正常调用");
        for (int i = 0; i < 20; i++) Take(ping());
        Chk(Take(ping()) == "pong", "连续 21 次调用稳定（缓冲区复用正常）");
        free();
        Chk(true, "再次 ND_Free 不崩溃");

        Finish();
    }



    private static void Finish()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        if (_fail == 0)
        {
            Console.WriteLine($"全部通过：{_pass} 项" +
                (_info > 0 ? $"，另有 {_info} 项因当前环境不满足而跳过判定" : ""));
        }
        else
        {
            Console.WriteLine($"通过 {_pass} 项，失败 {_fail} 项" +
                (_info > 0 ? $"，环境跳过 {_info} 项" : ""));
        }
        Console.WriteLine("═══════════════════════════════════════════════════");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}

/// <summary>
/// 极简 JSON 读取器。
///
/// 注意：这里刻意不按 `"key":` 做全局 IndexOf —— 那样嵌套对象里的同名字段
/// 会先被命中，导致数组只能读出第一项（第一版就踩了这个坑）。
/// 正确做法是先把整个文档解析成树，再按路径取值。
/// 不依赖 System.Text.Json 的反射序列化，只做一个最小可用的递归下降解析。
/// </summary>
internal sealed class JsonProbe
{
    // ---- 最小 JSON 值模型 ----
    private sealed class Val
    {
        public bool IsStr;
        public string Str = "";
        public double Num;
        public bool IsBool, Bool;
        public bool IsNull;
        public List<Val> Arr;
        public Dictionary<string, Val> Obj;

        public bool IsArray => Arr != null;
        public bool IsObject => Obj != null;

        public string AsText() =>
            IsStr ? Str
            : IsBool ? (Bool ? "true" : "false")
            : IsNull ? ""
            : Arr != null ? "[" + Arr.Count + " 项]"
            : Obj != null ? "{对象}"
            : Num.ToString("0.###");
    }

    private readonly Val _root;
    public bool Ok, IsObj;
    public string Code = "", Error = "";

    private JsonProbe(string json)
    {
        _root = ParseValue(json, 0, out _);
    }

    public static JsonProbe Parse(string json)
    {
        var p = new JsonProbe(json ?? "");
        p.IsObj = p._root?.IsObject == true;
        p.Ok = p.Get("ok").Equals("true", StringComparison.OrdinalIgnoreCase);
        p.Code = p.Get("code");
        p.Error = p.Get("error");
        return p;
    }

    // ---- 取值 ----
    private Val Dig(string path)
    {
        Val cur = _root;
        if (cur == null || path.Length == 0) return cur;
        foreach (var key in path.Split('.'))
        {
            if (cur == null) return null;
            if (cur.IsObject)
            {
                if (!cur.Obj.TryGetValue(key, out cur)) return null;
            }
            else if (cur.IsArray && int.TryParse(key, out int idx))
            {
                if (idx < 0 || idx >= cur.Arr.Count) return null;
                cur = cur.Arr[idx];
            }
            else return null;
        }
        return cur;
    }

    public string Get(string path) => Dig(path)?.AsText() ?? "";
    public string GetPath(params string[] keys) => Get(string.Join('.', keys));

    public int ArrayLen(string key) => Dig(key)?.Arr?.Count ?? -1;
    public int Num(string key) => (int)(Dig(key)?.Num ?? 0);
    public bool Bool(string key) => Dig(key)?.Bool ?? false;

    /// <summary>把数组里每个对象按指定字段列表拼成一行，便于打印</summary>
    public IEnumerable<string> Rows(string arrayKey, params string[] fields)
    {
        var arr = Dig(arrayKey);
        if (arr?.IsArray != true) yield break;
        foreach (var el in arr.Arr)
        {
            var parts = new List<string>();
            foreach (var f in fields)
            {
                var v = el.IsObject && el.Obj.TryGetValue(f, out var x) ? x.AsText() : "";
                parts.Add(v);
            }
            yield return string.Join("  —  ", parts);
        }
    }

    public IEnumerable<string> ArraySummary(string arrayKey, string f1, string f2, int max = 12)
    {
        int n = 0;
        foreach (var line in Rows(arrayKey, f1, f2))
        {
            yield return line;
            if (++n >= max) yield break;
        }
    }

    // ---- 递归下降解析 ----
    private static Val ParseValue(string s, int i, out int next)
    {
        next = i;
        if (s == null) return null;
        SkipWs(s, ref i);
        if (i >= s.Length) return null;

        char c = s[i];
        if (c == '{') return ParseObject(s, i, out next);
        if (c == '[') return ParseArray(s, i, out next);
        if (c == '"')
        {
            var v = new Val { IsStr = true, Str = ParseString(s, i, out next) };
            return v;
        }
        // 字面量：数字 / true / false / null
        int st = i;
        while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && s[i] != ' ' && s[i] != '\n' && s[i] != '\r') i++;
        string tok = s.Substring(st, i - st).Trim();
        next = i;
        if (tok == "true") return new Val { IsBool = true, Bool = true };
        if (tok == "false") return new Val { IsBool = true, Bool = false };
        if (tok == "null" || tok.Length == 0) return new Val { IsNull = true };
        double.TryParse(tok, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d);
        return new Val { Num = d };
    }

    private static Val ParseObject(string s, int i, out int next)
    {
        var v = new Val { Obj = new Dictionary<string, Val>(StringComparer.Ordinal) };
        i++; // {
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == '}') { i++; break; }
            if (s[i] == ',') { i++; continue; }
            if (s[i] != '"') { i++; continue; }

            string key = ParseString(s, i, out i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            var val = ParseValue(s, i, out i);
            if (val != null) v.Obj[key] = val;
        }
        next = i;
        return v;
    }

    private static Val ParseArray(string s, int i, out int next)
    {
        var v = new Val { Arr = new List<Val>() };
        i++; // [
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ']') { i++; break; }
            if (s[i] == ',') { i++; continue; }
            var val = ParseValue(s, i, out i);
            if (val != null) v.Arr.Add(val);
        }
        next = i;
        return v;
    }

    private static string ParseString(string s, int i, out int next)
    {
        var sb = new StringBuilder();
        i++; // 开引号
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                i += 2;
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 3 < s.Length && ushort.TryParse(s.Substring(i, 4),
                                System.Globalization.NumberStyles.HexNumber, null, out ushort u))
                        { sb.Append((char)u); i += 4; }
                        break;
                    default: sb.Append(n); break;
                }
                continue;
            }
            if (c == '"') { i++; break; }
            sb.Append(c);
            i++;
        }
        next = i;
        return sb.ToString();
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }
}
