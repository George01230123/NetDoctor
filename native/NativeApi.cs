using System.Text.Json;
using System.Text;

namespace NetDoctor.Core;

/// <summary>
/// 原生 DLL 的对外接口实现。
/// 所有导出函数都返回 UTF-8 JSON，字符串内存由 DLL 内部分配、调用 ND_Free 释放。
/// 全部为同步阻塞调用（调用方通常是易语言的界面线程，命令本身就要几秒）。
/// </summary>
internal static class NativeApi
{

    // ===============================================================
    // 工具
    // ===============================================================
    // ===============================================================
    // JSON 工具
    //
    // ⚠ 这里必须手写转义，不能用 JsonSerializer.Serialize()：
    //   NativeAOT 下反射被裁剪（IlcDisableReflection），
    //   反射式序列化会直接抛 JsonSerializerIsReflectionDisabled 崩溃宿主。
    //   读取输入仍可用 JsonDocument.Parse（走的是源生成器路径，AOT 安全）。
    // ===============================================================
    private static string J(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);        // 中文等直接输出，UTF-8 编码后即可
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Ok(string extraJson = "")
        => "{\"ok\":true" + (extraJson.Length > 0 ? "," + extraJson : "") + "}";

    private static string Err(string code, string msg)
        => "{\"ok\":false,\"code\":" + J(code) + ",\"error\":" + J(msg ?? "") + "}";

    /// <summary>把所有 Log 输出收集到内存字符串，随返回值一起交给调用方</summary>
    private sealed class LogCatcher : IDisposable
    {
        public readonly StringBuilder Sb = new();
        private readonly Action<string> _handler;
        public LogCatcher()
        {
            _handler = line => { lock (Sb) Sb.AppendLine(line); };
            Log.Line += _handler;
        }
        public string Text { get { lock (Sb) return Sb.ToString(); } }
        public void Dispose() => Log.Line -= _handler;
    }

    /// <summary>
    /// 把同步调用包起来：异常转错误 JSON、记录日志、保证日志钩子一定被摘掉。
    ///
    /// 注意：**入参的解码必须发生在进入这里之前**。
    /// 因为非法指针导致的访问冲突无法被 catch（.NET 下 AccessViolationException
    /// 不可捕获），把它包在 try 里也没用，必须在取指针之前就校验掉。
    /// 见 Exports.ArgSafe()。
    /// </summary>
    private static string Guard(string func, Func<string> body)
    {
        try
        {
            using var catcher = new LogCatcher();
            try
            {
                string payload = body();
                // 把日志附在末尾
                string logs = catcher.Text;
                if (payload.Length > 2 && payload[^1] == '}')
                    payload = payload.Substring(0, payload.Length - 1) + ",\"log\":" + J(logs) + "}";
                return payload;
            }
            catch (Exception ex)
            {
                string logs = catcher.Text;
                return "{\"ok\":false,\"code\":\"exception\",\"error\":" + J(ex.Message) +
                       ",\"log\":" + J(logs) + "}";
            }
        }
        finally
        {
        }
    }

    private static T Await<T>(Task<T> t) => t.GetAwaiter().GetResult();
    private static void Await(Task t) => t.GetAwaiter().GetResult();

    // ===============================================================
    // 1. 版本与自检
    // ===============================================================
    public static string Version()
    {
        var asm = typeof(NativeApi).Assembly;
        return Ok(
            "\"name\":" + J("夕颜若雪网络工具") +
            ",\"api\":" + J("1") +
            ",\"version\":" + J(asm.GetName().Version?.ToString(3) ?? "1.3.0") +
            ",\"arch\":" + J(IntPtr.Size == 4 ? "x86" : "x64") +
            ",\"processArch\":" + J(Environment.Is64BitProcess ? "x64" : "x86") +
            ",\"clr\":\"nativeaot\"" +
            ",\"os\":" + J(Environment.OSVersion.VersionString) +
            ",\"admin\":" + (AdminHelper.IsAdmin() ? "true" : "false") +
            ",\"dataDir\":" + J(EmbeddedScripts.Dir)
        );
    }

    // ===============================================================
    // 2. 网络诊断
    // ===============================================================
    public static string DiagNetwork(int includeDnsTest)
    {
        return Guard("diag", () =>
        {
            Log.Info("原生调用：网络诊断");
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true");

            // 网卡
            var adapters = NetworkDiag.Adapters();
            sb.Append(",\"adapters\":[");
            bool first = true;
            foreach (var a in adapters)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(a.Name))
                  .Append(",\"type\":").Append(J(a.Type))
                  .Append(",\"desc\":").Append(J(a.Desc))
                  .Append(",\"up\":").Append(a.Up ? "true" : "false")
                  .Append(",\"ipv4\":").Append(J(a.IPv4))
                  .Append(",\"mask\":").Append(J(a.Mask))
                  .Append(",\"gateway\":").Append(J(a.Gateway))
                  .Append(",\"dns\":").Append(J(a.Dns))
                  .Append(",\"dhcp\":").Append(J(a.Dhcp))
                  .Append(",\"speed\":").Append(J(a.Speed))
                  .Append(",\"mac\":").Append(J(a.Mac))
                  .Append('}');
            }
            sb.Append(']');

            // 分层连通性
            var (checks, gw) = Await(NetworkDiag.Connectivity());
            sb.Append(",\"gateway\":").Append(J(gw));
            sb.Append(",\"checks\":[");
            first = true;
            foreach (var c in checks)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(c.Name))
                  .Append(",\"level\":").Append(J(c.Level.ToString()))
                  .Append(",\"detail\":").Append(J(c.Detail))
                  .Append(",\"hint\":").Append(J(c.Hint))
                  .Append('}');
            }
            sb.Append(']');

            // DNS 实测
            if (includeDnsTest != 0)
            {
                sb.Append(",\"dns\":[");
                first = true;
                foreach (var s in NetworkDiag.DnsServers())
                {
                    var r = Await(NetworkDiag.TestDnsServer(s));
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"server\":").Append(J(r.Server))
                      .Append(",\"ok\":").Append(r.Ok ? "true" : "false")
                      .Append(",\"ms\":").Append(r.Ms < 0 ? "null" : r.Ms.ToString("F1"))
                      .Append(",\"note\":").Append(J(r.Note))
                      .Append('}');
                }
                sb.Append(']');
            }

            // 系统代理 + 本机代理探测
            var (en, srv, pac) = NetworkDiag.SystemProxy();
            sb.Append(",\"proxy\":{\"enabled\":").Append(en ? "true" : "false")
              .Append(",\"server\":").Append(J(srv))
              .Append(",\"pac\":").Append(J(pac))
              .Append('}');
            var probe = Await(NetworkDiag.ProbeLocalProxy());
            sb.Append(",\"proxyProbe\":{\"bad\":").Append(probe.bad ? "true" : "false")
              .Append(",\"detail\":").Append(J(probe.detail)).Append('}');

            // 服务等杂项
            var misc = Await(NetworkDiag.MiscAsync(gw));
            sb.Append(",\"system\":[");
            first = true;
            foreach (var c in misc)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(c.Name))
                  .Append(",\"level\":").Append(J(c.Level.ToString()))
                  .Append(",\"detail\":").Append(J(c.Detail))
                  .Append(",\"hint\":").Append(J(c.Hint))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        });
    }

    // ===============================================================
    // 3. 断网修复
    // ===============================================================
    public static string FixNetwork(int quick)
    {
        return Guard("fix", () =>
        {
            Log.Info(quick != 0 ? "原生调用：一键断网修复" : "原生调用：轻量修复（仅刷新 DNS + 清代理）");
            Await(Backup.Snapshot(quick != 0 ? "原生DLL：一键断网修复" : "原生DLL：轻量修复"));
            if (quick != 0) Await(NetworkRepair.QuickFix());
            else
            {
                Await(NetworkRepair.FlushDnsOnly());
                Await(NetworkRepair.ClearWinHttpProxy());
                Await(NetworkRepair.ClearProxy());
            }
            return Ok("\"mode\":" + J(quick != 0 ? "quick" : "lite"));
        });
    }

    /// <summary>
    /// 深度修复：重置 Winsock / TCP-IP 协议栈，需要重启才完全生效。
    /// 不会自动重启电脑。
    ///
    /// ⚠ 刻意**不做** hosts 还原：
    ///   还原 hosts 会清掉用户自己加的所有条目（屏蔽广告、内网域名映射等），
    ///   这是不可逆的信息丢失。图形界面里它是可勾选项、且执行前有确认框；
    ///   而 DLL 调用没有确认环节，所以这里不包含它。
    ///   需要还原 hosts 请用图形界面，或自行处理。
    /// </summary>
    public static string FixDeep()
    {
        return Guard("fixdeep", () =>
        {
            Log.Warn("原生调用：深度修复（重置 Winsock / TCP-IP，需重启才完全生效）");
            Log.Info("说明：深度修复不含 hosts 还原（避免清掉用户自定义条目）");
            Await(Backup.Snapshot("原生DLL：深度修复"));
            Await(NetworkRepair.ResetWinsock());
            Await(NetworkRepair.ResetIpStackFull());
            Await(NetworkRepair.ClearArp());
            Await(NetworkRepair.ClearProxy());
            Await(NetworkRepair.FixServices());
            Await(NetworkRepair.RenewDhcp());
            return Ok("\"needReboot\":true,\"hostsTouched\":false");
        });
    }

    /// <summary>单独还原 hosts（危险，需宿主自行确认后调用）</summary>
    public static string RestoreHosts()
    {
        return Guard("restorehosts", () =>
        {
            Log.Warn("原生调用：还原 hosts 文件");
            Await(Backup.Snapshot("原生DLL：还原 hosts"));
            bool ok = Await(NetworkRepair.FixHosts());
            return ok ? Ok("\"restored\":true") : Err("failed", "还原 hosts 失败，详见 log");
        });
    }

    /// <summary>
    /// 指定数据目录（释放脚本、日志、快照的落点）。
    /// </summary>
    /// <param name="dir">目标目录；null 表示恢复自动判定</param>
    /// <param name="restoreDefault">true 表示调用方传的是 NULL，即恢复默认</param>
    public static string SetDataDir(string dir, bool restoreDefault = false)
    {
        return Guard("setdatadir", () =>
        {
            if (restoreDefault)
            {
                EmbeddedScripts.SetDataDir(null);
                string auto = EmbeddedScripts.Ensure();
                Log.Info("数据目录已恢复为自动判定：" + auto);
                return Ok("\"dir\":" + J(auto) + ",\"auto\":true");
            }

            if (dir == null || dir.Trim().Length == 0)
                return Err("empty", "数据目录不能为空字符串；如需恢复自动判定请传 NULL");

            try
            {
                Directory.CreateDirectory(dir);
                EmbeddedScripts.SetDataDir(dir);
                string actual = EmbeddedScripts.Ensure();

                // 真正验证一下能不能写，避免"创建成功但写不进去"
                string probe = Path.Combine(actual, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                Log.Info("数据目录已切换为：" + actual);
                return Ok("\"dir\":" + J(actual) + ",\"auto\":false");
            }
            catch (Exception ex)
            {
                // 切换失败就回滚，避免后续全部功能因为坏目录而不可用
                try { EmbeddedScripts.SetDataDir(null); } catch { }
                return Err("io", "无法使用该目录：" + ex.Message);
            }
        });
    }

    // ===============================================================
    // 4. DNS 择优
    // ===============================================================
    public static string DnsBenchmark(int applyBest)
    {
        return Guard("dnsbench", () =>
        {
            Log.Info("原生调用：DNS 测速" + (applyBest != 0 ? " 并应用最快" : ""));
            var list = Await(NetworkOptimizer.BenchmarkAll());
            var sb = new StringBuilder("{\"ok\":true,\"results\":[");
            bool first = true;
            foreach (var c in list)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(c.Name))
                  .Append(",\"ip\":").Append(J(c.Ip))
                  .Append(",\"ok\":").Append(c.Ok ? "true" : "false")
                  .Append(",\"ms\":").Append(c.Ms < 0 ? "null" : c.Ms.ToString("F1"))
                  .Append(",\"note\":").Append(J(c.Note))
                  .Append('}');
            }
            sb.Append(']');

            var best = list.Where(x => x.Ok).OrderBy(x => x.Ms).FirstOrDefault();
            if (best != null)
                sb.Append(",\"best\":{\"name\":").Append(J(best.Name))
                  .Append(",\"ip\":").Append(J(best.Ip))
                  .Append(",\"ms\":").Append(best.Ms.ToString("F1")).Append('}');

            if (applyBest != 0)
            {
                if (best == null)
                    sb.Append(",\"applied\":false,\"applyError\":").Append(J("所有 DNS 均无响应，未应用"));
                else
                {
                    Await(Backup.Snapshot("原生DLL：应用最快 DNS"));
                    Await(NetworkOptimizer.ApplyDns(new[] { best.Ip }));
                    sb.Append(",\"applied\":true");
                }
            }
            sb.Append('}');
            return sb.ToString();
        });
    }

    public static string DnsRestore()
    {
        return Guard("dnsrestore", () =>
        {
            Log.Info("原生调用：恢复自动获取 DNS");
            Await(NetworkOptimizer.RestoreAutoDns());
            return Ok();
        });
    }

    // ===============================================================
    // 5. 垃圾清理
    // ===============================================================
    public static string CleanScan()
    {
        return Guard("cleanscan", () =>
        {
            Log.Info("原生调用：扫描清理目标占用");
            var items = JunkCleaner.Targets();
            var sb = new StringBuilder("{\"ok\":true,\"items\":[");
            long total = 0;
            bool first = true;
            foreach (var it in items)
            {
                long sz = it.IsCommandOnly ? -1 : JunkCleaner.MeasureSize(it);
                if (sz > 0) total += sz;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(it.Name))
                  .Append(",\"group\":").Append(J(it.Group))
                  .Append(",\"bytes\":").Append(sz < 0 ? "null" : sz.ToString())
                  .Append(",\"size\":").Append(J(it.IsCommandOnly ? "命令项" : JunkCleaner.Fmt(sz)))
                  .Append(",\"commandOnly\":").Append(it.IsCommandOnly ? "true" : "false")
                  .Append(",\"defaultOn\":").Append(it.DefaultOn ? "true" : "false")
                  .Append(",\"highRisk\":").Append(it.HighRisk ? "true" : "false")
                  .Append(",\"note\":").Append(J(it.Note))
                  .Append('}');
            }
            sb.Append("],\"totalBytes\":").Append(total)
              .Append(",\"total\":").Append(J(JunkCleaner.Fmt(total)))
              .Append('}');
            return sb.ToString();
        });
    }

    /// <summary>按名称清理。names 为 JSON 字符串数组；为空则清理全部「默认勾选且非高危」项</summary>
    /// <summary>
    /// names 为 JSON 字符串数组；mode=apply 应用，mode=restore 还原。
    /// namesUnspecified 为 true 表示宿主传的是 NULL 或空串（即「未指定」），
    /// 与「传了内容但解析不出名称」必须区分开 —— 后者应报错而不是静默当空。
    /// </summary>
    public static string CleanRun(string namesJson, int includeRecycle, bool namesUnspecified = true)
    {
        return Guard("cleanrun", () =>
        {
            var all = JunkCleaner.Targets();
            List<CleanItem> picked;

            var wanted = ParseStringArray(namesJson);
            if (namesUnspecified)
            {
                // 宿主明确表示「没指定」：按文档用默认项
                picked = all.Where(i => i.DefaultOn && !i.HighRisk).ToList();
            }
            else if (wanted.Count == 0)
            {
                // 宿主提供了内容却解析不出任何名称（畸形 JSON / 非数组 / 空数组）：
                // 以前这里会掉进「默认清理」分支，等于用户没要求却真的删了文件。
                // 清理是破坏性操作，宁可什么都不做也不能猜用户意图。
                return "{\"ok\":false,\"code\":\"empty\",\"error\":" +
                       J("未指定清理项或 JSON 无法解析（名称要与 ND_CleanScan 返回的 name 完全一致）") + "}";
            }
            else
            {
                picked = all.Where(i => wanted.Contains(i.Name)).ToList();
            }

            if (includeRecycle != 0 && !picked.Any(p => p.Name.Contains("回收站")))
                picked.AddRange(all.Where(i => i.Name.Contains("回收站")));

            if (picked.Count == 0)
                return "{\"ok\":false,\"code\":\"nothing_to_clean\",\"error\":" + J("没有匹配到任何清理项（名称要与扫描结果一致）") + "}";

            Log.Info($"原生调用：清理 {picked.Count} 项");
            long before = 0;
            foreach (var it in picked) { long s = it.IsCommandOnly ? 0 : JunkCleaner.MeasureSize(it); if (s > 0) before += s; }

            var (freed, failed, notes) = Await(JunkCleaner.CleanAsync(picked));

            var sb = new StringBuilder("{\"ok\":true,\"count\":").Append(picked.Count)
              .Append(",\"beforeBytes\":").Append(before)
              .Append(",\"freedBytes\":").Append(freed)
              .Append(",\"freed\":").Append(J(JunkCleaner.Fmt(freed)))
              .Append(",\"failed\":").Append(failed)
              .Append(",\"names\":[");
            for (int i = 0; i < picked.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(J(picked[i].Name));
            }
            sb.Append("],\"notes\":[");
            for (int i = 0; i < notes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(J(notes[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        });
    }

    // ===============================================================
    // 6. 硬件信息
    // ===============================================================
    public static string Hardware(int compact)
    {
        return Guard("hardware", () =>
        {
            Log.Info("原生调用：硬件检测");
            var hw = Await(HardwareInfo.GatherAsync(force: true));
            string text = HardwareInfo.BuildReport(hw);

            var sb = new StringBuilder("{\"ok\":true");
            sb.Append(",\"summary\":").Append(J(HardwareInfo.SummaryLine(hw)));
            sb.Append(",\"report\":").Append(J(text));

            // 顺手给几个结构化字段，方便易语言直接取值
            sb.Append(",\"cpu\":");
            if (hw.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
                sb.Append("{\"name\":").Append(J(HardwareInfo.Str(c[0], "Name").Trim()))
                  .Append(",\"cores\":").Append(HardwareInfo.Num(c[0], "Cores"))
                  .Append(",\"threads\":").Append(HardwareInfo.Num(c[0], "Threads"))
                  .Append(",\"maxMhz\":").Append(HardwareInfo.Num(c[0], "MaxClockMHz")).Append('}');
            else sb.Append("null");

            sb.Append(",\"memory\":");
            if (hw.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                long tot = 0; foreach (var x in m.EnumerateArray()) tot += HardwareInfo.Num(x, "CapacityBytes");
                sb.Append("{\"modules\":").Append(m.GetArrayLength())
                  .Append(",\"totalBytes\":").Append(tot)
                  .Append(",\"total\":").Append(J(JunkCleaner.Fmt(tot)))
                  .Append(",\"type\":").Append(J(HardwareInfo.Str(m[0], "Type")))
                  .Append(",\"speedMhz\":").Append(HardwareInfo.Num(m[0], "ConfiguredMHz"))
                  .Append(",\"dualChannel\":").Append(m.GetArrayLength() > 1 ? "true" : "false")
                  .Append('}');
            }
            else sb.Append("null");

            sb.Append(",\"gpu\":");
            if (hw.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0)
            {
                long vram = HardwareInfo.GpuVramFromRegistry(HardwareInfo.Str(g[0], "PNPDeviceID"));
                if (vram <= 0) vram = HardwareInfo.Num(g[0], "AdapterRam");
                sb.Append("{\"name\":").Append(J(HardwareInfo.Str(g[0], "Name")))
                  .Append(",\"vramBytes\":").Append(vram)
                  .Append(",\"vram\":").Append(J(JunkCleaner.Fmt(vram)))
                  .Append(",\"driver\":").Append(J(HardwareInfo.Str(g[0], "DriverVersion")))
                  .Append('}');
            }
            else sb.Append("null");

            sb.Append(",\"disks\":[");
            if (hw.TryGetProperty("disk", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                bool first = true;
                foreach (var x in d.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"model\":").Append(J(HardwareInfo.Str(x, "Model")))
                      .Append(",\"sizeBytes\":").Append(HardwareInfo.Num(x, "SizeBytes"))
                      .Append(",\"size\":").Append(J(JunkCleaner.Fmt(HardwareInfo.Num(x, "SizeBytes"))))
                      .Append(",\"iface\":").Append(J(HardwareInfo.Str(x, "Interface")))
                      .Append(",\"health\":").Append(J(HardwareInfo.Str(x, "Health")))
                      .Append(",\"tempC\":").Append(HardwareInfo.Num(x, "Temperature"))
                      .Append(",\"powerOnHours\":").Append(HardwareInfo.Num(x, "PowerOnHours"))
                      .Append('}');
                }
            }
            sb.Append(']');

            sb.Append(",\"system\":");
            if (hw.TryGetProperty("system", out var s))
                sb.Append("{\"os\":").Append(J(HardwareInfo.Str(s, "Caption") + " " + HardwareInfo.Str(s, "Version")))
                  .Append(",\"board\":").Append(J(HardwareInfo.Str(s, "BoardVendor") + " " + HardwareInfo.Str(s, "BoardProduct")))
                  .Append(",\"bios\":").Append(J(HardwareInfo.Str(s, "BiosVendor") + " " + HardwareInfo.Str(s, "BiosVersion")))
                  .Append(",\"model\":").Append(J(HardwareInfo.Str(s, "Model")))
                  .Append('}');
            else sb.Append("null");

            sb.Append(",\"monitor\":");
            if (hw.TryGetProperty("monitor", out var mo) && mo.ValueKind == JsonValueKind.Array && mo.GetArrayLength() > 0)
                sb.Append("{\"name\":").Append(J(HardwareInfo.Str(mo[0], "FriendlyName")))
                  .Append(",\"vendor\":").Append(J(HardwareInfo.Str(mo[0], "Manufacturer")))
                  .Append(",\"panel\":").Append(J(HardwareInfo.Str(mo[0], "ProductCode")))
                  .Append(",\"year\":").Append(J(HardwareInfo.Str(mo[0], "YearOfMfg")))
                  .Append('}');
            else sb.Append("null");

            sb.Append('}');
            return sb.ToString();
        });
    }

    // ===============================================================
    // 7. Windows 优化
    // ===============================================================
    public static string OptList()
    {
        return Guard("optlist", () =>
        {
            Log.Info("原生调用：枚举 Windows 优化项状态");
            var all = WindowsOptimizer.Load();
            var sb = new StringBuilder("{\"ok\":true,\"total\":").Append(all.Count);

            sb.Append(",\"categories\":[");
            bool first = true;
            foreach (var cat in WindowsOptimizer.CategoryOrder)
            {
                var items = all.Where(i => i.Category == cat).ToList();
                if (items.Count == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(J(cat))
                  .Append(",\"title\":").Append(J(WindowsOptimizer.CategoryTitle(cat)))
                  .Append(",\"hint\":").Append(J(WindowsOptimizer.CategoryHint(cat)))
                  .Append(",\"count\":").Append(items.Count)
                  .Append('}');
            }
            sb.Append(']');

            sb.Append(",\"items\":[");
            first = true;
            int applied = 0;
            foreach (var it in all)
            {
                var st = WindowsOptimizer.Check(it);
                if (st.Applied) applied++;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(it.Name))
                  .Append(",\"category\":").Append(J(it.Category))
                  .Append(",\"applied\":").Append(st.Applied ? "true" : "false")
                  .Append(",\"unknown\":").Append(st.Unknown ? "true" : "false")
                  .Append(",\"current\":").Append(J(st.Current))
                  .Append(",\"target\":").Append(J(it.OptimizedValue))
                  .Append(",\"highRisk\":").Append(it.HighRisk ? "true" : "false")
                  .Append('}');
            }
            sb.Append("],\"applied\":").Append(applied)
              .Append(",\"notApplied\":").Append(all.Count - applied)
              .Append('}');
            return sb.ToString();
        });
    }

    /// <summary>names 为 JSON 字符串数组；mode=apply 应用，mode=restore 还原</summary>
    public static string OptApply(string namesJson, int restore)
    {
        return Guard(restore != 0 ? "optrestore" : "optapply", () =>
        {
            var all = WindowsOptimizer.Load();
            var wanted = ParseStringArray(namesJson);
            if (wanted.Count == 0)
                return "{\"ok\":false,\"code\":\"empty\",\"error\":" + J("未指定优化项名称") + "}";

            var picked = all.Where(i => wanted.Contains(i.Name)).ToList();
            if (picked.Count == 0)
                return "{\"ok\":false,\"code\":\"not_found\",\"error\":" +
                       J("没有匹配到任何优化项（名称要与 ND_OptList 返回的 name 完全一致）") + "}";

            Log.Info($"原生调用：{(restore != 0 ? "还原" : "应用")} {picked.Count} 项优化");

            // 原值快照
            try
            {
                var txt = WindowsOptimizer.SnapshotValues(picked, restore != 0 ? "原生DLL：还原" : "原生DLL：应用优化");
                File.AppendAllText(Path.Combine(Backup.Root, "backup.ini"), txt, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Warn("原值快照写入失败：" + ex.Message); }

            int ok = 0, bad = 0;
            var failedNames = new List<string>();
            foreach (var it in picked)
            {
                Log.Step($"[{(restore != 0 ? "还原" : "优化")}] {it.Name}");
                bool r;
                try
                {
                    r = restore != 0
                        ? WindowsOptimizer.Restore(it, m => Log.Info(m))
                        : WindowsOptimizer.Apply(it, m => Log.Info(m));
                }
                catch (Exception ex) { Log.Err("  " + ex.Message); r = false; }

                if (r) { ok++; Log.Ok($"  {it.Name} → 完成"); }
                else { bad++; failedNames.Add(it.Name); Log.Warn($"  {it.Name} → 部分失败"); }
            }

            WindowsOptimizer.BroadcastSettingChange();

            var sb = new StringBuilder("{\"ok\":true,\"mode\":").Append(J(restore != 0 ? "restore" : "apply"))
              .Append(",\"succeeded\":").Append(ok)
              .Append(",\"failed\":").Append(bad)
              .Append(",\"failedNames\":[");
            for (int i = 0; i < failedNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(J(failedNames[i]));
            }
            sb.Append("],\"needExplorerRestart\":true}");
            return sb.ToString();
        });
    }

    // ===============================================================
    // 8. 服务 / 启动项
    // ===============================================================
    public static string Services(string filter)
    {
        return Guard("services", () =>
        {
            var list = SystemItems.Services(includeMicrosoft: false);
            if (!string.IsNullOrWhiteSpace(filter))
                list = list.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                    || s.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var sb = new StringBuilder("{\"ok\":true,\"count\":").Append(list.Count).Append(",\"items\":[");
            bool first = true;
            foreach (var s in list)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(s.Name))
                  .Append(",\"display\":").Append(J(s.Display))
                  .Append(",\"running\":").Append(s.Running ? "true" : "false")
                  .Append(",\"status\":").Append(J(s.Status))
                  .Append(",\"startType\":").Append(J(s.StartType))
                  .Append(",\"startNum\":").Append(J(s.StartNum))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        });
    }

    public static string Startup()
    {
        return Guard("startup", () =>
        {
            var list = SystemItems.Startup();
            SystemItems.ApplyApprovedState(list);
            var sb = new StringBuilder("{\"ok\":true,\"count\":").Append(list.Count).Append(",\"items\":[");
            bool first = true;
            foreach (var s in list)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(J(s.Name))
                  .Append(",\"source\":").Append(J(s.Source))
                  .Append(",\"command\":").Append(J(s.Command))
                  .Append(",\"enabled\":").Append(s.Enabled ? "true" : "false")
                  .Append(",\"kind\":").Append(J(s.Kind))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        });
    }

    // ===============================================================
    private static List<string> ParseStringArray(string json)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return res;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var s = e.GetString();
                    if (!string.IsNullOrEmpty(s)) res.Add(s);
                }
            else if (doc.RootElement.ValueKind == JsonValueKind.String)
                res.Add(doc.RootElement.GetString());
        }
        catch { }
        return res;
    }
}

internal static class AdminHelper
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
