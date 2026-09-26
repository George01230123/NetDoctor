using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using NetDoctor.Core;

namespace NetDoctor.Core;

/// <summary>修改前的原始状态快照，便于对照回滚</summary>
internal static class Backup
{
    public static string Root
    {
        get
        {
            var d = Path.Combine(AppContext.BaseDirectory, "backup");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    public static async Task Snapshot(string reason)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("; ==================== 状态快照 ====================");
            sb.AppendLine($"; 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"; 原因: {reason}");

            var (en, srv, pac) = NetworkDiag.SystemProxy();
            sb.AppendLine();
            sb.AppendLine("[系统代理]");
            sb.AppendLine($"ProxyEnable={en}");
            sb.AppendLine($"ProxyServer={srv}");
            sb.AppendLine($"AutoConfigURL={pac}");

            sb.AppendLine();
            sb.AppendLine("[WinHTTP代理]");
            sb.AppendLine((await Cmd.Netsh("winhttp show proxy", 15000)).All.Replace("\r", ""));

            sb.AppendLine();
            sb.AppendLine("[TCP全局参数]");
            sb.AppendLine((await Cmd.Netsh("int tcp show global", 15000)).All.Replace("\r", ""));

            sb.AppendLine();
            sb.AppendLine("[网卡与DNS]");
            foreach (var a in NetworkDiag.Adapters())
                sb.AppendLine($"  {a.Name} | IP={a.IPv4} | 网关={a.Gateway} | DNS={a.Dns} | {a.Dhcp}");

            sb.AppendLine();
            sb.AppendLine("[默认路由]");
            sb.AppendLine((await Cmd.RunAsync("route", "print -4", 20000)).All.Replace("\r", ""));

            var path = Path.Combine(Root, "backup.ini");
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);

            try
            {
                string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                            @"drivers\etc\hosts");
                if (File.Exists(hosts))
                    File.Copy(hosts, Path.Combine(Root, $"hosts_{DateTime.Now:yyyyMMdd_HHmmss}.bak"), true);
            }
            catch { }

            Log.Ok("已生成状态快照 → " + path);
        }
        catch (Exception ex)
        {
            Log.Warn("快照生成失败：" + ex.Message);
        }
    }
}

internal static class NetworkRepair
{
    private static bool _needReboot;

    public static bool NeedReboot => _needReboot;

    // ---------------- 单项修复 ----------------
    public static async Task<bool> FlushDns()
    {
        bool a = await Log.RunLogged("刷新 DNS 解析缓存", "ipconfig", "/flushdns", 20000);
        await Log.RunLogged("重新注册 DNS 记录", "ipconfig", "/registerdns", 30000);
        return a;
    }

    public static async Task<bool> RenewDhcp()
    {
        Log.Warn("重新获取 IP 会短暂断网 3~10 秒");
        await Log.RunLogged("释放当前 IP", "ipconfig", "/release", 40000);
        bool ok = await Log.RunLogged("重新获取 IP", "ipconfig", "/renew", 60000);
        if (!ok) Log.Warn("获取失败：请确认该网卡是「自动获得 IP 地址」模式");
        return ok;
    }

    public static async Task<bool> ResetWinsock()
    {
        bool ok = await Log.RunLogged("重置 Winsock 目录", "netsh", "winsock reset", 60000);
        if (ok) { _needReboot = true; Log.Warn("Winsock 已重置，需重启电脑后完全生效"); }
        return ok;
    }

    public static async Task<bool> ResetTcpIp()
    {
        await Log.RunLogged("重置 IPv4 协议栈", "netsh", "int ip reset", 60000);
        await Log.RunLogged("重置 IPv6 协议栈", "netsh", "int ipv6 reset", 60000);
        await Log.RunLogged("重置接口 IPv4 配置", "netsh", "interface ipv4 reset", 60000);
        _needReboot = true;
        Log.Warn("TCP/IP 已重置，建议重启电脑");
        return true;
    }

    public static async Task<bool> ClearArp()
    {
        bool a = await Log.RunLogged("清空 ARP 缓存", "netsh", "interface ip delete arpcache", 30000);
        await Log.RunLogged("清空所有接口邻居缓存", "netsh", "interface ip delete neighbors", 30000);
        return a;
    }

    public static async Task<bool> ClearProxy()
    {
        var (en, srv, pac) = NetworkDiag.SystemProxy();
        Log.Info($"当前系统代理：启用={en}  服务器='{srv}'  PAC='{pac}'");
        bool ok = true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (k != null)
            {
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                if (k.GetValue("ProxyServer") != null) k.DeleteValue("ProxyServer", false);
                if (k.GetValue("AutoConfigURL") != null) k.DeleteValue("AutoConfigURL", false);
                Log.Ok("已关闭 IE/系统代理设置（ProxyEnable=0，清除 ProxyServer 与 PAC）");
            }
            else Log.Warn("无法打开 Internet Settings 注册表项");
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Err("关闭系统代理失败：" + ex.Message);
        }

        await Log.RunLogged("重置 WinHTTP 系统代理", "netsh", "winhttp reset proxy", 30000);

        // 通知已运行程序代理已变更
        try
        {
            const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
            const int INTERNET_OPTION_REFRESH = 37;
            IntPtr h = NativeMethods.InternetOpenA("NetDoctor", 0, null, null, 0);
            if (h != IntPtr.Zero)
            {
                NativeMethods.InternetSetOptionA(h, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                NativeMethods.InternetSetOptionA(h, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                NativeMethods.InternetCloseHandle(h);
                Log.Ok("已通知系统代理设置变更");
            }
        }
        catch { }
        return ok;
    }

    public static async Task<bool> FixServices()
    {
        string[] svcs = { "Dhcp", "Dnscache", "NlaSvc", "netprofm", "WlanSvc", "WinHttpAutoProxySvc", "nsi", "Netman", "Wcmsvc" };
        bool all = true;
        foreach (var name in svcs)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Running) { Log.Ok($"服务 {name} 已在运行"); continue; }

                Log.Step($"修复服务 {name}（当前 {sc.Status}）");
                await Log.RunLogged($"{name} 设置为自动启动", "sc", $"config {name} start= auto", 20000);

                bool started = false;
                try
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                    started = sc.Status == ServiceControllerStatus.Running;
                }
                catch (Exception ex) { Log.Warn($"启动 {name} 异常：{ex.Message}"); }

                if (started) Log.Ok($"服务 {name} 已启动");
                else { Log.Err($"服务 {name} 仍未能启动"); all = false; }
            }
            catch (Exception ex)
            {
                Log.Warn($"服务 {name} 处理异常：{ex.Message}");
                all = false;
            }
        }
        return all;
    }

    public static async Task<bool> FixHosts()
    {
        await Task.Yield();
        string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                    @"drivers\etc\hosts");
        try
        {
            if (File.Exists(hosts))
            {
                try { File.SetAttributes(hosts, FileAttributes.Normal); } catch { }
                File.Copy(hosts, Path.Combine(Backup.Root, $"hosts_{DateTime.Now:yyyyMMdd_HHmmss}.bak"), true);
                Log.Ok("原 hosts 已备份到 backup 目录");
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Copyright (c) 1993-2009 Microsoft Corp.");
            sb.AppendLine("#");
            sb.AppendLine("# 由 夕颜若雪网络工具 还原为默认内容");
            sb.AppendLine($"# 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("127.0.0.1       localhost");
            sb.AppendLine("::1             localhost");
            File.WriteAllText(hosts, sb.ToString(), new UTF8Encoding(false));
            Log.Ok("hosts 已还原为默认（仅保留 localhost）");
            await Log.RunLogged("刷新 DNS 缓存", "ipconfig", "/flushdns", 20000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Err("还原 hosts 失败：" + ex.Message);
            return false;
        }
    }

    public static async Task<bool> ResetFirewall()
    {
        Log.Warn("重置防火墙会删除所有自定义规则，请谨慎");
        return await Log.RunLogged("重置防火墙为默认策略", "netsh", "advfirewall reset", 60000);
    }

    public static async Task<bool> FixConflictIp()
    {
        await Task.Yield();
        var bad = NetworkDiag.Adapters().Where(a => a.Up && a.IPv4.StartsWith("169.254.")).ToList();
        if (bad.Count == 0)
        {
            Log.Info("未发现 169.254.x.x 无效地址，跳过");
            return true;
        }
        foreach (var a in bad) Log.Warn($"网卡「{a.Name}」拿到的是无效地址 {a.IPv4}");
        Log.Step("尝试重新获取 IP 以摆脱 169.254 地址");
        return await RenewDhcp();
    }

    public static async Task ResetIpStackFull()
    {
        Log.Step("重置 TCP/IP 协议栈（netsh int ip reset）");
        await Log.RunLogged("重置 IPv4 协议栈", "netsh", "int ip reset", 60000);
        await Log.RunLogged("重置 IPv6 协议栈", "netsh", "int ipv6 reset", 60000);
        await Log.RunLogged("重置接口 IPv4", "netsh", "interface ipv4 reset", 60000);
        _needReboot = true;
        Log.Warn("TCP/IP 已重置，建议重启电脑");
    }

    public static async Task FlushDnsOnly()
    {
        await Log.RunLogged("刷新 DNS 解析缓存", "ipconfig", "/flushdns", 20000);
    }

    public static async Task ClearWinHttpProxy()
    {
        await Log.RunLogged("重置 WinHTTP 代理", "netsh", "winhttp reset proxy", 30000);
    }

    // ---------------- 组合修复 ----------------
    public static async Task QuickFix()
    {
        Log.Info("════════ 一键断网修复 开始 ════════");
        await Backup.Snapshot("一键断网修复");
        await FlushDns();
        await FixConflictIp();
        await ClearArp();
        await ClearProxy();
        await FixServices();
        Log.Info("════════ 一键断网修复 结束 ════════");
        if (_needReboot) Log.Warn("有项目需要重启电脑才能完全生效");
    }

    /// <summary>
    /// 深度修复。界面层与原生 DLL 共用这一份实现，
    /// 因此这里不能直接弹窗 —— 交互通过 <paramref name="confirmReboot"/> 回调注入：
    /// 传 null 表示无界面（原生 DLL 场景），只做修复、不询问重启。
    /// </summary>
    /// <param name="confirmReboot">返回 true 表示调用方同意立即重启；null 表示不询问也不重启</param>
    /// <param name="notify">用于提示"已安排重启"等信息的回调，可为 null</param>
    public static async Task DeepFix(Func<bool> confirmReboot = null, Action<string> notify = null)
    {
        Log.Info("════════ 深度修复 开始（会重置协议栈）════════");
        await Backup.Snapshot("深度修复");
        await FlushDns();
        await ResetWinsock();
        await ResetIpStackFull();
        await ClearArp();
        await ClearProxy();
        await FixServices();
        await FixHosts();
        await RenewDhcp();
        Log.Info("════════ 深度修复 结束 ════════");
        Log.Warn("深度修复已完成，请重启电脑使全部改动生效");

        if (confirmReboot == null) return;          // 无界面：不自动重启
        if (!confirmReboot()) { Log.Info("用户暂不重启"); return; }

        Log.Warn("用户选择重启电脑");
        Cmd.Run("shutdown", "/r /t 15 /c \"网络工具：深度修复完成，15 秒后重启\"", 10000);
        notify?.Invoke("已在 15 秒后重启。\n\n取消请立即运行：shutdown /a");
    }
}
