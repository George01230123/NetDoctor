using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NetDoctor.Core;

internal enum Sev { Ok, Warn, Bad, Info }

internal sealed class Check
{
    public string Group = "";
    public string Name = "";
    public string Detail = "";
    public Sev Level = Sev.Info;
    public string Hint = "";          // 修复建议
    public override string ToString() => $"[{Level}] {Name}: {Detail}";
}

internal sealed class AdapterInfo
{
    public string Name, Desc, Type, Mac, IPv4, IPv6, Mask, Gateway, Dns;
    public string Dhcp, Speed;
    public bool Up;
}

internal sealed class DnsTest
{
    public string Server = "";
    public double Ms = -1;
    public bool Ok;
    public string Note = "";
}

internal sealed class DiagReport
{
    public readonly List<Check> Checks = new();
    public readonly List<AdapterInfo> Adapters = new();
    public readonly List<DnsTest> Dns = new();
    public string Gateway = "";
    public string WebProxy = "";
    public string WinHttpProxy = "";
    public DateTime At = DateTime.Now;

    public int Count(Sev s) => Checks.Count(c => c.Level == s);
    public string Summary => $"正常 {Count(Sev.Ok)} · 注意 {Count(Sev.Warn)} · 异常 {Count(Sev.Bad)}";
}

internal static class NetworkDiag
{
    private const string TestHost = "www.baidu.com";

    // ---------- 网卡信息 ----------
    /// <summary>区分物理网卡与虚拟/过滤驱动网卡，避免把 WFP/QoS 过滤层当成网卡</summary>
    public static string ClassifyType(NetworkInterfaceType t, string desc, string name)
    {
        string s = (desc + " " + name).ToLowerInvariant();

        if (s.Contains("wfp") || s.Contains("qos packet scheduler") || s.Contains("lightweight filter")
            || s.Contains("filter driver") || s.Contains("scheduler") || s.Contains("native mac layer"))
            return "过滤层";

        if (s.Contains("vmware") || s.Contains("virtualbox") || s.Contains("hyper-v") || s.Contains("vethernet")
            || s.Contains("wsl") || s.Contains("loopback") || s.Contains("tap-") || s.Contains("tun")
            || s.Contains("npcap") || s.Contains("virtual") || s.Contains("虚拟"))
            return "虚拟";

        if (t == NetworkInterfaceType.Loopback) return "回环";
        if (t == NetworkInterfaceType.Tunnel) return "隧道";
        if (t == NetworkInterfaceType.Wireless80211) return "无线";
        if (t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet
            || t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx
            || t == NetworkInterfaceType.Ethernet3Megabit)
            return "有线";
        return "其它";
    }

    /// <summary>真正承载体流量的网卡（排除过滤层与回环）</summary>
    public static List<AdapterInfo> RealAdapters()
        => Adapters().Where(a => a.Type != "过滤层" && a.Type != "回环").ToList();

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var ip = ni.GetIPProperties();
            var uni = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            var gw = ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            var dns = ip.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                                     .Select(d => d.ToString()).ToList();
            string type = ClassifyType(ni.NetworkInterfaceType, ni.Description, ni.Name);
            string mac = "";
            try { mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); } catch { }

            string dhcp = "—";
            try
            {
                var props = ni.GetIPProperties().GetIPv4Properties();
                if (props != null) dhcp = props.IsDhcpEnabled ? "自动(DHCP)" : "静态";
            }
            catch { }

            list.Add(new AdapterInfo
            {
                Name = ni.Name,
                Desc = ni.Description,
                Type = type,
                Up = ni.OperationalStatus == OperationalStatus.Up,
                Mac = mac,
                IPv4 = uni?.Address.ToString() ?? "—",
                Mask = uni?.IPv4Mask?.ToString() ?? "—",
                IPv6 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)?.Address.ToString() ?? "—",
                Gateway = gw?.Address.ToString() ?? "—",
                Dns = dns.Count > 0 ? string.Join(", ", dns) : "—",
                Dhcp = dhcp,
                Speed = ni.Speed > 0 ? (ni.Speed / 1_000_000) + " Mbps" : "—",
            });
        }
        return list;
    }

    // ---------- 分层连通性 ----------
    public static async Task<(List<Check> checks, string gateway)> Connectivity()
    {
        var checks = new List<Check>();
        string gateway = "";

        // 1) 回环
        checks.Add(new Check
        {
            Group = "连通性", Name = "本机协议栈 (127.0.0.1)",
            Level = Sev.Ok, Detail = "TCP/IP 协议栈可正常收发"
        });

        // 2) 默认网关
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var g = ni.GetIPProperties().GatewayAddresses
                      .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork
                                        && !x.Address.ToString().StartsWith("0."));
            if (g != null) { gateway = g.Address.ToString(); break; }
        }

        if (string.IsNullOrEmpty(gateway))
        {
            checks.Add(new Check
            {
                Group = "连通性", Name = "默认网关", Level = Sev.Bad,
                Detail = "没有找到默认网关", Hint = "网线/无线未连接，或 IP 未获取到"
            });
        }
        else
        {
            var (ok, ms) = await PingAsync(gateway, 1500);
            checks.Add(new Check
            {
                Group = "连通性", Name = $"默认网关 ({gateway})",
                Level = ok ? (ms < 20 ? Sev.Ok : Sev.Warn) : Sev.Bad,
                Detail = ok ? $"{ms} ms" : "无法连通",
                Hint = ok ? "" : "内网不通：检查网线、路由器、IP/掩码/网关配置"
            });
        }

        // 3) 外网（多目标，避免单点 ICMP 被封误判）
        string[] targets = { "223.5.5.5", "114.114.114.114", "119.29.29.29" };
        double best = -1; string bestIp = "";
        foreach (var t in targets)
        {
            var (ok, ms) = await PingAsync(t, 1500);
            if (ok && (best < 0 || ms < best)) { best = ms; bestIp = t; }
        }
        checks.Add(new Check
        {
            Group = "连通性", Name = "外网 IP 可达",
            Level = best >= 0 ? (best < 60 ? Sev.Ok : Sev.Warn) : Sev.Bad,
            Detail = best >= 0 ? $"最快 {bestIp} → {best} ms" : "全部超时",
            Hint = best >= 0 ? "" : "网关通但外网不通：宽带拨号/上级路由/运营商问题"
        });

        // 4) DNS 解析
        var sw = Stopwatch.StartNew();
        bool dnsOk = false; string dnsDetail = "";
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(TestHost);
            sw.Stop();
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            dnsOk = v4 != null;
            dnsDetail = dnsOk ? $"{TestHost} → {v4}  ({sw.ElapsedMilliseconds} ms)"
                              : $"{TestHost} 未返回 IPv4 地址";
        }
        catch (Exception ex)
        {
            sw.Stop();
            dnsDetail = $"解析失败：{ex.Message}";
        }
        checks.Add(new Check
        {
            Group = "连通性", Name = "DNS 域名解析",
            Level = dnsOk ? (sw.ElapsedMilliseconds < 300 ? Sev.Ok : Sev.Warn) : Sev.Bad,
            Detail = dnsDetail,
            Hint = dnsOk ? "" : "能上 IP 不能开网页：DNS 挂了，用【断网修复】或换 DNS"
        });

        // 5) HTTP 出网
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            sw.Restart();
            var resp = await hc.GetAsync("http://www.baidu.com/");
            sw.Stop();
            checks.Add(new Check
            {
                Group = "连通性", Name = "HTTP 出网",
                Level = resp.IsSuccessStatusCode ? Sev.Ok : Sev.Warn,
                Detail = $"HTTP {(int)resp.StatusCode}  ({sw.ElapsedMilliseconds} ms)",
                Hint = resp.IsSuccessStatusCode ? "" : "返回异常状态码，可能被代理/防火墙拦截"
            });
        }
        catch (Exception ex)
        {
            checks.Add(new Check
            {
                Group = "连通性", Name = "HTTP 出网", Level = Sev.Bad,
                Detail = "请求失败：" + ex.Message,
                Hint = "检查系统代理设置 / 防火墙 / 中间设备"
            });
        }

        return (checks, gateway);
    }

    public static async Task<(bool ok, double ms)> PingAsync(string host, int timeout)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, timeout);
            if (r.Status == IPStatus.Success) return (true, r.RoundtripTime);
            return (false, -1);
        }
        catch { return (false, -1); }
    }

    // ---------- DNS 服务器测速 ----------
    public static async Task<DnsTest> TestDnsServer(string server, int timeoutMs = 1500)
    {
        var t = new DnsTest { Server = server };
        try
        {
            var ip = IPAddress.Parse(server);
            var query = BuildDnsQuery(TestHost);

            var sw = Stopwatch.StartNew();
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Connect(ip, 53);
            await udp.SendAsync(query, query.Length);
            var recvTask = udp.ReceiveAsync();
            var done = await Task.WhenAny(recvTask, Task.Delay(timeoutMs));
            if (done != recvTask)
            {
                t.Ok = false; t.Note = "超时无响应"; return t;
            }
            var resp = recvTask.Result.Buffer;
            sw.Stop();
            if (resp.Length < 12) { t.Ok = false; t.Note = "响应报文异常"; return t; }
            int rcode = resp[3] & 0x0F;
            int ancount = (resp[6] << 8) | resp[7];
            if (rcode != 0) { t.Ok = false; t.Note = $"DNS 返回错误码 {rcode}"; return t; }
            if (ancount == 0) { t.Ok = false; t.Note = "无解析结果(可能被劫持)"; return t; }
            t.Ok = true;
            t.Ms = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            t.Ok = false;
            t.Note = ex.Message;
        }
        return t;
    }

    private static byte[] BuildDnsQuery(string domain)
    {
        var ms = new MemoryStream();
        var rnd = (ushort)Random.Shared.Next(1, 0xFFFF);
        ms.WriteByte((byte)(rnd >> 8)); ms.WriteByte((byte)rnd);
        ms.WriteByte(0x01); ms.WriteByte(0x00);   // 标准查询, RD=1
        ms.WriteByte(0x00); ms.WriteByte(0x01);   // QDCOUNT
        ms.WriteByte(0); ms.WriteByte(0);         // ANCOUNT
        ms.WriteByte(0); ms.WriteByte(0);         // NSCOUNT
        ms.WriteByte(0); ms.WriteByte(0);         // ARCOUNT
        foreach (var part in domain.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(part);
            ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }
        ms.WriteByte(0);
        ms.WriteByte(0x00); ms.WriteByte(0x01);   // TYPE A
        ms.WriteByte(0x00); ms.WriteByte(0x01);   // CLASS IN
        return ms.ToArray();
    }

    public static List<string> DnsServers()
    {
        var set = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var d in ni.GetIPProperties().DnsAddresses)
                if (d.AddressFamily == AddressFamily.InterNetwork && !set.Contains(d.ToString()))
                    set.Add(d.ToString());
        }
        return set;
    }

    // ---------- 系统代理 ----------
    public static (bool enabled, string server, string pac) SystemProxy()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k == null) return (false, "", "");
            int en = (int)(k.GetValue("ProxyEnable") ?? 0);
            string srv = k.GetValue("ProxyServer")?.ToString() ?? "";
            string pac = k.GetValue("AutoConfigURL")?.ToString() ?? "";
            return (en != 0, srv, pac);
        }
        catch { return (false, "", ""); }
    }

    public static async Task<string> WinHttpProxyAsync()
    {
        var r = await Cmd.Netsh("winhttp show proxy", 15000);
        var line = r.All.Split('\n')
                        .Select(x => x.Trim())
                        .FirstOrDefault(x => x.Contains("代理服务器") || x.Contains("Proxy Server"));
        return line ?? r.All.Replace("\r", "").Replace("\n", " ").Trim();
    }

    /// <summary>解析 netsh winhttp show proxy 的输出。返回 (是否配置了代理, 代理地址)</summary>
    public static (bool enabled, string server) ParseWinHttp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, "");
        var lines = raw.Replace("\r", "").Split('\n')
                       .Select(l => l.Trim())
                       .Where(l => l.Length > 0)
                       .ToList();

        bool direct = lines.Any(l => l.Contains("直接访问") || l.Contains("Direct access")
                                  || l.Contains("无需代理") || l.Contains("no proxy"));
        if (direct) return (false, "");

        // 形如 "代理服务器    : 127.0.0.1:7897"
        foreach (var l in lines)
        {
            if (l.Contains("代理服务器") || l.Contains("Proxy Server"))
            {
                int i = l.IndexOf(':');
                if (i < 0) i = l.IndexOf('：');
                if (i >= 0)
                {
                    var v = l.Substring(i + 1).Trim();
                    if (v.Length > 0 && !v.Contains("设置"))
                        return (true, v);
                }
            }
        }
        return (false, "");
    }

    /// <summary>
    /// 检查「系统代理指向本机、但代理软件没开」这一最常见的"假断网"。
    /// 返回 (是否有问题, 说明)
    /// </summary>
    public static async Task<(bool bad, string detail)> ProbeLocalProxy()
    {
        var (en, srv, pac) = SystemProxy();
        if (!en || string.IsNullOrWhiteSpace(srv))
            return (false, "未启用系统代理");

        // 解析 host:port
        string host = srv, portStr = "";
        int colon = srv.LastIndexOf(':');
        if (colon > 0 && !srv.Contains("="))     // 形如 127.0.0.1:7897
        {
            host = srv.Substring(0, colon).Trim();
            portStr = srv.Substring(colon + 1).Trim();
        }
        else if (srv.Contains("="))              // 形如 http=127.0.0.1:7897;https=...
        {
            var part = srv.Split(';').FirstOrDefault(p => p.Contains("="));
            if (part != null)
            {
                var v = part.Split('=')[1];
                int c2 = v.LastIndexOf(':');
                if (c2 > 0) { host = v.Substring(0, c2); portStr = v.Substring(c2 + 1); }
                else host = v;
            }
        }

        bool isLocal = host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("::1");

        if (!isLocal)
            return (false, $"代理指向 {srv}（非本机，属正常配置）");

        if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
            return (true, $"代理指向本机 {srv}，端口解析异常 —— 该端口不通时全网都无法访问");

        // 探测本机代理端口是否真的在监听
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(task, Task.Delay(800));
            if (done == task && tcp.Connected)
                return (false, $"代理 127.0.0.1:{port} 正在监听（代理软件已开启）");
            return (true, $"代理软件未运行！系统代理仍指向 127.0.0.1:{port}，端口无响应 —— 这就是「全网打不开」的原因，建议一键关闭系统代理");
        }
        catch (Exception ex)
        {
            return (true, $"代理端口 127.0.0.1:{port} 探测失败（{ex.Message}）—— 建议关闭系统代理");
        }
    }

    // ---------- 其它静态检查 ----------
    public static async Task<List<Check>> MiscAsync(string gateway)
    {
        var list = new List<Check>();

        // hosts 文件
        try
        {
            string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                        @"drivers\etc\hosts");
            if (!File.Exists(hosts))
                list.Add(new Check { Group = "系统", Name = "hosts 文件", Level = Sev.Warn, Detail = "文件不存在" });
            else
            {
                var lines = File.ReadAllLines(hosts)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .ToList();
                bool bad = lines.Any(l =>
                {
                    var f = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    return f.Length >= 2 && (f[1].Contains("baidu") || f[1].Contains("qq.com") || f[1].Contains("microsoft"));
                });
                list.Add(new Check
                {
                    Group = "系统", Name = "hosts 文件",
                    Level = bad ? Sev.Warn : Sev.Ok,
                    Detail = lines.Count == 0 ? "无自定义条目（正常）" : $"{lines.Count} 条自定义解析" + (bad ? "，含常见站点劫持" : ""),
                    Hint = bad ? "hosts 里有异常条目，建议备份并还原" : ""
                });
            }
        }
        catch (Exception ex)
        {
            list.Add(new Check { Group = "系统", Name = "hosts 文件", Level = Sev.Info, Detail = "读取失败：" + ex.Message });
        }

        // Winsock 目录
        var ws = (await Cmd.Netsh("winsock show catalog", 30000)).All;
        int lsp = ws.Split('\n').Count(l => l.Contains("条目类型") || l.Contains("Entry Type"));
        list.Add(new Check
        {
            Group = "系统", Name = "Winsock 目录",
            Level = Sev.Ok,
            Detail = lsp > 0 ? $"已加载 {lsp} 个条目" : "读取正常",
            Hint = "若浏览器全部打不开，可尝试 netsh winsock reset"
        });

        // 关键服务
        string[] svcs = { "Dhcp", "Dnscache", "NlaSvc", "netprofm", "WlanSvc", "WinHttpAutoProxySvc" };
        var stopped = new List<string>();
        foreach (var s in svcs)
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(s);
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running
                    && sc.Status != System.ServiceProcess.ServiceControllerStatus.StartPending)
                    stopped.Add(s);
            }
            catch { }
        }
        list.Add(new Check
        {
            Group = "系统", Name = "网络相关服务",
            Level = stopped.Count == 0 ? Sev.Ok : Sev.Warn,
            Detail = stopped.Count == 0 ? "全部正常运行" : "未运行：" + string.Join(", ", stopped),
            Hint = stopped.Count == 0 ? "" : "用【断网修复】一键拉起"
        });

        // 防火墙
        var fw = (await Cmd.Netsh("advfirewall show allprofiles state", 20000)).All;
        int on = fw.Split('\n').Count(l => l.Contains("ON") || l.Contains("启用"));
        list.Add(new Check
        {
            Group = "系统", Name = "防火墙状态",
            Level = Sev.Ok, Detail = on > 0 ? $"已启用（{on} 个配置文件）" : "部分或全部关闭",
            Hint = "若网页异常可临时关闭防火墙排查，但不要长期关闭"
        });

        // 路由冲突
        var rt = (await Cmd.RunAsync("route", "print -4", 20000)).All;
        var defs = rt.Split('\n')
                     .Where(l => l.Trim().StartsWith("0.0.0.0"))
                     .Select(l => l.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                     .Where(a => a.Length >= 3)
                     .Select(a => a[2])
                     .Distinct().ToList();
        list.Add(new Check
        {
            Group = "系统", Name = "默认路由",
            Level = defs.Count > 1 ? Sev.Warn : Sev.Ok,
            Detail = defs.Count == 0 ? "无默认路由" : string.Join(" / ", defs),
            Hint = defs.Count > 1 ? "存在多条默认路由，可能造成时通时断" : ""
        });

        return list;
    }
}
