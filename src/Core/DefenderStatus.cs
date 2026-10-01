using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace NetDoctor.Core;

/// <summary>
/// Windows 安全中心（Defender）体检。
///
/// 设计要点：**不依赖 Defender 服务存活**。
///   Defender 被禁用的机器上，Get-MpComputerStatus / Get-MpPreference 会失败
///   （实测报 0x800106ba），只靠它们会得到一句"查询失败"，等于什么都没查。
///   所以这里直接从文件系统、进程表、服务、注册表取证据，
///   能把「引擎平台缺失 / 服务起不来 / 被策略禁用 / 有第三方杀软接管」
///   这几类情况分别判出来 —— 而这些恰恰是出问题时才需要知道的。
///
/// 全部只读：不改注册表、不启停服务、不调用任何写入型 cmdlet。
/// </summary>
internal static class DefenderStatus
{
    // ---------------- 数据模型 ----------------

    internal sealed class Check
    {
        public string Name = "";
        public string Value = "";
        /// <summary>ok / warn / bad / info</summary>
        public string Level = "info";
        public string Note = "";
    }

    internal sealed class Result
    {
        public List<Check> Checks = new();
        public List<string> AvProducts = new();      // 安全中心注册的杀软
        public bool ThirdPartyActive;                 // 已确认第三方杀软正在运行
        public bool EngineUsable;                     // Defender 服务是否真的可用
        public string Verdict = "";                   // 一句话结论
        public string Level = "info";                 // 结论级别

        public int Count(string lv) => Checks.Count(c => c.Level == lv);
    }

    // ---------------- 常量 ----------------

    private const string NServices = @"SYSTEM\CurrentControlSet\Services\";
    private const string NDefender = @"SOFTWARE\Microsoft\Windows Defender";
    private const string NDefenderPolicy = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    private const string NPlatform = @"C:\ProgramData\Microsoft\Windows Defender\Platform";
    private const string NProgramDir = @"C:\Program Files\Windows Defender";

    /// <summary>Defender 的服务与驱动</summary>
    private static readonly (string name, string title, bool critical)[] Services =
    {
        ("WinDefend",              "反恶意软件服务",       true),
        ("WdNisSvc",               "网络检查服务",         true),
        ("WdFilter",               "文件系统筛选驱动",     true),
        ("WdBoot",                 "启动时反恶意软件驱动", true),
        ("WdNisDrv",               "网络检查驱动",         false),
        ("SecurityHealthService",  "安全中心服务",         false),
        ("wscsvc",                 "安全中心",             false),
    };

    /// <summary>引擎运行必需的关键文件</summary>
    private static readonly string[] EngineFiles =
    {
        "MsMpEng.exe", "MpSvc.dll", "NisSrv.exe", "MpClient.dll",
    };

    // ---------------- 主入口 ----------------

    public static Result Scan()
    {
        var r = new Result();

        ScanEngineFiles(r);
        ScanPlatform(r);
        ScanProcesses(r);
        ScanServices(r);
        ScanPolicy(r);
        ScanAvProducts(r);
        ScanMpStatus(r);
        Verdict(r);

        return r;
    }

    // ---------------- 各项检查 ----------------

    private static void ScanEngineFiles(Result r)
    {
        var missing = new List<string>();
        foreach (var f in EngineFiles)
        {
            try
            {
                if (!File.Exists(Path.Combine(NProgramDir, f))) missing.Add(f);
            }
            catch { missing.Add(f); }
        }

        r.Checks.Add(new Check
        {
            Name = "引擎文件",
            Value = missing.Count == 0 ? $"{EngineFiles.Length} 个必需文件齐全" : $"缺 {missing.Count} 个",
            Level = missing.Count == 0 ? "ok" : "bad",
            Note = missing.Count == 0 ? "" : "缺失：" + string.Join("、", missing),
        });
    }

    /// <summary>
    /// 平台目录是引擎的版本化运行时。它为空 = 引擎不可用，
    /// 这是「Defender 装了但跑不起来」最常见的原因，而 WMI 查询完全查不到这一点。
    /// </summary>
    private static void ScanPlatform(Result r)
    {
        int dirs = 0, files = 0;
        string newest = "";
        try
        {
            if (Directory.Exists(NPlatform))
            {
                foreach (var d in Directory.GetDirectories(NPlatform))
                {
                    dirs++;
                    int n = 0;
                    try { n = Directory.GetFiles(d).Length; } catch { }
                    if (n > 0) { files += n; newest = Path.GetFileName(d); }
                }
            }
        }
        catch { }

        if (!Directory.Exists(NPlatform))
        {
            r.Checks.Add(new Check
            {
                Name = "引擎平台目录",
                Value = "不存在",
                Level = "bad",
                Note = NPlatform,
            });
        }
        else if (files == 0)
        {
            r.Checks.Add(new Check
            {
                Name = "引擎平台目录",
                Value = "存在但为空（引擎运行时缺失）",
                Level = "bad",
                Note = "目录下没有任何版本文件 —— Defender 无法启动的典型原因。" +
                       "需要由 Windows 更新重新下发平台包才能恢复。",
            });
        }
        else
        {
            r.Checks.Add(new Check
            {
                Name = "引擎平台目录",
                Value = $"{dirs} 个版本，{files} 个文件",
                Level = "ok",
                Note = newest.Length > 0 ? "最新版本：" + newest : "",
            });
        }
    }

    private static void ScanProcesses(Result r)
    {
        var want = new[] { "MsMpEng", "NisSrv" };
        var running = new List<string>();
        foreach (var w in want)
        {
            try
            {
                if (Process.GetProcessesByName(w).Length > 0) running.Add(w);
            }
            catch { }
        }

        bool antimalware = running.Contains("MsMpEng");
        r.Checks.Add(new Check
        {
            Name = "关键进程",
            Value = running.Count == 0 ? "均未运行" : string.Join("、", running) + " 正在运行",
            Level = antimalware ? "ok" : "bad",
            Note = antimalware ? "" : "反恶意软件引擎进程 MsMpEng 未运行，实时保护实际处于关闭状态。",
        });
    }

    private static void ScanServices(Result r)
    {
        int disabled = 0, stoppedCritical = 0;
        var parts = new List<string>();

        foreach (var (name, title, critical) in Services)
        {
            string state = "";
            int? start = ReadServiceStart(name);

            bool running;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(name);
                running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { running = false; }

            if (start == 4)
            {
                state = "已禁用";
                disabled++;
                if (critical) stoppedCritical++;
            }
            else if (running) state = "运行中";
            else
            {
                state = "已停止";
                if (critical) stoppedCritical++;
            }

            parts.Add($"{name}={state}");
        }

        int total = Services.Length;
        string level = disabled > 0 || stoppedCritical > 0 ? "bad" : "ok";
        r.Checks.Add(new Check
        {
            Name = "服务与驱动",
            Value = $"{total} 项中 {disabled} 项禁用、{stoppedCritical} 项关键项未运行",
            Level = level,
            Note = string.Join("  ", parts),
        });
    }

    private static int? ReadServiceStart(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(NServices + name);
            var v = k?.GetValue("Start");
            return v == null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    /// <summary>
    /// 策略标志。注意：这些键的所有者是 SYSTEM 且 ACL 受保护（AreAccessRulesProtected），
    /// 普通管理员只能读、不能改 —— 所以体检能报出来，但修复需要 TrustedInstaller 权限。
    /// </summary>
    private static void ScanPolicy(Result r)
    {
        var found = new List<string>();

        foreach (var (path, val) in new[]
        {
            (NDefender, "DisableAntiSpyware"),
            (NDefender, "DisableAntiVirus"),
            (NDefender, "PassiveMode"),
        })
        {
            object v = null;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                v = k?.GetValue(val);
            }
            catch { }

            if (v == null) continue;
            int iv = Convert.ToInt32(v);
            if ((val == "PassiveMode" && iv == 1) || (val != "PassiveMode" && iv != 0))
                found.Add($"{val}={iv}");
        }

        // 组策略层的禁用项
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(NDefenderPolicy);
            if (k != null)
            {
                var das = k.GetValue("DisableAntiSpyware");
                if (das != null && Convert.ToInt32(das) != 0) found.Add($"策略 DisableAntiSpyware={das}");
            }
        }
        catch { }

        r.Checks.Add(new Check
        {
            Name = "禁用策略",
            Value = found.Count == 0 ? "未发现禁用标志" : $"{found.Count} 项被设置",
            Level = found.Count == 0 ? "ok" : "bad",
            Note = found.Count == 0 ? ""
                 : string.Join("、", found) +
                   "　（该键归 SYSTEM 所有且 ACL 受保护，普通管理员无法修改）",
        });
    }

    /// <summary>
    /// 安全中心注册的杀软。用注册表读，不依赖 WMI 服务。
    ///
    /// 这里有个容易误判的点：**注册表有登记 ≠ 杀软真的在工作**。
    /// 卸载残留会留下条目，如果只看"登记了第三方杀软"就下结论，
    /// 会把一台实际裸奔的机器报成"已受保护"。所以额外要求：
    ///   · 解析 STATE 标志位，确认产品自称已启用
    ///   · 用它的 PRODUCTEXE 确认进程真的在跑
    /// 两个都满足才算"已被接管"。
    /// </summary>
    private static void ScanAvProducts(Result r)
    {
        var list = new List<string>();
        bool thirdPartyActive = false;
        string activeName = "";

        const string root = @"SOFTWARE\Microsoft\Security Center\Provider\Av";
        try
        {
            using var av = Registry.LocalMachine.OpenSubKey(root);
            if (av != null)
            {
                foreach (var sub in av.GetSubKeyNames())
                {
                    try
                    {
                        using var s = av.OpenSubKey(sub);
                        var name = (s?.GetValue("DISPLAYNAME") as string)?.Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (list.Contains(name)) continue;          // 同一产品可能有多个 GUID 条目
                        list.Add(name);

                        bool isDefender = name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isDefender) continue;

                        // 状态位：0x1000 = 已启用；0x0010 = 已过期
                        var stateObj = s?.GetValue("STATE");
                        int state = stateObj == null ? 0 : Convert.ToInt32(stateObj);
                        bool enabled = (state & 0x1000) != 0;
                        bool upToDate = (state & 0x0010) == 0;

                        // 用 PRODUCTEXE 反推进程名并核实其确实在运行
                        bool procRunning = false;
                        var exe = s?.GetValue("PRODUCTEXE") as string;
                        if (!string.IsNullOrWhiteSpace(exe))
                        {
                            try
                            {
                                var pn = Path.GetFileNameWithoutExtension(exe.Trim().Trim('"'));
                                if (!string.IsNullOrWhiteSpace(pn))
                                    procRunning = Process.GetProcessesByName(pn).Length > 0;
                            }
                            catch { }
                        }

                        if (enabled && procRunning)
                        {
                            thirdPartyActive = true;
                            activeName = name;
                            if (!upToDate) activeName += "（病毒库可能过期）";
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        r.AvProducts = list;
        r.ThirdPartyActive = thirdPartyActive;

        if (list.Count == 0)
        {
            r.Checks.Add(new Check
            {
                Name = "已注册杀软",
                Value = "无（安全中心没有登记任何杀毒软件）",
                Level = "warn",
                Note = "未能从安全中心注册表读到杀软条目；若确实没装第三方杀软，请以「禁用策略」与「关键进程」两项为准。",
            });
            return;
        }

        if (thirdPartyActive)
        {
            r.Checks.Add(new Check
            {
                Name = "已注册杀软",
                Value = string.Join("、", list),
                Level = "ok",
                Note = $"已确认「{activeName}」正在运行并处于启用状态；" +
                       "此时 Defender 停止实时防护、引擎平台被移除，属于 Windows 的正常共存机制，不是故障。",
            });
        }
        else
        {
            bool onlyDefender = list.All(n => n.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0);
            r.Checks.Add(new Check
            {
                Name = "已注册杀软",
                Value = string.Join("、", list),
                Level = onlyDefender ? "info" : "warn",
                Note = onlyDefender
                    ? "只有 Windows Defender 自己登记在册。"
                    : "登记了第三方杀软，但**未能确认其进程正在运行** —— " +
                      "可能是卸载残留。若确实没在用该杀软，请以「关键进程」与「引擎平台目录」两项为准。",
            });
        }
    }

    /// <summary>
    /// 尝试取 Defender 自报状态。服务不可用时这是必然失败的，
    /// 属于预期路径，安静降级即可，不当作错误。
    /// </summary>
    private static void ScanMpStatus(Result r)
    {
        // 不要在脚本里改 [Console]::OutputEncoding：
        // PowerShell 在输出被重定向时本身就是按「活动代码页」（本机 936）写字节的，
        // 而 Cmd.Run 恰好以 GBK 读 —— 天然匹配。
        // 实测手工设成 936 反而会让中文变成乱码（设成 UTF8 则变成另一种语言的提示），
        // 保持默认即可。
        string script =
            "$ErrorActionPreference='Stop';" +
            "try{" +
            " $s=Get-MpComputerStatus;" +
            " $p=Get-MpPreference;" +
            " 'OK|' + $s.AMEngineVersion + '|' + $s.RealTimeProtectionEnabled + '|' + $s.AntivirusEnabled + '|' + $s.IsTamperProtected + '|' + $s.AntivirusSignatureVersion + '|' + $p.ScanPriority + '|' + $p.PUAProtection + '|' + $p.MAPSReporting" +
            "}catch{ 'ERR|' + $_.Exception.Message }";

        string output = "";
        try
        {
            // 用 -EncodedCommand 传脚本（Base64 of UTF-16LE）：
            // 直接把脚本拼进命令行时中文会经过命令行编码转换而损坏。
            // Base64 是纯 ASCII，彻底绕开该问题，也免去引号转义。
            var bytes = Encoding.Unicode.GetBytes(script);
            string encoded = Convert.ToBase64String(bytes);

            var r2 = Cmd.Run("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, 20000);

            // 必须用 All（StdOut + StdErr）：
            // PowerShell 把异常写进 **stderr**，而且序列化成 CLIXML
            // （形如 "#< CLIXML … <S S=\"Error\">操作失败…</S>"）。
            // 只读 StdOut 的话，报错时拿到的是空串或 XML 标签，看不到真正原因。
            output = (r2.All ?? "").Trim();
            output = StripClixml(output);
        }
        catch { }

        // 解析必须严谨：曾经出现过「脚本执行失败，但输出里回显了脚本源码，
        // 于是把 ' + $s.AMEngineVersion + ' 这种脚本片段当成检测值」的事故
        // —— 那会凭空造出几个假的"异常"，比查不到更糟。
        // 所以只有确凿拿到完整标记行才算有效，其余一律按失败处理。
        var okM = System.Text.RegularExpressions.Regex.Match(
            output, @"OK\|([^\s|]+)\|(True|False)\|(True|False)\|(True|False)\|([^\s|]*)\|(-?\d+)\|(-?\d+)\|(\d+)");
        var errM = System.Text.RegularExpressions.Regex.Match(output, @"ERR\|");

        if (!okM.Success)
        {
            string pretty = "服务未运行";
            string note = "Defender 服务未运行或不可用，查询不到自报状态（属预期表现，不是本工具出错）。";

            var mc = System.Text.RegularExpressions.Regex.Match(output, @"0x[0-9A-Fa-f]{8}");
            if (mc.Success)
            {
                pretty = mc.Value.Equals("0x800106ba", StringComparison.OrdinalIgnoreCase)
                    ? "服务未运行（0x800106ba）"
                    : $"查询失败（{mc.Value}）";
                note = $"系统返回 {mc.Value}。{note}";
            }
            else if (!errM.Success && output.Length > 0)
            {
                // 既没有完整标记、也没有 ERR 标记，说明解析没对上，明说是解析问题
                pretty = "无法解析查询结果";
                note = "未能从 PowerShell 输出中解析出状态（可能输出格式有变）。";
            }

            r.Checks.Add(new Check
            {
                Name = "Defender 自报状态",
                Value = pretty,
                Level = "warn",
                Note = note,
            });
            return;
        }

        string engine = okM.Groups[1].Value.Trim();
        string rtp = okM.Groups[2].Value;
        string av = okM.Groups[3].Value;
        string tamper = okM.Groups[4].Value;
        string sig = okM.Groups[5].Value.Trim();
        string prio = okM.Groups[6].Value;
        string pua = okM.Groups[7].Value;
        string maps = okM.Groups[8].Value;

        bool engineOk = engine.Length > 0 && engine != "0.0.0.0";
        r.EngineUsable = engineOk;

        r.Checks.Add(new Check
        {
            Name = "引擎版本",
            Value = engine.Length == 0 ? "(空)" : engine,
            Level = engineOk ? "ok" : "bad",
            Note = engineOk ? "" : "引擎版本为 0.0.0.0 表示引擎未加载。",
        });

        r.Checks.Add(new Check
        {
            Name = "实时保护",
            Value = rtp == "True" ? "已开启" : "已关闭",
            Level = rtp == "True" ? "ok" : "bad",
        });

        r.Checks.Add(new Check
        {
            Name = "反病毒启用",
            Value = av == "True" ? "已启用" : "未启用",
            Level = av == "True" ? "ok" : "bad",
        });

        r.Checks.Add(new Check
        {
            Name = "篡改保护",
            Value = tamper == "True" ? "已开启" : "已关闭",
            Level = tamper == "True" ? "ok" : "warn",
            Note = tamper == "True" ? "" : "篡改保护关闭时，任何程序都能改 Defender 设置。",
        });

        r.Checks.Add(new Check { Name = "病毒库版本", Value = sig.Length == 0 ? "(空)" : sig, Level = "info" });

        string prioText = prio switch
        {
            "0" => "0（高）",
            "1" => "1（较高）",
            "2" => "2（正常）",
            "3" => "3（较低）",
            "4" => "4（低）",
            _ => prio,
        };
        r.Checks.Add(new Check
        {
            Name = "扫描优先级",
            Value = prioText,
            Level = "info",
            Note = "调低可让计划扫描不抢占 CPU（需要 Defender 服务可用才能修改）。",
        });

        r.Checks.Add(new Check
        {
            Name = "PUA 防护",
            Value = pua switch { "0" => "已关闭", "1" => "已启用", "2" => "仅审核", _ => pua },
            Level = "info",
        });

        r.Checks.Add(new Check
        {
            Name = "云保护(MAPS)",
            Value = maps switch { "0" => "已关闭", "1" => "基本", "2" => "高级", _ => (maps.Length == 0 ? "(空)" : maps) },
            Level = "info",
        });
    }

    /// <summary>
    /// 去掉 PowerShell 的 CLIXML 包装，取出可读文本。
    ///
    /// PowerShell 在输出被重定向时，会把异常序列化成 CLIXML：
    ///   #&lt; CLIXML
    ///   &lt;Objs Version="1.1.0.1" ...&gt;&lt;S S="Error"&gt;操作失败，出现以下错误: 0x800106ba&lt;/S&gt;…
    /// 直接把这坨塞进报告既看不懂也没意义，这里只保留 &lt;S&gt; 里的文本。
    /// </summary>
    private static string StripClixml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (!s.Contains("CLIXML") && !s.Contains("<Objs")) return s.Trim();

        var sb = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(s, "<S[^>]*>(.*?)</S>",
                     System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string t = m.Groups[1].Value
                .Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&apos;", "'")
                .Replace("&amp;", "&");
            if (!string.IsNullOrWhiteSpace(t)) sb.Append(t.Trim()).Append(' ');
        }

        // 顺带滤掉 XML 声明与 Objs 外壳残留
        var res = sb.ToString().Trim();
        if (res.Length == 0)
        {
            int i = s.IndexOf("CLIXML", StringComparison.Ordinal);
            res = i >= 0 ? s.Substring(i + 6).Trim() : s.Trim();
        }
        return res;
    }

    private static void Verdict(Result r)    {
        bool engineBad = r.Checks.Any(c => c.Name == "引擎平台目录" && c.Level == "bad")
                      || r.Checks.Any(c => c.Name == "关键进程" && c.Level == "bad");
        bool policySet = r.Checks.Any(c => c.Name == "禁用策略" && c.Level == "bad");

        // 顺序很重要：先确认「是否有别的杀软真的在顶着」。
        // 只看注册表登记就下"已受保护"的结论是不可靠的 —— 卸载残留也会留条目，
        // 那会把一台实际没防护的机器报成正常。
        if (r.ThirdPartyActive)
        {
            r.Verdict = "已由第三方杀软接管，Defender 停止实时防护属正常共存，未发现风险。";
            r.Level = "ok";
            return;
        }

        if (engineBad && policySet)
        {
            r.Verdict = "Defender 引擎不可用，且存在禁用标志 —— 本机可能没有可用的杀毒防护。";
            r.Level = "bad";
            return;
        }

        if (engineBad)
        {
            r.Verdict = "Defender 引擎不可用（服务未运行 / 引擎平台缺失），实时防护未生效。";
            r.Level = "bad";
            return;
        }

        if (policySet)
        {
            r.Verdict = "存在禁用标志，但引擎文件在位 —— 策略层被改过，建议核对是否为本意。";
            r.Level = "warn";
            return;
        }

        if (!r.EngineUsable)
        {
            r.Verdict = "无法确认实时防护状态，请以「关键进程」与「服务与驱动」两项为准。";
            r.Level = "warn";
            return;
        }

        r.Verdict = "Defender 状态正常。";
        r.Level = "ok";
    }

    // ---------------- 输出 ----------------

    public static string BuildReport(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=============== Windows 安全中心体检 ===============");
        sb.AppendLine($"检测时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("说明：只读体检，不修改任何设置。数据来自文件系统 / 进程 / 服务 / 注册表。");
        sb.AppendLine();

        sb.AppendLine($"【结论】{r.Verdict}");
        sb.AppendLine($"　　统计：正常 {r.Count("ok")} · 注意 {r.Count("warn")} · 异常 {r.Count("bad")} · 信息 {r.Count("info")}");
        sb.AppendLine();

        sb.AppendLine("【逐项】");
        foreach (var c in r.Checks)
        {
            string mark = c.Level switch
            {
                "ok" => "[正常]",
                "warn" => "[注意]",
                "bad" => "[异常]",
                _ => "[信息]",
            };
            sb.AppendLine($"  {mark} {c.Name}：{c.Value}");
            if (!string.IsNullOrEmpty(c.Note)) sb.AppendLine($"         {c.Note}");
        }

        if (r.AvProducts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【安全中心注册的杀软】");
            foreach (var a in r.AvProducts) sb.AppendLine("  · " + a);
        }

        sb.AppendLine();
        sb.AppendLine("提示：本工具不提供关闭 Defender 的功能 —— 那会降低本机安全性。");
        sb.AppendLine("　　　若「禁用策略」报异常，该注册表键归 SYSTEM 所有且受 ACL 保护，");
        sb.AppendLine("　　　普通管理员无法修改，需由系统更新重新下发组件。");
        return sb.ToString();
    }

    public static string SummaryLine(Result r)
        => $"正常 {r.Count("ok")} · 注意 {r.Count("warn")} · 异常 {r.Count("bad")}　{r.Verdict}";

    private static string Shorten(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
