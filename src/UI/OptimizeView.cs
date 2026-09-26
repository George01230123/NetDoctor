using System.Drawing;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class OptimizeView : Panel
{
    private readonly MainForm _main;
    private readonly DataGridView _grid = new();
    private readonly DataGridView _gridTcp = new();
    private readonly TextBox _log = new();
    private readonly TextBox _txtCustom = new();
    private readonly Label _best = new();
    private readonly CheckBox _chkAutoApply = Theme.Chk("测速后自动应用最快 DNS", false);
    private bool _busy;
    private List<DnsCandidate> _result = new();

    public OptimizeView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        var title = Theme.Lbl("网络优化", 15f, Theme.Text, FontStyle.Bold);
        title.Dock = DockStyle.Top; title.Height = 40;
        var desc = Theme.Lbl("DNS 择优加速 + TCP 参数调优 + 网卡节能关闭，改善网页打开速度与连接稳定性。", 9f, Theme.SubText);
        desc.Dock = DockStyle.Top; desc.Height = 24;

        // ---------- 日志（固定高度） ----------
        var logCard = new Card { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(12, 8, 12, 8), Accent2 = Theme.Accent };
        var lt = Theme.Lbl("优化过程", 9.5f, Theme.Text, FontStyle.Bold);
        lt.Dock = DockStyle.Top; lt.Height = 22;
        Theme.StyleOutput(_log);
        _log.Dock = DockStyle.Fill;
        logCard.Controls.Add(_log); logCard.Controls.Add(lt);

        // ---------- TCP + 自定义DNS ----------
        var bottom2 = new Panel { Dock = DockStyle.Bottom, Height = 224, BackColor = Color.Transparent };

        var tcpCard = new Card { Dock = DockStyle.Left, Width = 470, Accent2 = Theme.Warn, Padding = new Padding(12, 8, 12, 8) };
        var tt = Theme.Lbl("TCP 参数调优", 9.5f, Theme.Text, FontStyle.Bold);
        tt.Dock = DockStyle.Top; tt.Height = 22;
        var flowTcp = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 74, BackColor = Color.Transparent };
        var bResp = Theme.Btn("响应优先", 96, 32, true);
        bResp.Click += async (_, _) => await Guard(async () => { await NetworkOptimizer.TuneResponsive(); ShowTcp(); });
        var bThr = Theme.Btn("吞吐优先", 96, 32);
        bThr.Click += async (_, _) => await Guard(async () => { await NetworkOptimizer.TuneThroughput(); ShowTcp(); });
        var bDef = Theme.Btn("还原默认", 96, 32);
        bDef.Click += async (_, _) => await Guard(async () => { await NetworkOptimizer.TuneDefault(); ShowTcp(); });
        var bShow = Theme.Btn("查看当前", 96, 32);
        bShow.Click += (_, _) => ShowTcp();
        var bPwr = Theme.Btn("关闭网卡节能", 120, 32);
        bPwr.Click += async (_, _) => await Guard(async () => { await NetworkOptimizer.DisableNicPowerSave(); });
        var bNet = Theme.Btn("看谁在占网速", 120, 32);
        bNet.Click += async (_, _) => await Guard(async () =>
        {
            Log.Step("统计当前 TCP 连接占用");
            var t = await NetworkOptimizer.NetTopProcesses();
            Log.Info("连接数 TOP 进程：\n" + t);
        });
        flowTcp.Controls.Add(bResp); flowTcp.Controls.Add(bThr); flowTcp.Controls.Add(bDef);
        flowTcp.Controls.Add(bShow); flowTcp.Controls.Add(bPwr); flowTcp.Controls.Add(bNet);

        Theme.Grid(_gridTcp);
        _gridTcp.Columns.Add("k", "TCP 参数");
        _gridTcp.Columns.Add("v", "当前值");
        _gridTcp.Columns["k"].FillWeight = 60;
        _gridTcp.Columns["v"].FillWeight = 40;
        _gridTcp.RowTemplate.Height = 22;
        _gridTcp.ColumnHeadersHeight = 26;
        _gridTcp.ScrollBars = ScrollBars.Vertical;
        _gridTcp.Dock = DockStyle.Fill;

        tcpCard.Controls.Add(_gridTcp);
        tcpCard.Controls.Add(flowTcp);
        tcpCard.Controls.Add(tt);

        var dnsCard = new Card { Dock = DockStyle.Fill, Accent2 = Theme.Ok, Padding = new Padding(12, 8, 12, 8) };
        var dt = Theme.Lbl("自定义 DNS", 9.5f, Theme.Text, FontStyle.Bold);
        dt.Dock = DockStyle.Top; dt.Height = 22;
        var flowDns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, BackColor = Color.Transparent };
        _txtCustom.Font = Theme.F(9f);
        _txtCustom.Width = 250;
        _txtCustom.Height = 26;
        _txtCustom.BackColor = Theme.Card;
        _txtCustom.ForeColor = Theme.Text;
        _txtCustom.BorderStyle = BorderStyle.FixedSingle;
        _txtCustom.Text = "223.5.5.5,223.6.6.6";
        var bApplyC = Theme.Btn("应用", 70, 30, true);
        bApplyC.Click += async (_, _) => await ApplyCustom();
        var bRestore = Theme.Btn("恢复自动获取DNS", 140, 30);
        bRestore.Click += async (_, _) => await Guard(async () =>
        {
            await NetworkOptimizer.RestoreAutoDns();
            _main.RefreshHome();
        });
        flowDns.Controls.Add(_txtCustom); flowDns.Controls.Add(bApplyC); flowDns.Controls.Add(bRestore);

        var hint = Theme.Lbl(
            "多个 DNS 用英文逗号分隔，例如：223.5.5.5,114.114.114.114\n\n" +
            "点右侧「开始 DNS 测速」实测 12 个公共 DNS 的响应速度，\n" +
            "再一键切换到最快的那组。改前会自动生成状态快照。", 8.5f, Theme.SubText);
        hint.Dock = DockStyle.Fill;

        dnsCard.Controls.Add(hint);
        dnsCard.Controls.Add(flowDns);
        dnsCard.Controls.Add(dt);

        bottom2.Controls.Add(dnsCard);
        bottom2.Controls.Add(tcpCard);

        // ---------- DNS 测速表格（占主区） ----------
        var dnsArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var btnRow = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.Transparent };
        var bBench = Theme.Btn("开始 DNS 测速（12 个公共 DNS）", 230, 36, true);
        bBench.Location = new Point(0, 4);
        bBench.Click += (_, _) => RunDnsBenchmark();
        var bBest = Theme.Btn("应用最快 DNS", 120, 36);
        bBest.Location = new Point(242, 4);
        bBest.Click += async (_, _) => await ApplyBest();
        var bTop2 = Theme.Btn("应用最快前两个", 140, 36);
        bTop2.Location = new Point(374, 4);
        bTop2.Click += async (_, _) => await ApplyTop2();
        _chkAutoApply.Location = new Point(528, 12);
        btnRow.Controls.Add(bBench); btnRow.Controls.Add(bBest); btnRow.Controls.Add(bTop2); btnRow.Controls.Add(_chkAutoApply);

        _best.Dock = DockStyle.Bottom;
        _best.Height = 24;
        _best.Font = Theme.F(9f, FontStyle.Bold);
        _best.ForeColor = Theme.SubText;
        _best.Text = "尚未测速";

        Theme.Grid(_grid);
        _grid.Columns.Add("name", "DNS 名称");
        _grid.Columns.Add("ip", "服务器地址");
        _grid.Columns.Add("ms", "响应时间");
        _grid.Columns.Add("rate", "评级");
        _grid.Columns.Add("note", "备注");
        _grid.Columns["name"].FillWeight = 110;
        _grid.Columns["ip"].FillWeight = 80;
        _grid.Columns["ms"].FillWeight = 60;
        _grid.Columns["rate"].FillWeight = 50;
        _grid.Columns["note"].FillWeight = 120;
        _grid.Dock = DockStyle.Fill;

        dnsArea.Controls.Add(_grid);
        dnsArea.Controls.Add(_best);
        dnsArea.Controls.Add(btnRow);

        Controls.Add(dnsArea);
        Controls.Add(logCard);
        Controls.Add(bottom2);
        Controls.Add(desc);
        Controls.Add(title);

        Log.Line += OnLogLine;
        HandleDestroyed += (_, _) => Log.Line -= OnLogLine;

        ShowTcp();
        VisibleChanged += (_, _) => { if (Visible) ShowTcp(); };
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLogLine(line)); } catch { } return; }
        _log.AppendText(line + Environment.NewLine);
    }

    private async void ShowTcp()
    {
        try
        {
            var pairs = await NetworkOptimizer.TcpGlobalPairsAsync();
            _gridTcp.Rows.Clear();
            foreach (var kv in pairs)
            {
                int i = _gridTcp.Rows.Add(kv.Key, kv.Value);
                var v = kv.Value.ToLowerInvariant();
                _gridTcp.Rows[i].Cells[1].Style.ForeColor =
                    (v == "enabled" || v == "normal" || v == "default") ? Theme.Ok
                  : (v == "disabled" || v == "off") ? Theme.SubText
                  : Theme.Warn;
            }
            if (pairs.Count == 0) _gridTcp.Rows.Add("(读取失败)", "");
        }
        catch (Exception ex)
        {
            _gridTcp.Rows.Clear();
            _gridTcp.Rows.Add("读取失败", ex.Message);
        }
    }

    private async Task Guard(Func<Task> act)
    {
        if (_busy) { Log.Warn("已有优化任务在执行，请稍候"); return; }
        _busy = true;
        _main.SetBusy(true, "正在处理…");
        try { await act(); }
        catch (Exception ex) { Log.Err("优化异常：" + ex.Message); }
        finally
        {
            _busy = false;
            _main.SetBusy(false, "完成");
            await Task.Delay(600);
        }
    }

    public async void RunDnsBenchmark()
    {
        if (_busy) { Log.Warn("正在忙，请稍候"); return; }
        _busy = true;
        _main.SetBusy(true, "正在测速 DNS…");
        _grid.Rows.Clear();
        _best.Text = "测速中…";
        _best.ForeColor = Theme.SubText;

        try
        {
            Log.Info("──────── 开始 DNS 测速 ────────");
            var order = 0;
            var prog = new Progress<DnsCandidate>(c =>
            {
                order++;
                string rate = !c.Ok ? "不可用" : c.Ms < 40 ? "极快" : c.Ms < 100 ? "优秀" : c.Ms < 250 ? "正常" : "偏慢";
                var col = !c.Ok ? Theme.Bad : c.Ms < 100 ? Theme.Ok : Theme.Warn;
                int i = _grid.Rows.Add(c.Name, c.Ip, c.Ok ? $"{c.Ms:F0} ms" : "—", rate, c.Note);
                _grid.Rows[i].Cells[2].Style.ForeColor = col;
                _grid.Rows[i].Cells[3].Style.ForeColor = col;
                _best.Text = $"测速进度 {order}/12 …";
                Log.Write($"{c.Name,-14} {c.Ip,-16} {(c.Ok ? c.Ms.ToString("F0") + " ms" : "无响应")} {c.Note}",
                    c.Ok ? " OK " : "WARN");
            });

            _result = await NetworkOptimizer.BenchmarkAll(prog);
            var okList = _result.Where(r => r.Ok).ToList();
            if (okList.Count == 0)
            {
                _best.Text = "全部 DNS 无响应 —— 可能当前已断网";
                _best.ForeColor = Theme.Bad;
                Log.Err("所有 DNS 均无响应，请先做【断网修复】");
                return;
            }
            var top = okList.Take(3).ToList();
            _best.Text = "最快：" + string.Join("   ", top.Select(t => $"{t.Name} {t.Ip} {t.Ms:F0}ms"));
            _best.ForeColor = Theme.Ok;
            Log.Ok($"测速完成，最快 3 个：{string.Join(" / ", top.Select(t => $"{t.Name}({t.Ip}) {t.Ms:F0}ms"))}");

            if (_chkAutoApply.Checked)
            {
                await ApplyDnsList(new[] { top[0].Ip }, $"自动应用最快 DNS：{top[0].Name} ({top[0].Ip})");
            }
        }
        catch (Exception ex) { Log.Err("测速失败：" + ex.Message); }
        finally
        {
            _busy = false;
            _main.SetBusy(false, "测速完成");
        }
    }

    private async Task ApplyBest()
    {
        var b = _result.Where(r => r.Ok).OrderBy(r => r.Ms).FirstOrDefault();
        if (b == null) { Log.Warn("请先测速，或所有 DNS 均不可用"); return; }
        await ApplyDnsList(new[] { b.Ip }, $"应用最快 DNS：{b.Name} ({b.Ip}) {b.Ms:F0}ms");
    }

    private async Task ApplyTop2()
    {
        var t = _result.Where(r => r.Ok).OrderBy(r => r.Ms).Take(2).ToList();
        if (t.Count == 0) { Log.Warn("请先测速"); return; }
        await ApplyDnsList(t.Select(x => x.Ip).ToArray(),
            "应用最快前两个：" + string.Join(", ", t.Select(x => $"{x.Name}({x.Ip})")));
    }

    private async Task ApplyCustom()
    {
        var raw = _txtCustom.Text ?? "";
        var ips = raw.Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim())
                     .Distinct().ToArray();
        if (ips.Length == 0) { Log.Warn("请输入至少一个 DNS 地址"); return; }

        foreach (var ip in ips)
        {
            if (!System.Net.IPAddress.TryParse(ip, out _))
            {
                Log.Err($"「{ip}」不是合法 IP 地址，已中止");
                return;
            }
        }
        await ApplyDnsList(ips, "应用自定义 DNS：" + string.Join(", ", ips));
    }

    private async Task ApplyDnsList(string[] ips, string reason)
    {
        var ans = MessageBox.Show(
            reason + "\n\n即将修改所有活动网卡的 DNS 设置。\n修改前会自动生成快照，可随时恢复。\n\n确定应用吗？",
            "应用 DNS", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) { Log.Info("用户取消了 DNS 修改"); return; }

        await Guard(async () =>
        {
            await Backup.Snapshot("修改 DNS 设置");
            await NetworkOptimizer.ApplyDns(ips);
            _main.RefreshHome();
            await Task.Delay(300);
            var t = await NetworkDiag.TestDnsServer(ips[0]);
            Log.Info(t.Ok ? $"验证：新 DNS {ips[0]} 解析正常（{t.Ms:F0} ms）" : $"验证：新 DNS {ips[0]} 无响应，建议换一个");
        });
    }
}
