using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class DiagView : Panel
{
    private readonly MainForm _main;
    private readonly CheckList _checks = new();
    private readonly DataGridView _gridAd = new();
    private readonly DataGridView _gridDns = new();
    private readonly TextBox _txtSys = new();
    private readonly Label _summary;
    private readonly TabControl _tabs = new();
    private DiagReport _last;

    public DiagView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        // ---------- 顶部 ----------
        var head = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Transparent };

        // 用 TableLayoutPanel 分三列：标题(定宽) | 摘要(填满) | 按钮(自动宽)。
        // 这样可以彻底避开 Dock 顺序与手工坐标的问题 —— 之前两版分别因为
        // 「head.Width 构造期不稳定」和「Fill 抢走整行宽度」而失败。
        var headTbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        headTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
        headTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headTbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headTbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var t = Theme.Lbl("网络检测", 15f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Fill;
        t.TextAlign = ContentAlignment.MiddleLeft;
        t.Margin = new Padding(0);

        _summary = Theme.Lbl("尚未检测", 9.5f, Theme.SubText);
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Margin = new Padding(6, 0, 6, 0);
        _summary.AutoEllipsis = true;      // 文字过长时省略，而不是溢出压住按钮

        // 右侧按钮条：AutoSize 让它自己撑开，不会被父容器裁切
        var rightBar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0, 6, 0, 0),
        };

        var bRun = Theme.Btn("开始全面检测", 130, 34, true);
        bRun.Margin = new Padding(0, 0, 10, 0);
        bRun.Click += async (_, _) => await RunFullAsync(false);

        var bExp = Theme.Btn("导出报告", 100, 34);
        bExp.Margin = new Padding(0, 0, 10, 0);
        bExp.Click += (_, _) => ExportReport();

        var bCopy = Theme.Btn("复制摘要", 100, 34);
        bCopy.Margin = new Padding(0, 0, 10, 0);
        bCopy.Click += (_, _) => CopySummary();

        var bFix = Theme.Btn("去修复", 90, 34);
        bFix.Margin = new Padding(0);
        bFix.Click += (_, _) => _main.ShowView("repair");

        rightBar.Controls.Add(bFix);
        rightBar.Controls.Add(bCopy);
        rightBar.Controls.Add(bExp);
        rightBar.Controls.Add(bRun);

        headTbl.Controls.Add(t, 0, 0);
        headTbl.Controls.Add(_summary, 1, 0);
        headTbl.Controls.Add(rightBar, 2, 0);
        head.Controls.Add(headTbl);

        // ---------- 下方标签页 ----------
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.F(9f);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        // 标签宽度按可用宽度分配（4 个 × 120 = 480px，最小窗口时可用约 620px 尚可，
        // 但统一按可用宽度算，避免以后加标签又溢出）
        _tabs.DrawItem += Tabs_DrawItem;
        // 标签宽度按可用宽度均分：固定宽度在窄窗口下会把末尾标签压扁。
        //
        // ⚠ 改 ItemSize 会让 TabControl 重新布局并再次触发 Resize，
        //   在 Resize 处理器里直接改就形成无限递归 —— 实测直接把进程
        //   打成「无法创建新的堆栈防护页面」（栈溢出 0xC0000409）。
        //   所以：加进行中标志切断递归，并且只在控件已创建时异步改。
        bool tabSizing = false;
        void LayoutTabs()
        {
            if (tabSizing || !_tabs.IsHandleCreated) return;
            int n = Math.Max(1, _tabs.TabPages.Count);
            int avail = _tabs.ClientSize.Width - 8;
            if (avail < 100) return;
            int w = Math.Max(70, avail / n);
            if (_tabs.ItemSize.Width == w) return;

            tabSizing = true;
            try { _tabs.ItemSize = new Size(w, 30); }
            finally { tabSizing = false; }
        }
        _tabs.Resize += (_, _) =>
        {
            // 必须先确认句柄已创建：TabControl 在构造期（加入父容器时）就会触发
            // Resize，此时还没有窗口句柄，直接 BeginInvoke 会抛
            // 「在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke」。
            // 用 _tabs.IsHandleCreated 而不是 this.IsHandleCreated —— 后者可能已创建，
            // 而 _tabs 自己的句柄还没建好。
            if (tabSizing || !_tabs.IsHandleCreated) return;
            _tabs.BeginInvoke(new Action(LayoutTabs));
        };
        this.Resize += (_, _) => { if (IsHandleCreated && !tabSizing) BeginInvoke(new Action(LayoutTabs)); };
        HandleCreated += (_, _) => BeginInvoke(new Action(LayoutTabs));

        // Tab1: 检测结果
        _checks.Dock = DockStyle.Fill;
        var p1 = new Panel { BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0), Dock = DockStyle.Fill };
        p1.Controls.Add(_checks);

        // Tab2: 网卡
        Theme.Grid(_gridAd);
        _gridAd.Columns.Add("name", "网卡");
        _gridAd.Columns.Add("type", "类型");
        _gridAd.Columns.Add("desc", "描述");
        _gridAd.Columns.Add("ip", "IPv4");
        _gridAd.Columns.Add("mask", "掩码");
        _gridAd.Columns.Add("gw", "网关");
        _gridAd.Columns.Add("dns", "DNS");
        _gridAd.Columns.Add("dhcp", "获取方式");
        _gridAd.Columns.Add("speed", "速率");
        _gridAd.Columns.Add("mac", "MAC");
        _gridAd.Columns["desc"].FillWeight = 150;
        _gridAd.Columns["mac"].FillWeight = 90;
        var p2 = new Panel { BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0), Dock = DockStyle.Fill };
        _gridAd.Dock = DockStyle.Fill;
        p2.Controls.Add(_gridAd);

        // Tab3: DNS
        Theme.Grid(_gridDns);
        _gridDns.Columns.Add("srv", "DNS 服务器");
        _gridDns.Columns.Add("ms", "响应时间");
        _gridDns.Columns.Add("st", "状态");
        _gridDns.Columns.Add("note", "说明");
        var p3 = new Panel { BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0), Dock = DockStyle.Fill };
        _gridDns.Dock = DockStyle.Fill;
        p3.Controls.Add(_gridDns);

        // Tab4: 系统信息
        Theme.StyleOutput(_txtSys);
        var p4 = new Panel { BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0), Dock = DockStyle.Fill };
        _txtSys.Dock = DockStyle.Fill;
        p4.Controls.Add(_txtSys);

        var t1 = new TabPage("检测结果") { BackColor = Theme.Bg, Padding = new Padding(0) };
        var t2 = new TabPage("网卡信息") { BackColor = Theme.Bg, Padding = new Padding(0) };
        var t3 = new TabPage("DNS 实测") { BackColor = Theme.Bg, Padding = new Padding(0) };
        var t4 = new TabPage("原始信息") { BackColor = Theme.Bg, Padding = new Padding(0) };
        _tabs.TabPages.AddRange(new[] { t1, t2, t3, t4 });
        t1.Controls.Add(p1);
        t2.Controls.Add(p2);
        t3.Controls.Add(p3);
        t4.Controls.Add(p4);

        Controls.Add(_tabs);
        Controls.Add(head);

        // 首次显示时刷新网卡列表
        VisibleChanged += (_, _) => { if (Visible) RefreshAdapters(); };
    }

    private void Tabs_DrawItem(object sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        bool sel = e.Index == _tabs.SelectedIndex;
        var rect = e.Bounds;
        using (var br = new SolidBrush(sel ? Theme.Panel : Theme.Bg))
            g.FillRectangle(br, rect);
        if (sel)
            using (var br = new SolidBrush(Theme.Accent))
                g.FillRectangle(br, rect.X, rect.Bottom - 3, rect.Width, 3);
        TextRenderer.DrawText(g, _tabs.TabPages[e.Index].Text, Theme.F(9f, sel ? FontStyle.Bold : FontStyle.Regular),
            rect, sel ? Theme.Text : Theme.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    public void RefreshAdapters()
    {
        var list = NetworkDiag.Adapters();
        _gridAd.Rows.Clear();
        foreach (var a in list)
        {
            int i = _gridAd.Rows.Add(
                (a.Up ? "● " : "○ ") + a.Name, a.Type, a.Desc, a.IPv4, a.Mask,
                a.Gateway, a.Dns, a.Dhcp, a.Speed, a.Mac);
            _gridAd.Rows[i].Cells[0].Style.ForeColor = a.Up ? Theme.Ok : Theme.Idle;
        }
    }

    // -------------------------------------------------------------
    public async Task RunFullAsync(bool fromHome)
    {
        _main.SetBusy(true, "正在检测…");
        _checks.Clear();
        _gridDns.Rows.Clear();
        var report = new DiagReport();
        var sb = new StringBuilder();

        try
        {
            Log.Info("──────── 开始全面网络检测 ────────");

            // 网卡：只挑真正配了 IPv4 地址的物理/无线网卡，避免过滤层与空网卡干扰
            Log.Step("枚举网络适配器");
            report.Adapters.AddRange(NetworkDiag.Adapters());
            RefreshAdapters();
            var ups = report.Adapters
                .Where(a => a.Up && (a.Type == "有线" || a.Type == "无线"))
                .Where(a => !string.IsNullOrEmpty(a.IPv4) && a.IPv4 != "—")
                .ToList();
            report.Checks.Add(new Check
            {
                Group = "网卡",
                Name = "活动网卡",
                Level = ups.Count > 0 ? Sev.Ok : Sev.Bad,
                Detail = ups.Count > 0
                    ? string.Join(" / ", ups.Select(a => $"{a.Name}({a.IPv4})"))
                    : "没有已连接的物理网卡",
                Hint = ups.Count > 0 ? "" : "检查网线/无线开关，或网卡被禁用"
            });
            foreach (var a in ups) ApplyCheck(report, new Check
            {
                Group = "网卡", Name = $"「{a.Name}」地址配置",
                Level = a.IPv4.StartsWith("169.254.") ? Sev.Bad : Sev.Ok,
                Detail = $"{a.IPv4} / {a.Mask}  网关 {a.Gateway}  {a.Dhcp}",
                Hint = a.IPv4.StartsWith("169.254.") ? "169.254 是无效地址，说明没拿到 DHCP，需重新获取 IP" : ""
            });

            // 分层连通性
            var (conn, gw) = await NetworkDiag.Connectivity();
            report.Gateway = gw;
            foreach (var c in conn) ApplyCheck(report, c);

            // DNS 实测
            Log.Step("逐个实测 DNS 服务器响应速度");
            var servers = NetworkDiag.DnsServers();
            if (servers.Count == 0)
            {
                ApplyCheck(report, new Check
                {
                    Group = "DNS", Name = "DNS 服务器", Level = Sev.Bad,
                    Detail = "网卡上未配置任何 DNS", Hint = "用【网络优化】一键优选 DNS"
                });
            }
            else
            {
                foreach (var s in servers)
                {
                    var r = await NetworkDiag.TestDnsServer(s);
                    report.Dns.Add(r);
                    int i = _gridDns.Rows.Add(s,
                        r.Ok ? $"{r.Ms:F0} ms" : "—",
                        r.Ok ? (r.Ms < 60 ? "优秀" : r.Ms < 200 ? "正常" : "偏慢") : "无响应",
                        r.Note);
                    var col = !r.Ok ? Theme.Bad : r.Ms < 60 ? Theme.Ok : r.Ms < 200 ? Theme.Warn : Theme.Warn;
                    _gridDns.Rows[i].Cells[2].Style.ForeColor = col;
                    _gridDns.Rows[i].Cells[1].Style.ForeColor = col;
                }
                var fastest = report.Dns.Where(d => d.Ok).OrderBy(d => d.Ms).FirstOrDefault();
                var slowest = report.Dns.Where(d => !d.Ok).ToList();
                ApplyCheck(report, new Check
                {
                    Group = "DNS", Name = "DNS 可用性",
                    Level = slowest.Count == 0 ? Sev.Ok : (report.Dns.Any(d => d.Ok) ? Sev.Warn : Sev.Bad),
                    Detail = slowest.Count == 0
                        ? $"全部可解析，最快 {fastest?.Server} ({fastest?.Ms:F0} ms)"
                        : $"无响应：{string.Join(", ", slowest.Select(x => x.Server))}",
                    Hint = slowest.Count == 0 ? "" : "存在失效 DNS，会拖慢上网，建议改用优选 DNS"
                });
            }

            // 系统代理
            Log.Step("检查系统代理设置");
            var (en, srv2, pac) = NetworkDiag.SystemProxy();
            report.WebProxy = en ? srv2 : "";
            var probe = await NetworkDiag.ProbeLocalProxy();
            ApplyCheck(report, new Check
            {
                Group = "系统", Name = "系统代理 (WinINet)",
                Level = probe.bad ? Sev.Bad : (en ? Sev.Warn : Sev.Ok),
                Detail = en ? $"已启用 → {srv2}" : "未启用",
                Hint = probe.bad ? probe.detail
                     : (en ? "启用了代理；若上网异常可尝试关闭" : "")
            });

            var wh = await NetworkDiag.WinHttpProxyAsync();
            report.WinHttpProxy = wh;
            bool whDirect = wh.Contains("直接访问") || wh.Contains("Direct access")
                         || wh.Contains("无需代理") || wh.Contains("no proxy");
            var (whEnabled, whServer) = NetworkDiag.ParseWinHttp(wh);
            string whDetail = whDirect ? "直接访问（无代理，正常）"
                            : whEnabled ? $"已配置代理 → {whServer}"
                            : (string.IsNullOrWhiteSpace(wh) ? "读取失败" : wh);
            ApplyCheck(report, new Check
            {
                Group = "系统", Name = "WinHTTP 代理",
                Level = (whDirect || !whEnabled) ? Sev.Ok : Sev.Warn,
                Detail = whDetail,
                Hint = whEnabled ? "系统级代理（Windows 更新/服务走这里），异常时可用修复功能重置" : ""
            });
            if (!string.IsNullOrEmpty(pac))
                ApplyCheck(report, new Check
                {
                    Group = "系统", Name = "PAC 自动配置脚本", Level = Sev.Warn,
                    Detail = pac, Hint = "PAC 指向的地址失效会导致浏览器打不开网页"
                });

            // 其它
            foreach (var c in await NetworkDiag.MiscAsync(gw)) ApplyCheck(report, c);
            foreach (var c in await NetworkOptimizer.ScanNicPowerAsync()) ApplyCheck(report, c);

            // 原始信息
            sb.AppendLine("================= 原始信息 =================");
            sb.AppendLine("【ipconfig /all】");
            sb.AppendLine((await Cmd.Ipconfig("/all", 30000)).All);
            sb.AppendLine("\n【route print -4】");
            sb.AppendLine((await Cmd.RunAsync("route", "print -4", 20000)).All);
            sb.AppendLine("\n【netsh int tcp show global】");
            sb.AppendLine((await Cmd.Netsh("int tcp show global", 20000)).All);
            sb.AppendLine("\n【netsh winhttp show proxy】");
            sb.AppendLine((await Cmd.Netsh("winhttp show proxy", 20000)).All);
            sb.AppendLine("\n【netsh advfirewall show allprofiles】");
            sb.AppendLine((await Cmd.Netsh("advfirewall show allprofiles", 30000)).All);
            sb.AppendLine("\n【进程网络连接占用 TOP】");
            sb.AppendLine(await NetworkOptimizer.NetTopProcesses());
            _txtSys.Text = sb.ToString();

            _last = report;
            var bad = report.Count(Sev.Bad);
            var warn = report.Count(Sev.Warn);
            _summary.Text = $"检测完成 · 正常 {report.Count(Sev.Ok)} · 注意 {warn} · 异常 {bad} · {report.At:HH:mm:ss}";
            _summary.ForeColor = bad > 0 ? Theme.Bad : warn > 0 ? Theme.Warn : Theme.Ok;

            Log.Ok($"检测完成：正常 {report.Count(Sev.Ok)}，注意 {warn}，异常 {bad}");

            // 结论
            string concl = bad == 0
                ? (warn == 0 ? "网络状态良好，未发现异常。" : "网络基本正常，但有几项建议优化（见上方黄色项）。")
                : "发现异常项，建议切换到【断网修复】一键处理。";
            ApplyCheck(report, new Check { Group = "结论", Name = "总体结论", Level = bad > 0 ? Sev.Bad : warn > 0 ? Sev.Warn : Sev.Ok, Detail = concl });
            Log.Info("结论：" + concl);

            _main.RefreshHome();
        }
        catch (Exception ex)
        {
            Log.Err("检测过程出错：" + ex.Message);
        }
        finally
        {
            _main.SetBusy(false, "检测完成");
        }
    }

    private void ApplyCheck(DiagReport r, Check c)
    {
        r.Checks.Add(c);
        _checks.Add(c);
        switch (c.Level)
        {
            case Sev.Ok: Log.Write($"{c.Name} → {c.Detail}", " OK "); break;
            case Sev.Warn: Log.Write($"{c.Name} → {c.Detail}", "WARN"); break;
            case Sev.Bad: Log.Write($"{c.Name} → {c.Detail}", "FAIL"); break;
            default: Log.Write($"{c.Name} → {c.Detail}", "INFO"); break;
        }
    }

    public string BuildReportText()
    {
        if (_last == null) return "（尚未检测）";
        var sb = new StringBuilder();
        sb.AppendLine("夕颜若雪网络工具 · 网络检测报告");
        sb.AppendLine("时间：" + _last.At.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("汇总：" + _last.Summary);
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("\n【网卡信息】");
        foreach (var a in _last.Adapters)
            sb.AppendLine($"  {(a.Up ? "[已连接]" : "[未连接]")} {a.Name} ({a.Type})  IP={a.IPv4}  掩码={a.Mask}  网关={a.Gateway}  DNS={a.Dns}  {a.Dhcp}  {a.Speed}");
        sb.AppendLine("\n【检测项】");
        foreach (var c in _last.Checks)
            sb.AppendLine($"  [{c.Level,-4}] {c.Name}：{c.Detail}" + (string.IsNullOrEmpty(c.Hint) ? "" : $"   → {c.Hint}"));
        sb.AppendLine("\n【DNS 实测】");
        foreach (var d in _last.Dns)
            sb.AppendLine($"  {d.Server,-18} {(d.Ok ? d.Ms.ToString("F0") + " ms" : "无响应")}  {d.Note}");
        return sb.ToString();
    }

    private void CopySummary()
    {
        try
        {
            Clipboard.SetText(BuildReportText());
            Log.Ok("检测摘要已复制到剪贴板");
        }
        catch (Exception ex) { Log.Err("复制失败：" + ex.Message); }
    }

    private void ExportReport()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, $"检测报告_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(f, BuildReportText() + "\n\n" + _txtSys.Text, Encoding.UTF8);
            Log.Ok("报告已导出：" + f);
            Cmd.Open(dir);
        }
        catch (Exception ex) { Log.Err("导出失败：" + ex.Message); }
    }
}
