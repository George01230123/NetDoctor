using System.Net.NetworkInformation;
using NetDoctor.Core;

namespace NetDoctor.Core;

internal sealed class DnsCandidate
{
    public string Name = "";
    public string Ip = "";
    public double Ms = -1;
    public bool Ok;
    public string Note = "";
    public override string ToString() => $"{Name} ({Ip})";
}

internal static class NetworkOptimizer
{
    /// <summary>待测速的公共 DNS 清单（国内优先）</summary>
    public static readonly (string Name, string Ip)[] Known =
    {
        ("阿里云 DNS",      "223.5.5.5"),
        ("阿里云 DNS 备",   "223.6.6.6"),
        ("腾讯 DNSPod",     "119.29.29.29"),
        ("腾讯 DNSPod 备",  "119.28.28.28"),
        ("114 DNS",         "114.114.114.114"),
        ("114 DNS 备",      "114.114.115.115"),
        ("百度 DNS",        "180.76.76.76"),
        ("CNNIC DNS",       "1.2.4.8"),
        ("360 DNS",         "101.226.4.6"),
        ("Google DNS",      "8.8.8.8"),
        ("Cloudflare",      "1.1.1.1"),
        ("Quad9",           "9.9.9.9"),
    };

    /// <summary>并发测速（限制 6 个并发）</summary>
    public static async Task<List<DnsCandidate>> BenchmarkAll(
        IProgress<DnsCandidate> progress = null,
        CancellationToken ct = default)
    {
        var results = new List<DnsCandidate>();
        using var gate = new SemaphoreSlim(6);
        var tasks = Known.Select(async k =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var t = await NetworkDiag.TestDnsServer(k.Ip, 1500).ConfigureAwait(false);
                var c = new DnsCandidate { Name = k.Name, Ip = k.Ip, Ms = t.Ms, Ok = t.Ok, Note = t.Note };
                lock (results) results.Add(c);
                progress?.Report(c);
            }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderByDescending(r => r.Ok)
                      .ThenBy(r => r.Ms < 0 ? 99999 : r.Ms)
                      .ToList();
    }

    /// <summary>把 DNS 写入所有已连接的物理/无线网卡（跳过过滤层与隧道）</summary>
    public static async Task<bool> ApplyDns(string[] servers)
    {
        var targets = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n =>
            {
                var t = NetworkDiag.ClassifyType(n.NetworkInterfaceType, n.Description, n.Name);
                return t == "有线" || t == "无线" || t == "虚拟";
            })
            .Select(n => n.Name)
            .ToList();

        if (targets.Count == 0) { Log.Err("没有找到可用网卡"); return false; }

        bool all = true;
        foreach (var nic in targets)
        {
            Log.Step($"设置网卡「{nic}」DNS → {string.Join(", ", servers)}");
            bool ok = await Log.RunLogged(
                $"网卡「{nic}」主 DNS = {servers[0]}",
                "netsh", $"interface ip set dns name=\"{nic}\" source=static addr={servers[0]} primary", 30000);

            for (int i = 1; i < servers.Length; i++)
                await Cmd.Netsh($"interface ip add dns name=\"{nic}\" addr={servers[i]} index={i + 1}", 30000);

            await Cmd.Ipconfig("/flushdns", 20000);
            all &= ok;
        }

        Log.Warn("DNS 已修改。如需恢复：点上面的「恢复自动获取 DNS」");
        return all;
    }

    /// <summary>恢复为自动获取 DNS</summary>
    public static async Task<bool> RestoreAutoDns()
    {
        var targets = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n =>
            {
                var t = NetworkDiag.ClassifyType(n.NetworkInterfaceType, n.Description, n.Name);
                return t == "有线" || t == "无线" || t == "虚拟";
            })
            .Select(n => n.Name).ToList();

        if (targets.Count == 0) { Log.Err("没有找到可用网卡"); return false; }
        bool all = true;
        foreach (var nic in targets)
            all &= await Log.RunLogged($"网卡「{nic}」DNS 恢复自动获取",
                "netsh", $"interface ip set dns name=\"{nic}\" source=dhcp", 30000);

        await Cmd.Ipconfig("/flushdns", 20000);
        Log.Ok("已恢复为自动获取 DNS");
        return all;
    }

    // ---------------- TCP 参数 ----------------
    public static async Task<string> TcpGlobalTextAsync()
        => (await Cmd.Netsh("int tcp show global", 20000)).All.Trim();

    /// <summary>把 netsh int tcp show global 的输出解析成 键/值 列表</summary>
    public static async Task<List<KeyValuePair<string, string>>> TcpGlobalPairsAsync()
    {
        var raw = await TcpGlobalTextAsync();
        var list = new List<KeyValuePair<string, string>>();
        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            int i = t.IndexOf(':');
            if (i < 0) i = t.IndexOf('：');
            if (i <= 0) continue;
            var k = t.Substring(0, i).Trim();
            var v = t.Substring(i + 1).Trim();
            if (k.Length == 0 || v.Length == 0) continue;
            if (k.Contains("---") || k.Contains("参数")) continue;
            list.Add(new KeyValuePair<string, string>(k, v));
        }
        return list;
    }

    /// <summary>响应优先：游戏 / 远程桌面 / 网页浏览</summary>
    public static async Task<bool> TuneResponsive()
    {
        Log.Step("应用「响应优先」TCP 参数");
        bool ok = true;
        ok &= await Log.RunLogged("autotuninglevel=normal", "netsh", "int tcp set global autotuninglevel=normal", 20000);
        ok &= await Log.RunLogged("rss=enabled", "netsh", "int tcp set global rss=enabled", 20000);
        ok &= await Log.RunLogged("ecncapability=disabled", "netsh", "int tcp set global ecncapability=disabled", 20000);
        ok &= await Log.RunLogged("timestamps=disabled", "netsh", "int tcp set global timestamps=disabled", 20000);
        await Log.RunLogged("rsc=disabled（降低延迟）", "netsh", "int tcp set global rsc=disabled", 20000);
        await Log.RunLogged("heuristics=disabled", "netsh", "int tcp set heuristics disabled", 20000);
        await Cmd.Ipconfig("/flushdns", 20000);
        Log.Ok("「响应优先」已应用（重启后依然有效）");
        return ok;
    }

    /// <summary>吞吐优先：下载 / 大文件传输</summary>
    public static async Task<bool> TuneThroughput()
    {
        Log.Step("应用「吞吐优先」TCP 参数");
        bool ok = true;
        ok &= await Log.RunLogged("autotuninglevel=normal", "netsh", "int tcp set global autotuninglevel=normal", 20000);
        ok &= await Log.RunLogged("rss=enabled", "netsh", "int tcp set global rss=enabled", 20000);
        ok &= await Log.RunLogged("rsc=enabled", "netsh", "int tcp set global rsc=enabled", 20000);
        ok &= await Log.RunLogged("ecncapability=disabled", "netsh", "int tcp set global ecncapability=disabled", 20000);
        await Log.RunLogged("heuristics=enabled", "netsh", "int tcp set heuristics enabled", 20000);
        await Cmd.Ipconfig("/flushdns", 20000);
        Log.Ok("「吞吐优先」已应用");
        return ok;
    }

    /// <summary>还原为 Windows 默认 TCP 参数</summary>
    public static async Task<bool> TuneDefault()
    {
        Log.Step("还原 TCP 参数为 Windows 默认");
        bool ok = true;
        ok &= await Log.RunLogged("autotuninglevel=normal", "netsh", "int tcp set global autotuninglevel=normal", 20000);
        ok &= await Log.RunLogged("congestionprovider=default", "netsh", "int tcp set global congestionprovider=default", 20000);
        ok &= await Log.RunLogged("ecncapability=disabled", "netsh", "int tcp set global ecncapability=disabled", 20000);
        ok &= await Log.RunLogged("timestamps=disabled", "netsh", "int tcp set global timestamps=disabled", 20000);
        ok &= await Log.RunLogged("rss=enabled", "netsh", "int tcp set global rss=enabled", 20000);
        ok &= await Log.RunLogged("rsc=enabled", "netsh", "int tcp set global rsc=enabled", 20000);
        await Log.RunLogged("heuristics=enabled", "netsh", "int tcp set heuristics enabled", 20000);
        Log.Ok("已还原默认参数");
        return ok;
    }

    // ---------------- 网卡节能 ----------------
    public static async Task<List<Check>> ScanNicPowerAsync()
    {
        var list = new List<Check>();
        var ps = await Cmd.RunAsync("powershell",
            "-NoProfile -Command \"Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue | " +
            "Select-Object Name,AllowComputerToTurnOffDevice | ConvertTo-Csv -NoTypeInformation\"", 40000);
        var lines = ps.All.Split('\n')
                          .Select(x => x.Trim().Trim('"'))
                          .Where(x => x.Contains(","))
                          .ToList();
        if (lines.Count <= 1)
        {
            list.Add(new Check { Group = "网卡", Name = "电源管理", Level = Sev.Info, Detail = "无法读取（系统或驱动不支持）" });
            return list;
        }
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
                list.Add(new Check
                {
                    Group = "网卡",
                    Name = $"「{parts[0].Trim()}」允许关闭设备以省电",
                    Level = Sev.Warn,
                    Detail = "已开启 —— 可能导致随机掉线/断流",
                    Hint = "建议在设备管理器中关闭网卡电源管理（下方按钮可一键处理）"
                });
        }
        if (list.Count == 0)
            list.Add(new Check { Group = "网卡", Name = "电源管理", Level = Sev.Ok, Detail = "未发现异常省电设置" });
        return list;
    }

    /// <summary>关闭所有网卡的「允许计算机关闭此设备以节约电源」</summary>
    public static async Task<bool> DisableNicPowerSave()
    {
        Log.Step("关闭网卡电源节能");
        bool any = false;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var script =
                $"$n='{ni.Name}'; " +
                "try { Disable-NetAdapterPowerManagement -Name $n -ErrorAction Stop; Write-Output \"power:$n\" } catch {}; " +
                "try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Energy Efficient Ethernet' " +
                "-DisplayValue 'Disabled' -ErrorAction Stop; Write-Output \"eee:$n\" } catch {}; " +
                "try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName '节能以太网' " +
                "-DisplayValue '已禁用' -ErrorAction Stop; Write-Output \"eee2:$n\" } catch {}";
            var r = await Cmd.RunAsync("powershell", "-NoProfile -Command \"" + script + "\"", 60000);
            var outp = r.All.Trim();
            if (outp.Contains("power:") || outp.Contains("eee"))
            {
                Log.Ok($"网卡「{ni.Name}」节能相关设置已处理");
                any = true;
            }
            else Log.Info($"网卡「{ni.Name}」无需处理或驱动不支持该项");
        }
        if (!any) Log.Warn("没有可修改的项（可能已全部关闭，或驱动不提供该选项）");
        return true;
    }

    /// <summary>当前 TCP 连接数 TOP 进程（脚本方式，避免多引号解析问题）</summary>
    public static async Task<string> NetTopProcesses()
    {
        var script = EmbeddedScripts.PathOf("nettop.ps1");
        if (!File.Exists(script))
            return "(脚本释放失败，无法统计)";

        var r = await Cmd.RunAsync("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", 90000);
        var txt = r.All.Trim();
        return string.IsNullOrWhiteSpace(txt) ? "(统计结果为空)" : txt;
    }
}
