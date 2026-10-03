using System.Drawing;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class RepairView : Panel
{
    private readonly MainForm _main;
    private readonly TextBox _log = new();
    private readonly Label _title;
    private readonly List<CheckBox> _itemBoxes = new();
    private readonly Dictionary<string, Func<Task<bool>>> _itemActions = new();
    private readonly List<CheckBox> _fixBoxes = new();
    private readonly Dictionary<string, Func<Task>> _fixActions = new();
    private bool _busy;

    public RepairView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        _title = Theme.Lbl("断网修复", 15f, Theme.Text, FontStyle.Bold);
        _title.Dock = DockStyle.Top;
        _title.Height = 40;

        var desc = Theme.Lbl("按需勾选修复项；「一键断网修复」覆盖最常见的断网原因，安全无副作用。「深度修复」会重置协议栈，需要重启。", 9f, Theme.SubText);
        desc.Dock = DockStyle.Top;
        desc.Height = 24;

        // ---------- 日志（先加，占据剩余空间） ----------
        var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), Accent2 = Theme.Accent };
        var logTitle = Theme.Lbl("修复过程", 9.5f, Theme.Text, FontStyle.Bold);
        logTitle.Dock = DockStyle.Top; logTitle.Height = 24;
        Theme.StyleOutput(_log);
        _log.Dock = DockStyle.Fill;
        logCard.Controls.Add(_log);
        logCard.Controls.Add(logTitle);

        // ---------- 操作按钮行 ----------
        var btns = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Color.Transparent };
        var bQuick = Theme.Btn("一键断网修复", 150, 40, true);
        bQuick.Location = new Point(0, 6);
        bQuick.Click += (_, _) => RunQuickFix();
        var bSel = Theme.Btn("执行勾选项", 130, 40);
        bSel.Location = new Point(162, 6);
        bSel.Click += (_, _) => RunSelected();
        var bDeep = Theme.Btn("深度修复（需重启）", 160, 40);
        bDeep.Location = new Point(304, 6);
        bDeep.Click += (_, _) => RunDeepFix();
        var bLog = Theme.Btn("打开日志文件", 130, 40);
        bLog.Location = new Point(476, 6);
        bLog.Click += (_, _) => Cmd.Open(Log.FilePath);
        var bBackup = Theme.Btn("打开备份目录", 130, 40);
        bBackup.Location = new Point(618, 6);
        bBackup.Click += (_, _) => Cmd.Open(Backup.Root);
        btns.Controls.Add(bQuick); btns.Controls.Add(bSel); btns.Controls.Add(bDeep);
        btns.Controls.Add(bLog); btns.Controls.Add(bBackup);

        // ---------- 勾选项 ----------
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
        var optCard = BuildOptionCard();
        var fixCard = BuildFixCard();
        var warnCard = BuildWarnCard();

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Bg,
        };
        stack.Controls.Add(warnCard);
        stack.Controls.Add(optCard);
        stack.Controls.Add(fixCard);
        stack.Resize += (_, _) =>
        {
            // 减去垂直滚动条宽度，避免卡片右边缘被滚动条覆盖
            int w = Math.Max(200, stack.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            warnCard.Width = w; optCard.Width = w; fixCard.Width = w;
        };

        body.Controls.Add(stack);

        Controls.Add(body);
        Controls.Add(btns);
        Controls.Add(logCard);
        Controls.Add(desc);
        Controls.Add(_title);

        Log.Line += OnLogLine;
        HandleDestroyed += (_, _) => Log.Line -= OnLogLine;

        Log.Info("断网修复页已就绪。所有修改前都会自动写入 backup\\backup.ini 快照。");
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLogLine(line)); } catch { } return; }
        // 只显示本次会话中与修复相关的行，避免刷屏过多
        _log.AppendText(line + Environment.NewLine);
    }

    private Control BuildWarnCard()
    {
        var c = new Card { Height = 74, Accent2 = Theme.Warn, Margin = new Padding(0, 0, 0, 10) };
        var t = Theme.Lbl("说明", 9.5f, Theme.Warn, FontStyle.Bold);
        t.Dock = DockStyle.Top; t.Height = 22;
        var b = Theme.Lbl(
            "• 重置 Winsock / TCP-IP 后必须重启电脑才完全生效，重启前网络可能不稳定。\n" +
            "• 重新获取 IP 会短暂断开 3~10 秒；「重置防火墙」会删除自定义规则。",
            8.5f, Theme.SubText);
        b.Dock = DockStyle.Fill;
        c.Controls.Add(b); c.Controls.Add(t);
        return c;
    }

    private Card BuildOptionCard()
    {
        var c = new Card { Height = 268, Accent2 = Theme.Accent, Margin = new Padding(0, 0, 0, 10) };
        var t = Theme.Lbl("修复项（可单独勾选执行）", 10f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Top; t.Height = 24;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            BackColor = Color.Transparent,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));

        void AddItem(string key, string text, Func<Task<bool>> act, bool @default = true)
        {
            var cb = Theme.Chk(text, @default);
            cb.Dock = DockStyle.Fill;
            cb.Padding = new Padding(6, 0, 0, 0);
            _itemBoxes.Add(cb);
            _itemActions[key] = act;
            cb.Tag = key;
            int idx = _itemActions.Count - 1;
            grid.Controls.Add(cb, idx % 2, idx / 2);
        }

        AddItem("dns", "刷新 DNS 缓存 + 重新注册", NetworkRepair.FlushDns);
        AddItem("ip", "重新获取 IP 地址（DHCP）", NetworkRepair.RenewDhcp);
        AddItem("arp", "清空 ARP / 邻居缓存", NetworkRepair.ClearArp);
        AddItem("proxy", "关闭并重置系统代理", NetworkRepair.ClearProxy);
        AddItem("svc", "修复网络相关服务（DHCP/DNS/NLA 等）", NetworkRepair.FixServices);
        AddItem("169", "修复 169.254 无效地址", NetworkRepair.FixConflictIp);
        AddItem("hosts", "还原 hosts 文件为默认", NetworkRepair.FixHosts);
        AddItem("winsock", "重置 Winsock 目录（需重启）", NetworkRepair.ResetWinsock);
        AddItem("tcpip", "重置 TCP/IP 协议栈（需重启）", NetworkRepair.ResetTcpIp);
        AddItem("fw", "重置防火墙为默认（慎用）", NetworkRepair.ResetFirewall, false);

        c.Controls.Add(grid);
        c.Controls.Add(t);
        return c;
    }

    private Card BuildFixCard()
    {
        // 高度要放得下 5 行「立即处理」。原来 214 → 内容区约 170px，
        // 而 5 行 × 40px = 200px，最后一行会被切掉，且这里 AutoScroll=false，
        // 切掉就真的看不到了。
        var c = new Card { Height = 256, Accent2 = Theme.Ok, Margin = new Padding(0, 0, 0, 10) };
        var t = Theme.Lbl("快速处置（针对具体症状，点一下立刻执行）", 10f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Top; t.Height = 24;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Color.Transparent,
        };

        // 每行原来写死 900px 宽、提示标签写死 700px，
        // 而这一列可用宽度只有 876px（默认窗口）到 576px（最小窗口）——
        // 行会横向溢出、提示文字被右边缘切掉。
        // 改成在容器尺寸变化时按实际宽度重排。
        var fixRows = new List<Panel>();

        void LayoutFixRows()
        {
            int avail = flow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
            if (avail < 200) return;
            foreach (var row in fixRows)
            {
                row.Width = avail;
                foreach (Control c in row.Controls)
                {
                    if (c is Label lb && lb.Left >= 108)
                    {
                        int w = avail - lb.Left - 8;
                        if (w > 40) lb.Width = w;
                    }
                }
            }
        }
        flow.Resize += (_, _) => LayoutFixRows();
        // 同其它视图：补一次最终布局，避免用到中间宽度
        this.Resize += (_, _) => BeginInvoke(new Action(LayoutFixRows));
        HandleCreated += (_, _) => BeginInvoke(new Action(LayoutFixRows));

        void AddFix(string symptom, string hint, Func<Task> act)
        {
            var row = new Panel { Width = 900, Height = 40, BackColor = Color.Transparent, Margin = new Padding(0) };
            var b = Theme.Btn("立即处理", 96, 30);
            b.Location = new Point(0, 4);
            b.Click += async (_, _) => await Guard(act);
            var s = Theme.Lbl(symptom, 9f, Theme.Text, FontStyle.Bold);
            s.SetBounds(108, 2, 340, 18);
            var h = Theme.Lbl(hint, 8.5f, Theme.SubText);
            h.SetBounds(108, 20, 700, 18);
            row.Controls.Add(b); row.Controls.Add(s); row.Controls.Add(h);
            flow.Controls.Add(row);
            fixRows.Add(row);
            _fixBoxes.Add(new CheckBox());  // 占位，保持计数一致（未使用）
        }

        AddFix("网页打不开 / 能上 QQ 不能上网",
            "DNS 解析异常 → 刷新 DNS 缓存并重新注册", async () => { await NetworkRepair.FlushDnsOnly(); });
        AddFix("代理软件关了以后全网打不开",
            "系统代理仍指向 127.0.0.1 → 清除系统代理与 WinHTTP 代理", async () => { await NetworkRepair.ClearWinHttpProxy(); await NetworkRepair.ClearProxy(); });
        AddFix("右下角显示感叹号 / 无 Internet 访问",
            "网络位置感知服务异常 → 拉起 NlaSvc 等网络服务", NetworkRepair.FixServices);
        AddFix("IP 地址是 169.254.x.x（没拿到地址）",
            "DHCP 获取失败 → 重新获取 IP 地址", async () => { await NetworkRepair.RenewDhcp(); });
        AddFix("改了 hosts 之后某些网站打不开",
            "hosts 被写入劫持条目 → 备份并还原默认 hosts", NetworkRepair.FixHosts);

        c.Controls.Add(flow);
        c.Controls.Add(t);
        return c;
    }

    private async Task Guard(Func<Task> act)
    {
        if (_busy) { Log.Warn("已有修复任务在执行，请稍候"); return; }
        _busy = true;
        _main.SetBusy(true, "正在修复…");
        try
        {
            await Backup.Snapshot("单项修复");
            await act();
        }
        catch (Exception ex) { Log.Err("修复异常：" + ex.Message); }
        finally
        {
            _busy = false;
            _main.SetBusy(false, "修复完成");
            await Task.Delay(800);
        }
    }

    public async void RunQuickFix() => await Guard(NetworkRepair.QuickFix);

    private async void RunDeepFix()
    {
        var ans = MessageBox.Show(
            "深度修复将会：\n\n" +
            "  · 重置 Winsock 目录\n  · 重置 TCP/IP 协议栈\n  · 还原 hosts\n" +
            "  · 清空 ARP、关闭代理、拉起服务、重新获取 IP\n\n" +
            "完成后需要重启电脑。期间网络会中断数次。\n\n确定继续吗？",
            "深度修复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ans != DialogResult.Yes) { Log.Info("用户取消了深度修复"); return; }

        // 重启询问交给界面层（Core 不依赖 WinForms，原生 DLL 走同一份实现但不弹窗）
        await Guard(() => NetworkRepair.DeepFix(
            confirmReboot: () => MessageBox.Show(
                "深度修复已完成。\n\nWinsock 和 TCP/IP 协议栈的重置需要重启电脑才能完全生效。\n是否现在重启？",
                "夕颜若雪网络工具", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes,
            notify: msg => MessageBox.Show(msg, "提示")));
    }

    private async void RunSelected()
    {
        var sel = _itemBoxes.Where(b => b.Checked).Select(b => (string)b.Tag).ToList();
        if (sel.Count == 0) { Log.Warn("没有勾选任何修复项"); return; }

        await Guard(async () =>
        {
            Log.Info($"开始执行 {sel.Count} 个勾选修复项：{string.Join(", ", sel)}");
            foreach (var key in sel)
            {
                if (_itemActions.TryGetValue(key, out var act))
                {
                    try { await act(); }
                    catch (Exception ex) { Log.Err($"修复项 {key} 异常：{ex.Message}"); }
                }
            }
            Log.Ok("勾选项执行完毕");
        });
    }
}
