using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using NetDoctor.Core;

namespace NetDoctorTest;

internal static class TestMain
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sb = new StringBuilder();
        int fail = 0;

        void Head(string t) { Console.WriteLine(); Console.WriteLine("=== " + t + " ==="); sb.AppendLine("=== " + t + " ==="); }
        void Line(string t) { Console.WriteLine(t); sb.AppendLine(t); }
        void Assert(bool cond, string name)
        {
            Line((cond ? "  [PASS] " : "  [FAIL] ") + name);
            if (!cond) fail++;
        }

        Head("1. 网卡枚举");
        var ads = NetworkDiag.Adapters();
        foreach (var a in ads)
            Line($"  {(a.Up ? "UP  " : "DOWN")} {a.Name,-28} {a.Type,-8} IP={a.IPv4,-16} GW={a.Gateway,-16} DNS={a.Dns} {a.Dhcp} {a.Speed}");
        Assert(ads.Count > 0, "至少读到一块网卡");

        Head("2. 分层连通性");
        var sw = Stopwatch.StartNew();
        var (checks, gw) = await NetworkDiag.Connectivity();
        sw.Stop();
        Line($"  网关={gw}   耗时={sw.ElapsedMilliseconds}ms");
        foreach (var c in checks) Line($"  [{c.Level,-4}] {c.Name,-26} {c.Detail}");
        Assert(checks.Count == 5, "连通性返回 5 项");

        Head("3. DNS 服务器实测");
        var servers = NetworkDiag.DnsServers();
        Line("  本机 DNS：" + (servers.Count == 0 ? "(无)" : string.Join(", ", servers)));
        foreach (var s in servers)
        {
            var r = await NetworkDiag.TestDnsServer(s);
            Line($"  {s,-18} {(r.Ok ? r.Ms.ToString("F0") + " ms" : "无响应")} {r.Note}");
        }

        Head("4. 公共 DNS 并发测速");
        sw.Restart();
        var bench = await NetworkOptimizer.BenchmarkAll();
        sw.Stop();
        Line($"  共 {bench.Count} 个，耗时 {sw.ElapsedMilliseconds} ms");
        foreach (var b in bench.Take(6))
            Line($"  {b.Name,-16} {b.Ip,-16} {(b.Ok ? b.Ms.ToString("F0") + " ms" : "无响应")}");
        Assert(bench.Count == 12, "12 个候选 DNS 全部有结果");

        Head("5. 系统代理读取");
        var (en, srv, pac) = NetworkDiag.SystemProxy();
        Line($"  启用={en}  服务器='{srv}'  PAC='{pac}'");
        var wh = await NetworkDiag.WinHttpProxyAsync();
        Line("  WinHTTP: " + wh);
        var probe = await NetworkDiag.ProbeLocalProxy();
        Line($"  本机代理探测: 有问题={probe.bad}  {probe.detail}");
        Assert(true, "代理读取未抛异常");

        Head("6. 系统检查项");
        foreach (var c in await NetworkDiag.MiscAsync(gw))
            Line($"  [{c.Level,-4}] {c.Name,-22} {c.Detail}");

        Head("7. 网卡节能扫描");
        foreach (var c in await NetworkOptimizer.ScanNicPowerAsync())
            Line($"  [{c.Level,-4}] {c.Name,-40} {c.Detail}");

        Head("8. TCP 参数读取");
        var tcp = await NetworkOptimizer.TcpGlobalTextAsync();
        Line("  " + tcp.Replace("\n", "\n  "));
        Assert(tcp.Contains("全局参数") || tcp.Contains("Global Parameters"), "读到 TCP 全局参数");

        Head("9. 网络占用进程统计");
        Line("  " + (await NetworkOptimizer.NetTopProcesses()).Replace("\n", "\n  "));

        Head("10. 状态快照（只读，不修改任何设置）");
        await Backup.Snapshot("自测（只读验证）");
        var bf = Path.Combine(Backup.Root, "backup.ini");
        Assert(File.Exists(bf), "backup.ini 已生成 → " + bf);
        if (File.Exists(bf)) Line($"  快照大小 {new FileInfo(bf).Length} 字节");

        Head("11. 日志写入");
        NetDoctor.Log.Info("自测日志写入验证");
        Assert(File.Exists(NetDoctor.Log.FilePath), "日志文件：" + NetDoctor.Log.FilePath);

        Head("12. Ping 与超时处理");
        var t1 = await NetworkDiag.PingAsync(gw, 1200);
        Line($"  ping {gw} → {(t1.ok ? t1.ms + " ms" : "失败")}");
        var t2 = await NetworkDiag.TestDnsServer("192.0.2.1", 800);   // 保留测试网段，必定超时
        Assert(!t2.Ok, "无效 DNS 正确判定为无响应（未卡死）");

        Head("13. Windows 优化配置（内置 XML）");
        var opts = WindowsOptimizer.Load();
        Line($"  载入条目：{opts.Count}");
        Assert(opts.Count >= 150, $"内置配置载入完整（{opts.Count} 条）");

        var byCat = opts.GroupBy(o => o.Category)
                        .ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in WindowsOptimizer.CategoryOrder)
            if (byCat.TryGetValue(c, out int n))
                Line($"  {WindowsOptimizer.CategoryTitle(c),-18} {n,3} 项");

        int noApply = opts.Count(o => o.Apply.Count == 0);
        int noRestore = opts.Count(o => o.Restore.Count == 0);
        Line($"  缺少优化方案的条目：{noApply}");
        Line($"  缺少还原方案的条目：{noRestore}");
        Assert(noApply == 0, "每条优化项都有优化方案");
        Assert(noRestore == 0, "每条优化项都有还原方案");

        Head("14. 状态判定（只读注册表，不做任何修改）");
        int applied = 0, notApplied = 0, unknown = 0, errs = 0;
        var samples = new List<string>();
        foreach (var it in opts)
        {
            try
            {
                var st = WindowsOptimizer.Check(it);
                if (st.Unknown) unknown++;
                else if (st.Applied) { applied++; if (samples.Count < 8) samples.Add($"已优化  {it.Name}  = {st.Current}"); }
                else { notApplied++; if (samples.Count < 16) samples.Add($"未优化  {it.Name}  (当前 {st.Current} / 目标 {it.OptimizedValue})"); }
            }
            catch (Exception ex)
            {
                errs++;
                if (errs <= 3) Line($"  [异常] {it.Name} → {ex.Message}");
            }
        }
        Line($"  已优化 {applied} · 未优化 {notApplied} · 无法判定 {unknown} · 读取异常 {errs}");
        foreach (var s in samples) Line("    " + s);
        Assert(errs == 0, "全部条目状态检测无异常");

        Head("15. 值比较逻辑");
        Assert(WindowsOptimizer.ValuesEqual("0", "0"), "0 == 0");
        Assert(WindowsOptimizer.ValuesEqual("1", "0x1"), "1 == 0x1");
        Assert(WindowsOptimizer.ValuesEqual("4294967295", "4294967295"), "大数相等");
        Assert(!WindowsOptimizer.ValuesEqual("0", "1"), "0 != 1");
        Assert(WindowsOptimizer.ValuesEqual("22,22,22,00", "22222200"), "二进制分隔符容错");
        // 注册表 0xFFFFFFFF 读成 int 是 -1，必须归一化成 4294967295，否则「已优化」会被误判
        Assert(WindowsOptimizer.ToText(-1) == "4294967295", $"DWORD -1 → 4294967295（实际 {WindowsOptimizer.ToText(-1)}）");
        Assert(WindowsOptimizer.ToText(unchecked((int)0x80000000u)) == "2147483648", "DWORD 0x80000000 → 2147483648");
        Assert(WindowsOptimizer.ValuesEqual(WindowsOptimizer.ToText(-1), "4294967295"), "-1 与 4294967295 判定相等");

        Head("16. 快照生成");
        var snap = WindowsOptimizer.SnapshotValues(opts.Take(5), "自测");
        Assert(snap.Length > 200 && snap.Contains("优化前原值"), "快照文本生成正常");
        Line($"  样本长度 {snap.Length} 字符");

        Head("17. 垃圾清理目标表");
        var junk = JunkCleaner.Targets();
        Line($"  清理项 {junk.Count} 个，分组：" +
             string.Join(" / ", junk.Select(j => j.Group).Distinct()));
        Assert(junk.Count >= 20, $"清理项数量正常（{junk.Count}）");
        Assert(junk.All(j => !string.IsNullOrEmpty(j.Name)), "每项都有名称");
        var cmdOnly = junk.Count(j => j.IsCommandOnly);
        Line($"  其中命令类 {cmdOnly} 个（dism / ipconfig 等）");
        // 抽样测量（只读，不删除）
        long sample = 0;
        int measured = 0;
        foreach (var it in junk.Where(j => !j.IsCommandOnly).Take(6))
        {
            long sz = JunkCleaner.MeasureSize(it);
            if (sz >= 0) { measured++; sample += sz; }
            Line($"    {it.Name,-30} {(sz < 0 ? "(不可访问)" : JunkCleaner.Fmt(sz))}");
        }
        Assert(measured > 0, "占用测量可用");

        Head("18. 服务枚举");
        var svcs = SystemItems.Services(true);
        Line($"  服务总数 {svcs.Count}，运行中 {svcs.Count(s => s.Running)}");
        Assert(svcs.Count > 50, $"能枚举到服务（{svcs.Count}）");
        var running = svcs.FirstOrDefault(s => s.Running);
        Assert(running != null, "至少有一个运行中的服务");
        if (running != null)
            Line($"    样本：{running.Name} ({running.Display}) {running.Status} / {running.StartType}");
        Assert(svcs.All(s => s.StartNum != "" || s.StartType == "?"), "能读到启动类型");

        Head("19. 启动项枚举");
        var startup = SystemItems.Startup();
        SystemItems.ApplyApprovedState(startup);
        Line($"  注册表 Run + 启动文件夹 共 {startup.Count} 项");
        foreach (var s in startup.Take(8))
            Line($"    [{s.Source}] {s.Name} → {(s.Command.Length > 60 ? s.Command.Substring(0, 60) + "…" : s.Command)}  ({s.State})");
        Assert(true, "启动项枚举未抛异常");

        Head("20. Appx 应用枚举（PowerShell JSON）");
        var appxSw = Stopwatch.StartNew();
        var apps = await SystemItems.AppxAsync();
        appxSw.Stop();
        Line($"  应用 {apps.Count} 个，耗时 {appxSw.ElapsedMilliseconds} ms");
        foreach (var a in apps.Take(6))
            Line($"    {a.Name,-42} {a.SizeText,10}  v{a.Version}");
        Assert(apps.Count > 0, $"能枚举到应用（{apps.Count} 个）");
        Assert(apps.Any(a => a.SizeBytes > 0), "能读到应用占用大小");
        Line($"  合计占用 {JunkCleaner.Fmt(apps.Sum(a => a.SizeBytes))}");

        Head("21. 计划任务枚举（PowerShell JSON）");
        var tasks = await SystemItems.TasksAsync();
        Line($"  计划任务 {tasks.Count} 条");
        foreach (var t in tasks.Where(x => x.CanToggle).Take(5))
            Line($"    {t.Name}  [{t.State}]");
        Assert(tasks.Count > 0, $"能枚举到计划任务（{tasks.Count}）");

        Head("22. 硬件检测（WMI/CIM + 注册表）");
        var hwSw = Stopwatch.StartNew();
        var hw = await HardwareInfo.GatherAsync(force: true);
        hwSw.Stop();
        Assert(hw.ValueKind == JsonValueKind.Object, $"硬件信息获取成功（{hwSw.ElapsedMilliseconds} ms）");
        Line("  摘要：" + HardwareInfo.SummaryLine(hw));

        if (hw.TryGetProperty("cpu", out var cpuArr) && cpuArr.ValueKind == JsonValueKind.Array
            && cpuArr.GetArrayLength() > 0)
        {
            var c0 = cpuArr[0];
            Line($"  CPU：{HardwareInfo.Str(c0, "Name").Trim()}  " +
                 $"{HardwareInfo.Num(c0, "Cores")}核{HardwareInfo.Num(c0, "Threads")}线程  " +
                 $"{HardwareInfo.Num(c0, "MaxClockMHz")}MHz  L3={HardwareInfo.Num(c0, "L3KB") / 1024.0:0.#}MB");
            Assert(HardwareInfo.Num(c0, "Cores") > 0, "CPU 核心数读取正常");
        }
        else Assert(false, "CPU 信息缺失");

        if (hw.TryGetProperty("memory", out var memArr) && memArr.ValueKind == JsonValueKind.Array)
        {
            int n = memArr.GetArrayLength();
            long tot = 0;
            foreach (var m in memArr.EnumerateArray()) tot += HardwareInfo.Num(m, "CapacityBytes");
            Line($"  内存：{n} 条，合计 {JunkCleaner.Fmt(tot)}，" +
                 $"类型 {HardwareInfo.Str(memArr[0], "Type")}，{HardwareInfo.Num(memArr[0], "ConfiguredMHz")} MHz");
            Assert(tot > 0, "内存容量读取正常");
        }
        else Assert(false, "内存信息缺失");

        if (hw.TryGetProperty("gpu", out var gpuArr) && gpuArr.ValueKind == JsonValueKind.Array
            && gpuArr.GetArrayLength() > 0)
        {
            foreach (var g in gpuArr.EnumerateArray())
            {
                long vram = HardwareInfo.GpuVramFromRegistry(HardwareInfo.Str(g, "PNPDeviceID"));
                Line($"  显卡：{HardwareInfo.Str(g, "Name")}  显存 {(vram > 0 ? JunkCleaner.Fmt(vram) : "读取失败")}  " +
                     $"驱动 {HardwareInfo.Str(g, "DriverVersion")}");
                Assert(vram > 0, $"显存读取成功（{JunkCleaner.Fmt(vram)}）");
            }
        }

        if (hw.TryGetProperty("disk", out var dskArr) && dskArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in dskArr.EnumerateArray())
                Line($"  磁盘：{HardwareInfo.Str(d, "Model")}  {JunkCleaner.Fmt(HardwareInfo.Num(d, "SizeBytes"))}  " +
                     $"{HardwareInfo.Str(d, "Interface")}  健康={HardwareInfo.Str(d, "Health")}  " +
                     $"温度={HardwareInfo.Num(d, "Temperature")}°C");
            Assert(dskArr.GetArrayLength() > 0, "磁盘信息读取正常");
        }

        if (hw.TryGetProperty("monitor", out var monArr) && monArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in monArr.EnumerateArray())
                Line($"  显示器：{HardwareInfo.Str(m, "FriendlyName")}  {HardwareInfo.Str(m, "Manufacturer")} " +
                     $"{HardwareInfo.Str(m, "ProductCode")}  {HardwareInfo.Str(m, "YearOfMfg")}年");
            Assert(monArr.GetArrayLength() > 0, "显示器信息读取正常");
        }

        var rep = HardwareInfo.BuildReport(hw);
        Assert(rep.Length > 800 && rep.Contains("处理器") && rep.Contains("内存"), "报告文本生成正常");
        Line($"  报告长度 {rep.Length} 字符");

        Head("23. 工具启动器（图吧工具箱 tools 目录）");
        var root = ToolLauncher.FindRoot();
        Line("  tools 目录：" + (root ?? "未找到"));
        var tools = ToolLauncher.Scan(force: true);
        Line($"  扫描到工具 {tools.Count} 个");
        foreach (var g in tools.GroupBy(t => t.Category))
            Line($"    {g.Key,-12} {g.Count(),3} 个");
        Assert(root != null && tools.Count > 30, $"能扫描图吧工具集（{tools.Count} 个）");
        var withExe = tools.Count(t => !string.IsNullOrEmpty(t.Exe));
        Line($"  其中已附带可执行文件 {withExe} 个，未附带 {tools.Count - withExe} 个");
        foreach (var t in tools.Where(x => !string.IsNullOrEmpty(x.Exe)).Take(5))
            Line($"    样例：[{t.Category}] {t.Name} → {Path.GetFileName(t.Exe)}");

        Head("24. 性能测试引擎");
        var cpu = await Benchmarks.CpuAsync();
        Line($"  CPU 单线程 {cpu.SingleMBps:0.0} MB/s，多线程 {cpu.MultiMBps:0.0} MB/s，" +
             $"加速比 {cpu.MultiMBps / Math.Max(0.1, cpu.SingleMBps):0.0}×，浮点 {cpu.PrimeOps:0.0} M ops/s");
        Assert(cpu.SingleMBps > 50, "CPU 单线程跑分有结果");
        Assert(cpu.MultiMBps > cpu.SingleMBps, "多线程快于单线程");

        var tmpDir = Path.Combine(Path.GetTempPath(), "nd_bench_test");
        Directory.CreateDirectory(tmpDir);
        var disk = await Benchmarks.DiskAsync(tmpDir, 64);
        Line($"  磁盘（{tmpDir}）写入 {disk.WriteMBps:0.0} MB/s，读取 {disk.ReadMBps:0.0} MB/s  {disk.Error}");
        Assert(string.IsNullOrEmpty(disk.Error), "磁盘测速无错误");
        Assert(disk.WriteMBps > 5 && disk.ReadMBps > 5, "磁盘读写测速有结果");
        Assert(Directory.GetFiles(tmpDir, "nd_bench_*").Length == 0, "测速临时文件已清理");
        try { Directory.Delete(tmpDir, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "========== 全部自测通过 ==========" : $"========== 有 {fail} 项失败 ==========");
        sb.AppendLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), sb.ToString(), Encoding.UTF8);
        return fail == 0 ? 0 : 1;
    }
}
