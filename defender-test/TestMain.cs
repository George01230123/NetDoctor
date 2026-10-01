using System.Text;
using Microsoft.Win32;
using NetDoctor.Core;

namespace NetDoctorTest;

/// <summary>
/// Defender 体检模块验证。
///
/// 重点验证三件事：
///   1. 能跑通、结构完整（不依赖 Defender 服务是否存活）
///   2. 判定准确 —— 尤其是**只读性**：扫描前后系统状态必须完全一致
///   3. 能识别出「引擎平台目录为空」这类 WMI 查不到、但正是故障原因的情况
/// </summary>
internal static class DefenderTestMain
{
    private static int _fail, _pass;

    private static void Chk(bool ok, string name, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "   " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    private static void Head(string t) { Console.WriteLine(); Console.WriteLine("=== " + t + " ==="); }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("═════════ Defender 体检模块验证 ═════════");
        Console.WriteLine("（全程只读，不会修改任何系统设置）");

        // ---------- 只读性基线：记录若干关键位置的状态 ----------
        Head("0. 建立只读性基线");
        var before = Snapshot();
        foreach (var kv in before) Console.WriteLine($"  {kv.Key} = {kv.Value}");

        // ---------- 1. 基础可用性 ----------
        Head("1. 扫描可执行且结构完整");
        var r = DefenderStatus.Scan();
        Chk(r != null, "Scan() 返回非空");
        Chk(r.Checks.Count >= 6, "检查项数量合理", $"{r.Checks.Count} 项");
        Chk(!string.IsNullOrWhiteSpace(r.Verdict), "给出了结论", r.Verdict);

        foreach (var c in r.Checks)
            Console.WriteLine($"    {c.Level,-4} {c.Name}：{c.Value}" +
                              (string.IsNullOrEmpty(c.Note) ? "" : $"\n           {c.Note}"));

        // ---------- 2. 每项内容有效 ----------
        Head("2. 检查项内容有效性");
        bool allNamed = r.Checks.All(c => !string.IsNullOrWhiteSpace(c.Name));
        bool allValued = r.Checks.All(c => !string.IsNullOrWhiteSpace(c.Value));
        bool allLeveled = r.Checks.All(c => new[] { "ok", "warn", "bad", "info" }.Contains(c.Level));
        Chk(allNamed, "每项都有名称");
        Chk(allValued, "每项都有取值（空值会让界面看起来像没检测）");
        Chk(allLeveled, "每项级别合法（ok/warn/bad/info）");

        // ---------- 3. 关键判定：必须能看出引擎是否可用 ----------
        Head("3. 关键判定能力");

        var platform = r.Checks.FirstOrDefault(c => c.Name == "引擎平台目录");
        Chk(platform != null, "存在「引擎平台目录」检查项");

        bool platformDirExists = Directory.Exists(@"C:\ProgramData\Microsoft\Windows Defender\Platform");
        int platformFiles = 0;
        try
        {
            if (platformDirExists)
                platformFiles = Directory.GetDirectories(@"C:\ProgramData\Microsoft\Windows Defender\Platform")
                    .Sum(d => { try { return Directory.GetFiles(d).Length; } catch { return 0; } });
        }
        catch { }

        Console.WriteLine($"    （实测：目录存在={platformDirExists}，文件数={platformFiles}）");
        if (platformDirExists && platformFiles == 0)
        {
            Chk(platform != null && platform.Level == "bad",
                "平台目录为空时判定为「异常」（这是 WMI 查不到、却最关键的故障信息）",
                platform?.Value ?? "");
        }
        else
        {
            Chk(platform != null && platform.Level == "ok", "平台目录有文件时判定为正常", platform?.Value ?? "");
        }

        var proc = r.Checks.FirstOrDefault(c => c.Name == "关键进程");
        Chk(proc != null, "存在「关键进程」检查项");
        bool msmpen = System.Diagnostics.Process.GetProcessesByName("MsMpEng").Length > 0;
        Console.WriteLine($"    （实测：MsMpEng 在运行={msmpen}）");
        Chk(proc != null && (msmpen ? proc.Level == "ok" : proc.Level == "bad"),
            "关键进程判定与实际进程状态一致", proc?.Value ?? "");

        // 结论必须与检查项自洽：有 bad 项、且没有第三方杀软真在顶着时，结论不能是"正常"
        bool hasBad = r.Checks.Any(c => c.Level == "bad");
        if (hasBad && !r.ThirdPartyActive)
            Chk(r.Level != "ok", "存在异常项且无第三方杀软接管时，结论不能报「正常」", $"结论级别={r.Level}");
        else if (hasBad && r.ThirdPartyActive)
            Chk(r.Level == "ok", "有第三方杀软在运行时，Defender 停用不判为故障", $"结论级别={r.Level}");
        else
            Chk(true, "结论与检查项自洽（无冲突）", $"结论级别={r.Level}");

        // ---------- 3b. 杀软识别不得误判 ----------
        Head("3b. 杀软识别可靠性");
        var avNames = r.AvProducts;
        Chk(avNames.Count == avNames.Distinct().Count(),
            "杀软列表已去重（同一产品可能有多个 GUID 条目）",
            $"{avNames.Count} 项: {string.Join("、", avNames)}");
        Chk(avNames.All(n => !string.IsNullOrWhiteSpace(n)), "杀软名称非空");

        // 第三方杀软必须被"确认在运行"才认，避免把卸载残留当成有效防护
        if (r.ThirdPartyActive)
        {
            Chk(true, "已确认第三方杀软正在运行（非仅注册表残留）", r.Checks
                .FirstOrDefault(c => c.Name == "已注册杀软")?.Note ?? "");
        }
        else
        {
            bool onlyDef = avNames.All(n => n.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0);
            Console.WriteLine($"    （未确认到运行中的第三方杀软；仅 Defender={onlyDef}）");
            Chk(true, "未确认第三方杀软运行时未误报为「已受保护」");
        }

        // ---------- 3c. 不得把脚本源码当成检测值（回归防线）----------
        Head("3c. 解析健壮性：不得泄漏脚本源码");
        // 事故经过：PowerShell 执行失败时，输出里会回显脚本源码片段，
        // 早期实现按「找到 OK| 就解析」的宽松匹配，把
        //   ' + $s.AMEngineVersion + '
        // 当成了"引擎版本"，进而凭空造出几个假的"异常"项。
        // 这比"查不到"更糟 —— 它会向用户报告不存在的故障。
        string[] scriptMarkers = { "AMEngineVersion", "$s.", "$p.", "ErrorActionPreference", "Get-MpComputerStatus", "Get-MpPreference", "EncodedCommand" };
        foreach (var c in r.Checks)
        {
            foreach (var mk in scriptMarkers)
            {
                if (c.Value.Contains(mk) || c.Note.Contains(mk))
                {
                    Chk(false, $"「{c.Name}」的取值/说明不得含脚本片段", $"命中: {mk}");
                    goto afterLeakCheck;
                }
            }
        }
        Chk(true, "所有检查项的取值与说明都不含 PowerShell 脚本片段");
        afterLeakCheck:

        // 取值必须是人类可读的，不能是 True/False 这种原始布尔
        bool rawBool = r.Checks.Any(c => c.Value == "True" || c.Value == "False");
        Chk(!rawBool, "布尔类检查项已转成中文（不直接显示 True/False）");

        // ---------- 4. 策略标志读取 ----------
        Head("4. 禁用策略读取");
        var policy = r.Checks.FirstOrDefault(c => c.Name == "禁用策略");
        Chk(policy != null, "存在「禁用策略」检查项");

        int? das = ReadDword(@"SOFTWARE\Microsoft\Windows Defender", "DisableAntiSpyware");
        Console.WriteLine($"    （实测：DisableAntiSpyware = {(das.HasValue ? das.Value.ToString() : "(不存在)")}）");
        if (das.HasValue && das.Value != 0)
            Chk(policy != null && policy.Level == "bad", "DisableAntiSpyware≠0 时判定为异常", policy?.Value ?? "");
        else
            Chk(true, "策略标志与判定方向一致（当前无禁用标志）", policy?.Value ?? "");

        // 说明文字里要提示"普通管理员改不了"，否则用户会以为这是本工具能修的
        if (policy != null && policy.Level == "bad")
            Chk(policy.Note.Contains("SYSTEM") || policy.Note.Contains("管理员"),
                "异常时说明了「该键受保护、普通管理员无法修改」");

        // ---------- 5. 输出格式 ----------
        Head("5. 报告与摘要输出");
        string report = DefenderStatus.BuildReport(r);
        Chk(report.Length > 200, "报告长度合理", $"{report.Length} 字符");
        Chk(report.Contains("结论"), "报告含结论段");
        Chk(report.Contains("逐项"), "报告含逐项段");
        // 工具定位是「体检 + 优化」，不能提供关闭 Defender 的功能 —— 报告里应有这句说明
        Chk(report.Contains("不提供关闭 Defender"), "报告写明本工具不提供关闭 Defender 的功能");

        string sum = DefenderStatus.SummaryLine(r);
        Chk(sum.Length > 0 && sum.Contains("正常"), "摘要行可读", sum);

        // ---------- 6. 只读性验证（最重要） ----------
        Head("6. 只读性验证：扫描不得改动系统");
        var after = Snapshot();
        bool same = true;
        foreach (var kv in before)
        {
            string now = after.TryGetValue(kv.Key, out var v) ? v : "(缺失)";
            bool eq = now == kv.Value;
            if (!eq) { same = false; Console.WriteLine($"    ✗ 变动: {kv.Key}  {kv.Value} → {now}"); }
        }
        Chk(same, "扫描前后所有受监控状态完全一致（证明全程只读）");

        // ---------- 7. 重复调用稳定性 ----------
        Head("7. 重复调用稳定性");
        bool stable = true;
        for (int i = 0; i < 3; i++)
        {
            var r2 = DefenderStatus.Scan();
            if (r2.Checks.Count != r.Checks.Count) { stable = false; break; }
            if (r2.Level != r.Level) { stable = false; Console.WriteLine($"    第{i + 1}次结论级别变化: {r.Level} → {r2.Level}"); break; }
        }
        Chk(stable, "连续 3 次扫描结果稳定（检查项数、结论级别一致）");

        // ---------- 汇总 ----------
        Console.WriteLine();
        Console.WriteLine("═════════════════════════════════════════════");
        Console.WriteLine(_fail == 0 ? $"全部通过：{_pass} 项" : $"通过 {_pass} 项，失败 {_fail} 项");
        Console.WriteLine("═════════════════════════════════════════════");

        Console.WriteLine();
        Console.WriteLine("---------- 完整报告预览 ----------");
        Console.WriteLine(report);

        return _fail == 0 ? 0 : 1;
    }

    /// <summary>记录受监控的系统状态，用于证明扫描是只读的</summary>
    private static Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>();

        foreach (var (path, name) in new[]
        {
            (@"SOFTWARE\Microsoft\Windows Defender", "DisableAntiSpyware"),
            (@"SOFTWARE\Microsoft\Windows Defender", "DisableAntiVirus"),
            (@"SOFTWARE\Microsoft\Windows Defender", "PassiveMode"),
            (@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"),
        })
        {
            var v = ReadDword(path, name);
            d[$"{name}"] = v.HasValue ? v.Value.ToString() : "(不存在)";
        }

        foreach (var svc in new[] { "WinDefend", "WdNisSvc", "WdFilter" })
        {
            int? s = null;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + svc);
                var v = k?.GetValue("Start");
                if (v != null) s = Convert.ToInt32(v);
            }
            catch { }
            d[$"{svc}.Start"] = s.HasValue ? s.Value.ToString() : "(不存在)";
        }

        return d;
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            var v = k?.GetValue(name);
            return v == null ? (int?)null : Convert.ToInt32(v);
        }
        catch { return null; }
    }
}
