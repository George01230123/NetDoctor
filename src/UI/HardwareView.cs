using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class HardwareView : Panel
{
    private readonly MainForm _main;
    private readonly TabControl _tabs = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private bool _busy, _loaded;

    // 硬件信息
    private readonly TextBox _hwText = new();
    private readonly Label _hwSummary = new();
    private JsonElement _hw;

    // 工具启动器
    private readonly DataGridView _toolGrid = new();
    private readonly VScrollBar _toolSb = new();
    private readonly TextBox _toolFilter = new();
    private List<ToolEntry> _tools = new();

    // 跑分
    private readonly TextBox _benchText = new();
    private readonly ComboBox _diskDrive = new();

    public HardwareView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        var head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent };
        var t = Theme.Lbl("硬件检测", 15f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Left; t.Width = 150;
        _status = Theme.Lbl("就绪", 9f, Theme.SubText);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        head.Controls.Add(_status);
        head.Controls.Add(t);

        var hint = Theme.Lbl(
            "硬件信息来自 WMI/CIM 与注册表，本机采集，不依赖任何外部程序；工具启动器直接调用图吧工具箱 tools 目录下的原版工具。",
            8.5f, Theme.Idle);
        hint.Dock = DockStyle.Top; hint.Height = 22;

        var logCard = new Card { Dock = DockStyle.Bottom, Height = 120, Padding = new Padding(12, 6, 12, 6), Accent2 = Theme.Accent };
        var lt = Theme.Lbl("执行日志", 9.5f, Theme.Text, FontStyle.Bold);
        lt.Dock = DockStyle.Top; lt.Height = 22;
        Theme.StyleOutput(_log);
        _log.Dock = DockStyle.Fill;
        logCard.Controls.Add(_log); logCard.Controls.Add(lt);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.F(9f);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.ItemSize = new Size(126, 30);
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.DrawItem += DrawTab;

        _tabs.TabPages.Add(BuildHardwarePage());
        _tabs.TabPages.Add(BuildToolPage());
        _tabs.TabPages.Add(BuildBenchPage());
        _tabs.TabPages.Add(BuildScreenPage());

        Controls.Add(_tabs);
        Controls.Add(logCard);
        Controls.Add(hint);
        Controls.Add(head);

        Log.Line += OnLogLine;
        HandleDestroyed += (_, _) => Log.Line -= OnLogLine;
        VisibleChanged += (_, _) => { if (Visible) EnsureLoaded(); };
    }

    private void DrawTab(object sender, DrawItemEventArgs e)
    {
        bool sel = e.Index == _tabs.SelectedIndex;
        var r = e.Bounds;
        using (var br = new SolidBrush(sel ? Theme.Panel : Theme.Bg))
            e.Graphics.FillRectangle(br, r);
        if (sel)
            using (var br = new SolidBrush(Theme.Accent))
                e.Graphics.FillRectangle(br, r.X, r.Bottom - 3, r.Width, 3);
        TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text,
            Theme.F(9f, sel ? FontStyle.Bold : FontStyle.Regular), r,
            sel ? Theme.Text : Theme.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLogLine(line)); } catch { } return; }
        _log.AppendText(line + Environment.NewLine);
        _status.Text = line.Length > 150 ? line.Substring(0, 150) + "…" : line;
    }

    // ===============================================================
    // 1. 硬件信息
    // ===============================================================
    private TabPage BuildHardwarePage()
    {
        var page = new TabPage("硬件信息") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bDetect = Theme.Btn("开始检测", 110, 34, true);
        bDetect.Location = new Point(0, 6);
        bDetect.Click += async (_, _) => await DetectAsync(true);
        var bCopy = Theme.Btn("复制报告", 110, 34);
        bCopy.Location = new Point(122, 6);
        bCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_hwText.Text); Log.Ok("硬件报告已复制到剪贴板"); }
            catch (Exception ex) { Log.Err("复制失败：" + ex.Message); }
        };
        var bSave = Theme.Btn("导出到文件", 120, 34);
        bSave.Location = new Point(244, 6);
        bSave.Click += (_, _) =>
        {
            try
            {
                var f = Path.Combine(AppContext.BaseDirectory, "logs",
                    $"硬件报告_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(f, _hwText.Text, Encoding.UTF8);
                Log.Ok("已导出：" + f);
                Cmd.Open(Path.GetDirectoryName(f));
            }
            catch (Exception ex) { Log.Err("导出失败：" + ex.Message); }
        };
        var bTask = Theme.Btn("导出为任务管理器截图友好格式", 240, 34);
        bTask.Location = new Point(376, 6);
        bTask.Click += (_, _) => { MakeCompact(); };
        btns.Controls.Add(bDetect); btns.Controls.Add(bCopy); btns.Controls.Add(bSave); btns.Controls.Add(bTask);

        _hwSummary.Dock = DockStyle.Top;
        _hwSummary.Height = 30;
        _hwSummary.Font = Theme.F(9.5f, FontStyle.Bold);
        _hwSummary.ForeColor = Theme.Accent;
        _hwSummary.TextAlign = ContentAlignment.MiddleLeft;
        _hwSummary.Text = "尚未检测";

        Theme.StyleOutput(_hwText);
        _hwText.Dock = DockStyle.Fill;
        _hwText.Text = "点「开始检测」读取本机硬件信息。\r\n\r\n检测项：系统/整机、处理器、内存（含单双通道判断）、显卡（显存）、\r\n硬盘（健康度/温度/通电时间/磨损）、分区、显示器（面板/尺寸/生产日期）、\r\n网卡、声卡、电池健康度、温度传感器。\r\n";

        page.Controls.Add(_hwText);
        page.Controls.Add(_hwSummary);
        page.Controls.Add(btns);
        return page;
    }

    private bool _compact;

    private void MakeCompact()
    {
        if (_hw.ValueKind != JsonValueKind.Object) { Log.Warn("请先检测硬件信息"); return; }
        _compact = !_compact;
        RenderHw();
        Log.Info(_compact ? "已切换为紧凑格式（适合发帖/截图）" : "已切换为详细格式");
    }

    private void RenderHw()
    {
        if (_hw.ValueKind != JsonValueKind.Object) return;
        _hwText.Text = _compact ? BuildCompact(_hw) : HardwareInfo.BuildReport(_hw);
        _hwSummary.Text = HardwareInfo.SummaryLine(_hw);
    }

    private static string BuildCompact(JsonElement hw)
    {
        var sb = new StringBuilder();
        sb.AppendLine("====== 硬件配置 ======");
        if (hw.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
        {
            var x = c[0];
            sb.AppendLine($"CPU    : {HardwareInfo.Str(x, "Name").Trim()}");
            sb.AppendLine($"         {HardwareInfo.Num(x, "Cores")}核{HardwareInfo.Num(x, "Threads")}线程  " +
                          $"{HardwareInfo.Num(x, "MaxClockMHz")}MHz  L3 {HardwareInfo.Num(x, "L3KB") / 1024.0:0.#}MB");
        }
        if (hw.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0)
        {
            foreach (var x in g.EnumerateArray())
            {
                long vram = HardwareInfo.GpuVramFromRegistry(HardwareInfo.Str(x, "PNPDeviceID"));
                if (vram <= 0) vram = HardwareInfo.Num(x, "AdapterRam");
                sb.AppendLine($"显卡   : {HardwareInfo.Str(x, "Name")}  {(vram > 0 ? JunkCleaner.Fmt(vram) : "?")}");
            }
        }
        if (hw.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            long tot = 0; int cnt = 0;
            foreach (var x in m.EnumerateArray()) { tot += HardwareInfo.Num(x, "CapacityBytes"); cnt++; }
            var first = m[0];
            sb.AppendLine($"内存   : {JunkCleaner.Fmt(tot)} {HardwareInfo.Str(first, "Type")} " +
                          $"{HardwareInfo.Num(first, "ConfiguredMHz")}MHz ×{cnt}" +
                          (cnt == 1 ? "（单通道）" : ""));
        }
        if (hw.TryGetProperty("disk", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in d.EnumerateArray())
                sb.AppendLine($"硬盘   : {HardwareInfo.Str(x, "Model")}  {JunkCleaner.Fmt(HardwareInfo.Num(x, "SizeBytes"))}  {HardwareInfo.Str(x, "Interface")}");
        }
        if (hw.TryGetProperty("system", out var s))
        {
            sb.AppendLine($"主板   : {HardwareInfo.Str(s, "BoardVendor")} {HardwareInfo.Str(s, "BoardProduct")}");
            sb.AppendLine($"系统   : {HardwareInfo.Str(s, "Caption")} {HardwareInfo.Str(s, "Version")}");
        }
        if (hw.TryGetProperty("monitor", out var mo) && mo.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in mo.EnumerateArray())
                sb.AppendLine($"显示器 : {HardwareInfo.Str(x, "FriendlyName")}  {HardwareInfo.Str(x, "YearOfMfg")}年");
        }
        return sb.ToString();
    }

    private async Task DetectAsync(bool announce)
    {
        if (_busy) return;
        _busy = true;
        _main.SetBusy(true, "正在读取硬件信息…");
        try
        {
            Log.Info("──────── 硬件检测 ────────");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _hw = await HardwareInfo.GatherAsync(force: true);
            sw.Stop();
            RenderHw();
            Log.Ok($"硬件信息读取完成，耗时 {sw.ElapsedMilliseconds} ms");

            if (_hw.TryGetProperty("disk", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var x in d.EnumerateArray())
                {
                    var t = HardwareInfo.Num(x, "Temperature");
                    var h = HardwareInfo.Num(x, "Health");
                    string health = HardwareInfo.Str(x, "Health");
                    if (t >= 60) Log.Warn($"  磁盘「{HardwareInfo.Str(x, "Model")}」温度偏高：{t} °C");
                    if (!string.IsNullOrEmpty(health) && !health.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                        Log.Warn($"  磁盘「{HardwareInfo.Str(x, "Model")}」健康状态异常：{health}");
                }
            if (_hw.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Array
                && m.GetArrayLength() == 1)
                Log.Warn("  内存只检测到 1 条，处于单通道模式，会损失明显性能；建议补一条组成双通道。");

            if (!announce) return;
        }
        catch (Exception ex)
        {
            Log.Err("硬件检测失败：" + ex.Message);
            MessageBox.Show("硬件检测失败：\n" + ex.Message, "硬件检测",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    // ===============================================================
    // 2. 工具启动器
    // ===============================================================
    private TabPage BuildToolPage()
    {
        var page = new TabPage("工具启动器") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bScan = Theme.Btn("扫描工具", 110, 34, true);
        bScan.Location = new Point(0, 6);
        bScan.Click += (_, _) => ScanTools(true);
        var bRun = Theme.Btn("启动工具", 110, 34);
        bRun.Location = new Point(122, 6);
        bRun.Click += (_, _) => LaunchSelected();
        var bLoc = Theme.Btn("打开所在目录", 130, 34);
        bLoc.Location = new Point(244, 6);
        bLoc.Click += (_, _) =>
        {
            var t = SelectedTool();
            if (t != null) ToolLauncher.OpenLocation(t);
        };
        var bOpenRoot = Theme.Btn("打开 tools 目录", 150, 34);
        bOpenRoot.Location = new Point(386, 6);
        bOpenRoot.Click += (_, _) =>
        {
            var r = ToolLauncher.FindRoot();
            if (r != null) Cmd.Open(r);
            else Log.Warn("未找到图吧工具箱 tools 目录");
        };

        _toolFilter.Location = new Point(560, 10);
        _toolFilter.Width = 200;
        _toolFilter.BackColor = Theme.Card;
        _toolFilter.ForeColor = Theme.Text;
        _toolFilter.BorderStyle = BorderStyle.FixedSingle;
        _toolFilter.PlaceholderText = "搜索工具名…";
        _toolFilter.TextChanged += (_, _) => FillToolGrid();

        btns.Controls.Add(bScan); btns.Controls.Add(bRun); btns.Controls.Add(bLoc);
        btns.Controls.Add(bOpenRoot); btns.Controls.Add(_toolFilter);

        Theme.Grid(_toolGrid);
        _toolGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // 不用 Fill 模式：Windows 会把 FillWeight 按总宽压缩，中文列会窄到看不清，改为按比例设固定宽度
        _toolGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _toolGrid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "cat", HeaderText = "分类", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        _toolGrid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "name", HeaderText = "工具", Width = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
        _toolGrid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "exe", HeaderText = "可执行文件", Width = 240, SortMode = DataGridViewColumnSortMode.NotSortable });
        _toolGrid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "dir", HeaderText = "目录", Width = 400, SortMode = DataGridViewColumnSortMode.NotSortable });
        _toolGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _toolGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) LaunchSelected(); };
        _toolGrid.SizeChanged += (_, _) => { ScaleToolCols(); UpdateToolScroll(); };
        _toolGrid.RowsAdded += (_, _) => UpdateToolScroll();
        _toolSb.ValueChanged += (_, _) => { };

        // 自管右侧滚动条：DataGridView 自带的垂直滚动条在某些主题/DPI 下会渲染异常
        _toolGrid.Dock = DockStyle.Fill;
        _toolGrid.ScrollBars = ScrollBars.None;
        _toolSb.Dock = DockStyle.Right;
        _toolSb.Width = 14;
        _toolSb.Visible = false;
        _toolSb.Scroll += (_, _) => { try { _toolGrid.FirstDisplayedScrollingRowIndex = _toolSb.Value; } catch { } };
        _toolGrid.MouseWheel += (_, e) =>
        {
            if (!_toolSb.Visible) return;
            int v = Math.Clamp(_toolSb.Value - Math.Sign(e.Delta) * 3,
                               _toolSb.Minimum, _toolSb.Maximum - _toolSb.LargeChange + 1);
            _toolSb.Value = v;
            try { _toolGrid.FirstDisplayedScrollingRowIndex = v; } catch { }
        };

        page.Controls.Add(_toolGrid);
        page.Controls.Add(_toolSb);
        page.Controls.Add(btns);
        return page;
    }

    private ToolEntry SelectedTool() => _toolGrid.CurrentRow?.Tag as ToolEntry;

    /// <summary>按控件可用宽度等比分配工具列表列宽</summary>
    private void ScaleToolCols()
    {
        try
        {
            int avail = _toolGrid.ClientSize.Width - 4;
            if (avail < 300) return;
            double[] w = { 0.10, 0.18, 0.26, 0.46 };
            for (int i = 0; i < _toolGrid.Columns.Count && i < w.Length; i++)
                _toolGrid.Columns[i].Width = Math.Max(60, (int)(avail * w[i]));
        }
        catch { }
    }

    /// <summary>同步自管滚动条的范围</summary>
    private void UpdateToolScroll()
    {
        try
        {
            int rowH = Math.Max(1, _toolGrid.RowTemplate.Height);
            int visible = Math.Max(1, _toolGrid.ClientSize.Height / rowH);
            int total = _toolGrid.Rows.Count;
            _toolSb.Visible = total > visible;
            if (!_toolSb.Visible) { _toolSb.Value = 0; return; }
            _toolSb.Minimum = 0;
            _toolSb.LargeChange = visible;
            _toolSb.SmallChange = 1;
            _toolSb.Maximum = Math.Max(0, total - 1);
        }
        catch { }
    }

    private void LaunchSelected()
    {
        var t = SelectedTool();
        if (t == null) { Log.Warn("请先选中一个工具"); return; }
        if (string.IsNullOrEmpty(t.Exe))
        {
            Log.Warn($"「{t.Name}」本版本未附带可执行文件");
            return;
        }
        ToolLauncher.Launch(t);
    }

    private void ScanTools(bool verbose)
    {
        try
        {
            _tools = ToolLauncher.Scan(force: true);
            FillToolGrid();
            var root = ToolLauncher.FindRoot();
            if (root == null)
            {
                Log.Warn("未找到图吧工具箱 tools 目录。已尝试：D:\\github\\图吧工具箱*\\tools 等常见位置。");
                _status.Text = "未找到图吧工具箱";
                return;
            }
            Log.Ok($"已扫描 {_tools.Count} 个工具，来源：{root}");
            if (verbose)
                Log.Info("分类统计：" + string.Join(" / ",
                    _tools.GroupBy(t => t.Category).Select(g => $"{g.Key} {g.Count()}")));
            _status.Text = $"工具 {_tools.Count} 个，来源 {root}";
        }
        catch (Exception ex) { Log.Err("扫描工具失败：" + ex.Message); }
    }

    private void FillToolGrid()
    {
        string kw = (_toolFilter.Text ?? "").Trim();
        _toolGrid.Rows.Clear();
        foreach (var t in _tools)
        {
            if (kw.Length > 0 && !t.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                              && !t.Category.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
            int i = _toolGrid.Rows.Add(t.Category, t.Name,
                string.IsNullOrEmpty(t.Exe) ? "（未附带）" : Path.GetFileName(t.Exe),
                t.WorkDir);
            _toolGrid.Rows[i].Tag = t;
            if (string.IsNullOrEmpty(t.Exe)) _toolGrid.Rows[i].Cells["exe"].Style.ForeColor = Theme.Idle;
        }
        ScaleToolCols();
        UpdateToolScroll();
    }

    // ===============================================================
    // 3. 性能测试
    // ===============================================================
    private TabPage BuildBenchPage()
    {
        var page = new TabPage("性能测试") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bCpu = Theme.Btn("CPU 跑分（约 15 秒）", 170, 34, true);
        bCpu.Location = new Point(0, 6);
        bCpu.Click += async (_, _) => await RunCpuAsync();
        var bDisk = Theme.Btn("磁盘测速（256MB）", 160, 34);
        bDisk.Location = new Point(182, 6);
        bDisk.Click += async (_, _) => await RunDiskAsync();

        _diskDrive.DropDownStyle = ComboBoxStyle.DropDownList;
        _diskDrive.Width = 190;
        _diskDrive.Location = new Point(354, 11);
        _diskDrive.FlatStyle = FlatStyle.Flat;
        _diskDrive.BackColor = Theme.Card;
        _diskDrive.ForeColor = Theme.Text;
        _diskDrive.Font = Theme.F(9f);
        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady && d.DriveType == DriveType.Fixed)
                _diskDrive.Items.Add($"{d.Name}  (可用 {JunkCleaner.Fmt(d.AvailableFreeSpace)})");
        if (_diskDrive.Items.Count > 0) _diskDrive.SelectedIndex = 0;

        var bCopy = Theme.Btn("复制结果", 110, 34);
        bCopy.Location = new Point(556, 6);
        bCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_benchText.Text); Log.Ok("跑分结果已复制"); } catch { }
        };
        btns.Controls.Add(bCpu); btns.Controls.Add(bDisk); btns.Controls.Add(_diskDrive); btns.Controls.Add(bCopy);

        Theme.StyleOutput(_benchText);
        _benchText.Dock = DockStyle.Fill;
        _benchText.Text =
            "本工具自带跑分，全部在本机实测，不上传任何数据。\r\n\r\n" +
            "【CPU 跑分】SHA256 单线程/多线程吞吐 + 浮点运算性能，用于对比不同 CPU 或验证超频/降频。\r\n" +
            "【磁盘测速】顺序写入 + 顺序读取，会临时写入 256MB 文件并在测试后自动删除。\r\n" +
            "     ⚠ 固态硬盘测速会产生写入量（256MB 写入 + 256MB 读取），不必频繁测。\r\n\r\n" +
            "若要测整机稳定性/温度，建议用「工具启动器」里的 FurMark（烤显卡）、\r\n" +
            "Prime95 / LinX（烤 CPU）、TM5（内存）—— 这些是图吧原版工具。\r\n";

        page.Controls.Add(_benchText);
        page.Controls.Add(btns);
        return page;
    }

    private async Task RunCpuAsync()
    {
        if (_busy) return;
        if (MessageBox.Show(
                "CPU 跑分会把处理器跑满约 15 秒。\n\n笔记本请插好电源，散热不佳时温度会明显上升。\n\n确定开始吗？",
                "CPU 跑分", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _busy = true;
        _main.SetBusy(true, "CPU 跑分中…");
        try
        {
            Log.Info("──────── CPU 跑分开始 ────────");
            var prog = new Progress<string>(s => _status.Text = s);
            var r = await Benchmarks.CpuAsync(prog);

            var sb = new StringBuilder();
            sb.AppendLine("========== CPU 跑分结果 ==========");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (_hw.ValueKind == JsonValueKind.Object &&
                _hw.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
                sb.AppendLine($"CPU ：{HardwareInfo.Str(c[0], "Name").Trim()}");
            sb.AppendLine($"线程数：{r.Threads}");
            sb.AppendLine();
            sb.AppendLine($"SHA256 单线程 : {r.SingleMBps,10:0.0} MB/s   （{r.SingleSec:0.00} 秒）");
            sb.AppendLine($"SHA256 多线程 : {r.MultiMBps,10:0.0} MB/s   （{r.MultiSec:0.00} 秒）");
            sb.AppendLine($"多线程加速比 : {r.MultiMBps / Math.Max(0.1, r.SingleMBps),10:0.0} 倍");
            sb.AppendLine($"浮点运算     : {r.PrimeOps,10:0.0} M ops/s   （{r.PrimeSec:0.00} 秒）");
            sb.AppendLine();
            sb.AppendLine("【怎么看】");
            sb.AppendLine("· 多线程加速比接近线程数说明多核调度正常；明显偏低可能是降频、功耗墙或后台占用。");
            sb.AppendLine("· 单线程分数用于对比同型号 CPU，超频后会提升，降频/过热时会下降。");
            sb.AppendLine("· 结合任务管理器看跑分时的频率与温度，判断是否被功耗墙限制。");

            _benchText.Text = sb.ToString();
            Log.Ok($"CPU 跑分完成：单线程 {r.SingleMBps:0.0} MB/s，多线程 {r.MultiMBps:0.0} MB/s，" +
                   $"加速比 {r.MultiMBps / Math.Max(0.1, r.SingleMBps):0.0}×");
        }
        catch (Exception ex) { Log.Err("CPU 跑分失败：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    private async Task RunDiskAsync()
    {
        if (_busy) return;
        if (_diskDrive.SelectedIndex < 0) { Log.Warn("请先选择要测试的磁盘"); return; }
        string sel = _diskDrive.SelectedItem.ToString();
        string letter = sel.Substring(0, 2);
        string dir = letter + "\\";

        var free = new DriveInfo(letter).AvailableFreeSpace;
        if (free < 1L * 1024 * 1024 * 1024) { Log.Warn($"{letter} 可用空间不足 1GB，无法测速"); return; }

        if (MessageBox.Show(
                $"将在 {letter} 根目录临时写入 256MB 测试文件，测完自动删除。\n\n" +
                "⚠ 固态硬盘会产生写入量，不必频繁测试。\n\n确定开始吗？",
                "磁盘测速", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _busy = true;
        _main.SetBusy(true, "磁盘测速中…");
        try
        {
            Log.Info($"──────── 磁盘测速开始（{letter}）────────");
            var prog = new Progress<string>(s => _status.Text = s);
            var r = await Benchmarks.DiskAsync(dir, 256, prog);

            if (!string.IsNullOrEmpty(r.Error))
            {
                Log.Err("磁盘测速失败：" + r.Error);
                _benchText.Text = "磁盘测速失败：" + r.Error;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("========== 磁盘测速结果 ==========");
            sb.AppendLine($"时间  ：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"位置  ：{r.Path}");
            sb.AppendLine($"数据量：{JunkCleaner.Fmt(r.SizeBytes)} 顺序读写");
            sb.AppendLine();
            sb.AppendLine($"顺序写入 : {r.WriteMBps,10:0.0} MB/s");
            sb.AppendLine($"顺序读取 : {r.ReadMBps,10:0.0} MB/s");
            sb.AppendLine();
            sb.AppendLine("【怎么看】");
            sb.AppendLine("· SATA SSD 顺序读一般 450~560 MB/s，NVMe 视代数 1500~7000+ MB/s。");
            sb.AppendLine("· 机械硬盘 100~200 MB/s。写入明显低于读取说明缓存策略或颗粒状态有问题。");
            sb.AppendLine("· 这里只测顺序速度；随机 4K 性能（影响系统流畅度）请用工具启动器里的");
            sb.AppendLine("  CrystalDiskMark / AS SSD Benchmark 测。");
            sb.AppendLine("· 硬盘健康度请看「硬件信息」标签页的 SMART 数据。");

            _benchText.Text = sb.ToString();
            Log.Ok($"磁盘测速完成：写入 {r.WriteMBps:0.0} MB/s，读取 {r.ReadMBps:0.0} MB/s");
        }
        catch (Exception ex) { Log.Err("磁盘测速失败：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    // ===============================================================
    // 4. 屏幕测试
    // ===============================================================
    private TabPage BuildScreenPage()
    {
        var page = new TabPage("屏幕测试") { BackColor = Theme.Bg, Padding = new Padding(16) };

        var t = Theme.Lbl("显示器测试（全屏显示，按 ESC 或点任意处退出）", 10f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Top; t.Height = 28;

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 160, BackColor = Color.Transparent };
        void Add(string txt, Color c, string tip)
        {
            var b = Theme.Btn(txt, 150, 40);
            b.Margin = new Padding(0, 0, 10, 10);
            b.Click += (_, _) => ScreenTest.ShowSolid(c, tip);
            var tipLabel = new Label();   // 占位，避免布局错乱
            flow.Controls.Add(b);
        }
        Add("纯白（查坏点/灰尘）", Color.White, "白色");
        Add("纯黑（查漏光/亮点）", Color.Black, "黑色");
        Add("纯红", Color.FromArgb(255, 0, 0), "红色");
        Add("纯绿", Color.FromArgb(0, 255, 0), "绿色");
        Add("纯蓝", Color.FromArgb(0, 0, 255), "蓝色");
        Add("灰阶渐变", Color.Gray, "渐变");

        var flow2 = new FlowPanel2();
        var bGrad = Theme.Btn("灰阶渐变测试", 150, 40);
        bGrad.Margin = new Padding(0, 0, 10, 10);
        bGrad.Click += (_, _) => ScreenTest.ShowGradient();
        var bGrid = Theme.Btn("网格/几何测试", 150, 40);
        bGrid.Margin = new Padding(0, 0, 10, 10);
        bGrid.Click += (_, _) => ScreenTest.ShowGrid();
        var bText = Theme.Btn("文字清晰度测试", 150, 40);
        bText.Margin = new Padding(0, 0, 10, 10);
        bText.Click += (_, _) => ScreenTest.ShowText();
        var bUfo = Theme.Btn("UFO 高刷测试（网页）", 170, 40);
        bUfo.Margin = new Padding(0, 0, 10, 10);
        bUfo.Click += (_, _) => Cmd.Open("https://www.testufo.com/");
        var bOnline = Theme.Btn("在线屏幕测试（网页）", 170, 40);
        bOnline.Margin = new Padding(0, 0, 10, 10);
        bOnline.Click += (_, _) => Cmd.Open("https://screen.bmcx.com/");
        flow2.Controls.Add(bGrad); flow2.Controls.Add(bGrid);
        flow2.Controls.Add(bText); flow2.Controls.Add(bUfo); flow2.Controls.Add(bOnline);

        var note = Theme.Lbl(
            "用法：\n" +
            "· 纯白画面查黑点/灰尘，纯黑画面查亮点与漏光（关灯看更明显）。\n" +
            "· 三原色画面查色偏与坏点（某个子像素坏掉会在对应颜色下显出来）。\n" +
            "· 灰阶渐变查色带（banding）与灰阶过渡是否平滑。\n" +
            "· 网格画面查几何失真、边缘对焦、有无形变。\n" +
            "· 高刷新率与拖影请用 UFO 测试（需要浏览器支持高刷）。\n\n" +
            "退出方式：按 ESC 键，或鼠标点击画面。",
            9f, Theme.SubText);
        note.Dock = DockStyle.Fill;

        page.Controls.Add(note);
        page.Controls.Add(flow2);
        page.Controls.Add(flow);
        page.Controls.Add(t);
        return page;
    }

    // 小工具：第二行按钮容器
    private sealed class FlowPanel2 : FlowLayoutPanel
    {
        public FlowPanel2()
        {
            Dock = DockStyle.Top;
            Height = 56;
            BackColor = Color.Transparent;
        }
    }

    /// <summary>供 --autotest 驱动：自动完成一次硬件检测 + 工具扫描</summary>
    public async Task AutoDetectAsync(int tab = 0)
    {
        EnsureLoaded();
        ScanTools(false);
        await DetectAsync(false);
        if (tab >= 0 && tab < _tabs.TabPages.Count) _tabs.SelectedIndex = tab;
        ScaleToolCols();
        UpdateToolScroll();
    }

    // ===============================================================
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Log.Info("载入硬件检测页…");
        ScanTools(false);
        _ = DetectAsync(false);
    }
}
