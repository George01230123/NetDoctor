using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class MainForm : Form
{
    private readonly Panel _content = new();
    private readonly Panel _side = new();
    private readonly Panel _titleBar = new();
    private readonly Panel _statusBar = new();
    private readonly Label _statusText;
    private readonly ProgressBar _progress;
    private readonly List<NavButton> _navs = new();
    private readonly Dictionary<string, Control> _views = new();
    private readonly bool _admin;

    public DiagView ViewDiag { get; }
    public RepairView ViewRepair { get; }
    public OptimizeView ViewOptimize { get; }
    public WindowsView ViewWindows { get; }
    public SystemToolsView ViewTools { get; }
    public HardwareView ViewHardware { get; }
    public LogView ViewLog { get; }
    public Panel ViewHome { get; }

    public MainForm(bool admin)
    {
        _admin = admin;

        Text = "夕颜若雪网络工具";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.F(9f);
        KeyPreview = true;
        DoubleBuffered = true;

        // 尺寸在 Shown 时按当前屏幕（含 DPI 换算）计算，避免启动阶段 DPI 上下文不准
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(880, 540);
        BackColor = Theme.Bg;
        _pendingView = null;
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized && _views.Count > 0)
                Log.Info($"窗口尺寸变化 → {Width}x{Height}  WindowState={WindowState}");
        };

        // ---------------- 标题栏 ----------------
        _titleBar.Dock = DockStyle.Top;
        _titleBar.Height = 46;
        _titleBar.BackColor = Theme.Panel;
        _titleBar.MouseDown += TitleDrag;
        _titleBar.DoubleClick += (_, _) => WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;

        var logo = new Label
        {
            Text = "◈  夕颜若雪网络工具",
            Font = Theme.F(11f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = false,
            Dock = DockStyle.Left,
            Width = 260,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
        };
        logo.MouseDown += TitleDrag;

        var badge = new Label
        {
            Text = admin ? "管理员" : "受限",
            Font = Theme.F(8.5f, FontStyle.Bold),
            ForeColor = admin ? Theme.Ok : Theme.Warn,
            BackColor = Color.Transparent,
            AutoSize = false,
            Dock = DockStyle.Left,
            Width = 70,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var btnClose = new Button
        {
            Text = "✕", Dock = DockStyle.Right, Width = 46, FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent, ForeColor = Theme.SubText, Font = Theme.F(11f),
            Cursor = Cursors.Hand,
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xC4, 0x2B, 0x2B);
        btnClose.Click += (_, _) => Close();

        var btnMin = new Button
        {
            Text = "—", Dock = DockStyle.Right, Width = 46, FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent, ForeColor = Theme.SubText, Font = Theme.F(10f),
            Cursor = Cursors.Hand,
        };
        btnMin.FlatAppearance.BorderSize = 0;
        btnMin.FlatAppearance.MouseOverBackColor = Theme.CardHover;
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;

        _titleBar.Controls.Add(btnClose);
        _titleBar.Controls.Add(btnMin);
        _titleBar.Controls.Add(badge);
        _titleBar.Controls.Add(logo);

        // ---------------- 侧栏 ----------------
        _side.Dock = DockStyle.Left;
        _side.Width = 214;
        _side.BackColor = Theme.Panel;

        var sideTitle = new Label
        {
            Text = "功  能",
            Font = Theme.F(8.5f, FontStyle.Bold),
            ForeColor = Theme.Idle,
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(20, 8, 0, 0),
        };

        var navDefs = new (string Key, string Text)[]
        {
            ("home",     "  概览"),
            ("diag",     "  网络检测"),
            ("repair",   "  断网修复"),
            ("optimize", "  网络优化"),
            ("windows",  "  Windows优化"),
            ("tools",    "  系统工具"),
            ("hardware", "  硬件检测"),
            ("log",      "  运行日志"),
        };

        // DockStyle.Top 需要倒序添加
        for (int i = navDefs.Length - 1; i >= 0; i--)
        {
            var def = navDefs[i];
            var nb = new NavButton(def.Text);
            nb.Tag = def.Key;
            nb.Click += (_, _) => ShowView(def.Key);
            _navs.Insert(0, nb);
            _side.Controls.Add(nb);
        }
        _side.Controls.Add(sideTitle);

        var sideBottom = new Label
        {
            Text = "v1.4.0\n网络 · Windows 优化\n系统工具 · 硬件检测\n原生接口 DLL",
            Font = Theme.F(8f),
            ForeColor = Theme.Idle,
            Dock = DockStyle.Bottom,
            Height = 56,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(20, 0, 0, 0),
        };
        _side.Controls.Add(sideBottom);

        // ---------------- 内容区 ----------------
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.Padding = new Padding(0);

        // ---------------- 状态栏 ----------------
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = 32;
        _statusBar.BackColor = Theme.Panel;

        _statusText = new Label
        {
            Dock = DockStyle.Fill,
            Text = "就绪",
            Font = Theme.F(8.5f),
            ForeColor = Theme.SubText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Right,
            Width = 180,
            Style = ProgressBarStyle.Continuous,
            Visible = false,
        };

        _statusBar.Controls.Add(_statusText);
        _statusBar.Controls.Add(_progress);

        // ---------------- 视图 ----------------
        Log.Info("正在创建界面…");
        ViewHome = BuildHome();
        Log.Info("  · 概览页 OK");
        ViewDiag = new DiagView(this);
        Log.Info("  · 网络检测页 OK");
        ViewRepair = new RepairView(this);
        Log.Info("  · 断网修复页 OK");
        ViewOptimize = new OptimizeView(this);
        Log.Info("  · 网络优化页 OK");
        ViewWindows = new WindowsView(this);
        Log.Info("  · Windows优化页 OK");
        ViewTools = new SystemToolsView(this);
        Log.Info("  · 系统工具页 OK");
        ViewHardware = new HardwareView(this);
        Log.Info("  · 硬件检测页 OK");
        ViewLog = new LogView(this);
        Log.Info("  · 日志页 OK");

        _views["home"] = ViewHome;
        _views["diag"] = ViewDiag;
        _views["repair"] = ViewRepair;
        _views["optimize"] = ViewOptimize;
        _views["windows"] = ViewWindows;
        _views["tools"] = ViewTools;
        _views["hardware"] = ViewHardware;
        _views["log"] = ViewLog;

        // 所有视图一次性加入并填满内容区，切换时只调可见性 + Z 序。
        // 不能用 Controls.Clear()：那会让 Dock=Fill 的容器失去尺寸，新视图会拿到 0x0。
        foreach (var kv in _views)
        {
            kv.Value.Dock = DockStyle.Fill;
            kv.Value.Visible = false;
            _content.Controls.Add(kv.Value);
        }

        Controls.Add(_content);
        Controls.Add(_side);
        Controls.Add(_statusBar);
        Controls.Add(_titleBar);

        Log.Line += OnLogLine;
        FormClosing += (_, _) => Log.Line -= OnLogLine;

        Resize += (_, _) => { Region?.Dispose(); };
        Paint += MainForm_Paint;
        MouseDown += TitleDrag;

        // 视图切换必须等窗口完成首次布局之后再做
        _pendingView = "home";
        Shown += (_, _) =>
        {
            BeginInvoke(() =>
            {
                FitToScreen();
                ShowView(_pendingView ?? "home");
                Log.Info($"窗口已显示：{Width}x{Height} @({Left},{Top})");
            });
        };

        Log.Info("界面已就绪。");
    }

    private string _pendingView;

    /// <summary>按当前屏幕可用区域自适应窗口尺寸并居中</summary>
    private void FitToScreen()
    {
        try
        {
            var scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            var wa = scr.WorkingArea;
            int w = Math.Min(1200, Math.Max(880, wa.Width - 40));
            int h = Math.Min(780, Math.Max(540, wa.Height - 40));
            ClientSize = new Size(w, h);
            Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);
            Log.Info($"适配屏幕：可用区 {wa.Width}x{wa.Height} @({wa.X},{wa.Y}) → 窗口 {w}x{h}");
        }
        catch (Exception ex) { Log.Warn("适配屏幕失败：" + ex.Message); }
    }

    private void MainForm_Paint(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLogLine(line)); } catch { } return; }
        var t = line;
        if (t.Length > 160) t = t.Substring(0, 160) + "…";
        _statusText.Text = t;
    }

    public void SetBusy(bool busy, string text = null)
    {
        if (InvokeRequired) { BeginInvoke(() => SetBusy(busy, text)); return; }
        _progress.Visible = busy;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _progress.MarqueeAnimationSpeed = busy ? 24 : 0;
        if (!busy) { try { _progress.Value = 0; } catch { } }
        if (text != null) _statusText.Text = text;
    }

    private void TitleDrag(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
    }

    public void ShowView(string key)
    {
        foreach (var nb in _navs)
            nb.Active = (string)nb.Tag == key;

        if (!_views.TryGetValue(key, out var v)) return;

        _content.SuspendLayout();
        foreach (var kv in _views)
            kv.Value.Visible = ReferenceEquals(kv.Value, v);
        _content.ResumeLayout(true);
        v.BringToFront();
        PerformLayout();    }

    // ---------------- 概览页 ----------------
    // 注意 Dock=Top 的堆叠顺序与添加顺序相反：先加的在最下面。
    // 因此这里按「说明 → 按钮 → 状态卡 → 说明文字 → 标题」的顺序添加。
    private Panel BuildHome()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(22, 18, 22, 18), AutoScroll = true };

        // 说明卡（视觉上排最后，所以最先添加）
        var info = new Card { Dock = DockStyle.Top, Height = 196, Accent2 = Theme.Accent };
        var it = Theme.Lbl("使用说明", 10.5f, Theme.Text, FontStyle.Bold);
        it.Dock = DockStyle.Top; it.Height = 24;
        var body = Theme.Lbl(
            "①  网络检测：只读体检，不会修改任何设置。会依次检查网卡、网关、外网、DNS、HTTP 出网，并逐个实测 DNS 服务器速度。\n" +
            "②  断网修复：针对常见故障一键处理 —— 刷新 DNS、重取 IP、重置 Winsock/TCP-IP、清 ARP、关掉坏代理、拉起网络服务、还原 hosts。\n" +
            "      修改前会自动生成状态快照（backup 目录），可随时对照回滚；重置 Winsock / TCP-IP 后建议重启电脑。\n" +
            "③  网络优化：DNS 测速择优一键切换，TCP 参数调优（响应优先 / 吞吐优先），关闭网卡节能防止随机掉线。\n" +
            "④  全部操作都会写入 logs 目录下的日志文件，出问题可直接把日志发出来定位。",
            9f, Theme.SubText);
        body.Dock = DockStyle.Fill;
        info.Controls.Add(body);
        info.Controls.Add(it);
        root.Controls.Add(info);

        // 快捷按钮
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 104, BackColor = Color.Transparent };
        var b1 = Theme.Btn("⚡  一键网络体检", 170, 38, primary: true);
        b1.Click += async (_, _) => { ShowView("diag"); await ViewDiag.RunFullAsync(fromHome: true); };
        var b2 = Theme.Btn("🛠  一键断网修复", 170, 38);
        b2.Click += (_, _) => { ShowView("repair"); ViewRepair.RunQuickFix(); };
        var b3 = Theme.Btn("🚀  DNS 择优加速", 170, 38);
        b3.Click += (_, _) => { ShowView("optimize"); ViewOptimize.RunDnsBenchmark(); };
        var b5 = Theme.Btn("🧰  Windows 优化", 170, 38);
        b5.Click += (_, _) => ShowView("windows");
        var b7 = Theme.Btn("🗂  系统工具（清理/应用/服务）", 240, 38);
        b7.Click += (_, _) => ShowView("tools");
        var b8 = Theme.Btn("🖥  硬件检测 + 工具启动器", 220, 38);
        b8.Click += (_, _) => ShowView("hardware");
        var b6 = Theme.Btn("🧹  Windows 全推荐项", 180, 38);
        b6.Click += (_, _) => { ShowView("windows"); ViewWindows.SelectRecommendedPublic(); };
        var b4 = Theme.Btn("📄  打开日志", 130, 38);
        b4.Click += (_, _) => Cmd.Open(Log.FilePath);
        flow.Controls.Add(b1); flow.Controls.Add(b2); flow.Controls.Add(b3);
        flow.Controls.Add(b5); flow.Controls.Add(b6); flow.Controls.Add(b7); flow.Controls.Add(b8); flow.Controls.Add(b4);
        root.Controls.Add(flow);

        var gap2 = new Panel { Dock = DockStyle.Top, Height = 16 };
        root.Controls.Add(gap2);

        // 四张状态卡
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 96, ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
        };
        for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var (cardAd, _lblAd) = MakeStatCard("网卡", "—", "已连接");
        var (cardGw, _lblGw) = MakeStatCard("默认网关", "—", "自动识别");
        var (cardDns, _lblDns) = MakeStatCard("DNS", "—", "解析测速");
        var (cardPr, _lblPr) = MakeStatCard("系统代理", "—", "拦截检测");
        _homeAd = _lblAd; _homeGw = _lblGw; _homeDns = _lblDns; _homePr = _lblPr;

        cards.Controls.Add(cardAd, 0, 0);
        cards.Controls.Add(cardGw, 1, 0);
        cards.Controls.Add(cardDns, 2, 0);
        cards.Controls.Add(cardPr, 3, 0);
        root.Controls.Add(cards);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 14 };
        root.Controls.Add(spacer);

        var desc = Theme.Lbl("一键体检本机网络状态，自动定位断网原因，并提供针对性修复。", 9.5f, Theme.SubText);
        desc.Dock = DockStyle.Top; desc.Height = 24;
        root.Controls.Add(desc);

        var title = Theme.Lbl("网络概览", 15f, Theme.Text, FontStyle.Bold);
        title.Dock = DockStyle.Top; title.Height = 38;
        root.Controls.Add(title);

        // 概览页数据刷新
        Load += (_, _) => RefreshHome();
        return root;
    }

    private Label _homeAd, _homeGw, _homeDns, _homePr;

    public void RefreshHome()
    {
        try
        {
            var ads = NetworkDiag.Adapters().Where(a => a.Up).ToList();
            _homeAd.Text = ads.Count == 0 ? "无" : ads.Count.ToString();
            _homeAd.ForeColor = ads.Count == 0 ? Theme.Bad : Theme.Ok;

            string gw = "—";
            foreach (var a in ads) if (a.Gateway != "—") { gw = a.Gateway; break; }
            _homeGw.Text = gw;

            var dns = NetworkDiag.DnsServers();
            _homeDns.Text = dns.Count == 0 ? "未配置" : string.Join(" / ", dns.Take(2));
            _homeDns.ForeColor = dns.Count == 0 ? Theme.Warn : Theme.Ok;

            var (en, srv, pac) = NetworkDiag.SystemProxy();
            if (en && !string.IsNullOrEmpty(srv)) { _homePr.Text = srv; _homePr.ForeColor = Theme.Warn; }
            else if (!string.IsNullOrEmpty(pac)) { _homePr.Text = "PAC"; _homePr.ForeColor = Theme.Warn; }
            else { _homePr.Text = "未启用"; _homePr.ForeColor = Theme.Ok; }
        }
        catch (Exception ex) { Log.Warn("概览刷新失败：" + ex.Message); }
    }

    private (Card, Label) MakeStatCard(string title, string value, string sub)
    {
        var c = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Accent2 = Theme.Accent };
        var v = Theme.Lbl(value, 16f, Theme.Text, FontStyle.Bold);
        v.Dock = DockStyle.Fill;
        var t = Theme.Lbl(title, 8.5f, Theme.Idle);
        t.Dock = DockStyle.Top; t.Height = 20;
        var s = Theme.Lbl(sub, 8f, Theme.SubText);
        s.Dock = DockStyle.Bottom; s.Height = 18;
        c.Controls.Add(v);
        c.Controls.Add(s);
        c.Controls.Add(t);
        return (c, v);
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
