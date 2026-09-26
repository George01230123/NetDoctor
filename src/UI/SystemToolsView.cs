using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class SystemToolsView : Panel
{
    private readonly MainForm _main;
    private readonly TabControl _tabs = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private bool _busy, _loaded;

    // --- 清理 ---
    private readonly TreeView _cleanTree = new();
    private readonly Dictionary<string, CleanItem> _cleanMap = new();
    private readonly Dictionary<string, long> _cleanSize = new();
    private readonly CheckBox _cleanRecycle = Theme.Chk("同时清空回收站", false);

    // --- 应用 ---
    private readonly DataGridView _appGrid = new();

    // --- 服务 ---
    private readonly DataGridView _svcGrid = new();
    private readonly CheckBox _svcMs = Theme.Chk("显示微软自带服务", false);
    private readonly TextBox _svcFilter = new();

    // --- 启动项 ---
    private readonly DataGridView _startGrid = new();

    public SystemToolsView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        var head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent };
        var t = Theme.Lbl("系统工具", 15f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Left; t.Width = 150;
        _status = Theme.Lbl("正在载入…", 9f, Theme.SubText);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        head.Controls.Add(_status);
        head.Controls.Add(t);

        var hint = Theme.Lbl("垃圾清理 · 应用管理 · 系统服务 · 启动项 · 系统激活　｜　所有删除/禁用操作都不做快照，请确认后再执行。",
            8.5f, Theme.Idle);
        hint.Dock = DockStyle.Top; hint.Height = 22;

        var logCard = new Card { Dock = DockStyle.Bottom, Height = 132, Padding = new Padding(12, 6, 12, 6), Accent2 = Theme.Accent };
        var lt = Theme.Lbl("执行日志", 9.5f, Theme.Text, FontStyle.Bold);
        lt.Dock = DockStyle.Top; lt.Height = 22;
        Theme.StyleOutput(_log);
        _log.Dock = DockStyle.Fill;
        logCard.Controls.Add(_log); logCard.Controls.Add(lt);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.Transparent };
        _progress.Dock = DockStyle.Left; _progress.Width = 240;
        _progress.Style = ProgressBarStyle.Continuous;
        var tip = Theme.Lbl("提示：服务与启动项的改动立即生效；应用卸载后无法通过本工具恢复。", 8.5f, Theme.Idle);
        tip.Dock = DockStyle.Fill;
        bar.Controls.Add(tip); bar.Controls.Add(_progress);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.F(9f);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.ItemSize = new Size(120, 30);
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.DrawItem += DrawTab;

        _tabs.TabPages.Add(BuildCleanPage());
        _tabs.TabPages.Add(BuildAppPage());
        _tabs.TabPages.Add(BuildSvcPage());
        _tabs.TabPages.Add(BuildStartPage());
        _tabs.TabPages.Add(BuildActivatePage());

        Controls.Add(_tabs);
        Controls.Add(bar);
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
        var s = line.Length > 150 ? line.Substring(0, 150) + "…" : line;
        _status.Text = s;
    }

    // ===============================================================
    // 1. 垃圾清理
    // ===============================================================
    private TabPage BuildCleanPage()
    {
        var page = new TabPage("垃圾清理") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bScan = Theme.Btn("扫描占用", 110, 34, true);
        bScan.Location = new Point(0, 6);
        bScan.Click += async (_, _) => await ScanCleanAsync();
        var bAll = Theme.Btn("全选", 80, 34);
        bAll.Location = new Point(122, 6);
        bAll.Click += (_, _) => SetTreeChecked(true);
        var bNone = Theme.Btn("全不选", 80, 34);
        bNone.Location = new Point(214, 6);
        bNone.Click += (_, _) => SetTreeChecked(false);
        var bSafe = Theme.Btn("只选推荐项", 110, 34);
        bSafe.Location = new Point(306, 6);
        bSafe.Click += (_, _) => SelectSafeClean();
        var bGo = Theme.Btn("开始清理", 130, 34);
        bGo.Location = new Point(428, 6);
        bGo.Click += async (_, _) => await RunCleanAsync();
        _cleanRecycle.Location = new Point(572, 13);
        _cleanRecycle.ForeColor = Theme.Warn;
        btns.Controls.Add(bScan); btns.Controls.Add(bAll); btns.Controls.Add(bNone);
        btns.Controls.Add(bSafe); btns.Controls.Add(bGo); btns.Controls.Add(_cleanRecycle);

        _cleanTree.Dock = DockStyle.Fill;
        _cleanTree.CheckBoxes = true;
        _cleanTree.BackColor = Theme.Panel;
        _cleanTree.ForeColor = Theme.Text;
        _cleanTree.Font = Theme.F(9f);
        _cleanTree.BorderStyle = BorderStyle.None;
        _cleanTree.ItemHeight = 24;
        _cleanTree.ShowLines = false;
        _cleanTree.FullRowSelect = true;
        _cleanTree.AfterCheck += (_, e) =>
        {
            if (e.Node.Nodes.Count > 0)
            {
                foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked;
            }
            UpdateCleanSummary();
        };

        page.Controls.Add(_cleanTree);
        page.Controls.Add(btns);
        return page;
    }

    private void BuildCleanTree()
    {
        _cleanTree.BeginUpdate();
        _cleanTree.Nodes.Clear();
        _cleanMap.Clear();
        _cleanSize.Clear();

        var items = JunkCleaner.Targets();
        foreach (var g in items.GroupBy(i => i.Group))
        {
            var gn = new TreeNode(g.Key) { Checked = false };
            foreach (var it in g)
            {
                string key = g.Key + "|" + it.Name;
                _cleanMap[key] = it;
                var n = new TreeNode($"{it.Name}   (点击「扫描占用」计算大小)") { Tag = key, Checked = false };
                gn.Nodes.Add(n);
            }
            _cleanTree.Nodes.Add(gn);
        }
        _cleanTree.ExpandAll();
        _cleanTree.EndUpdate();
        UpdateCleanSummary();
    }

    private void SetTreeChecked(bool on)
    {
        foreach (TreeNode n in _cleanTree.Nodes)
        {
            n.Checked = on;
            foreach (TreeNode c in n.Nodes) c.Checked = on;
        }
        UpdateCleanSummary();
    }

    private void SelectSafeClean()
    {
        SetTreeChecked(false);
        foreach (TreeNode n in _cleanTree.Nodes)
            foreach (TreeNode c in n.Nodes)
                if (_cleanMap.TryGetValue((string)c.Tag, out var it))
                    c.Checked = it.DefaultOn && !it.HighRisk;
        UpdateCleanSummary();
    }

    private List<CleanItem> CheckedCleanItems()
    {
        var res = new List<CleanItem>();
        foreach (TreeNode n in _cleanTree.Nodes)
            foreach (TreeNode c in n.Nodes)
                if (c.Checked && _cleanMap.TryGetValue((string)c.Tag, out var it))
                    res.Add(it);
        return res;
    }

    private void UpdateCleanSummary()
    {
        var sel = CheckedCleanItems();
        long total = 0;
        int known = 0;
        foreach (var it in sel)
        {
            string key = it.Group + "|" + it.Name;
            if (_cleanSize.TryGetValue(key, out long v) && v >= 0) { total += v; known++; }
        }
        string sizeTxt = known == 0 ? "尚未扫描" : $"预计释放 {JunkCleaner.Fmt(total)}";
        _status.Text = $"已选 {sel.Count} / {_cleanMap.Count} 项，{sizeTxt}";
    }

    private async Task ScanCleanAsync()
    {
        if (_busy) { Log.Warn("正在忙，请稍候"); return; }
        _busy = true;
        _main.SetBusy(true, "正在扫描占用…");
        try
        {
            Log.Info("──────── 扫描清理目标占用 ────────");
            var items = JunkCleaner.Targets();
            _cleanSize.Clear();
            long total = 0;
            _progress.Maximum = items.Count;
            _progress.Value = 0;

            int i = 0;
            foreach (var it in items)
            {
                i++;
                _progress.Value = i;
                var key = it.Group + "|" + it.Name;
                long sz = await Task.Run(() => JunkCleaner.MeasureSize(it));
                _cleanSize[key] = sz;
                if (sz > 0) total += sz;
                Log.Info($"  {it.Name,-32} {(it.IsCommandOnly ? "（命令类，无占用）" : JunkCleaner.Fmt(sz))}");
            }

            // 回填到树
            _cleanTree.BeginUpdate();
            foreach (TreeNode g in _cleanTree.Nodes)
            {
                foreach (TreeNode c in g.Nodes)
                {
                    var it = _cleanMap[(string)c.Tag];
                    var key = it.Group + "|" + it.Name;
                    long sz = _cleanSize.TryGetValue(key, out long v) ? v : -1;
                    string warn = string.IsNullOrEmpty(it.Note) ? "" : "  ⚠";
                    c.Text = $"{it.Name}   [{(it.IsCommandOnly ? "命令" : JunkCleaner.Fmt(sz))}]{warn}";
                    if (!string.IsNullOrEmpty(it.Note))
                    {
                        c.ToolTipText = it.Note;
                        c.ForeColor = Theme.Warn;
                    }
                }
            }
            _cleanTree.EndUpdate();

            Log.Ok($"扫描完成，可清理内容合计 {JunkCleaner.Fmt(total)}");
            UpdateCleanSummary();
        }
        catch (Exception ex) { Log.Err("扫描失败：" + ex.Message); }
        finally
        {
            _busy = false;
            _progress.Value = 0;
            _main.SetBusy(false, "扫描完成");
        }
    }

    private async Task RunCleanAsync()
    {
        if (_busy) { Log.Warn("正在忙，请稍候"); return; }
        var items = CheckedCleanItems();
        if (items.Count == 0) { Log.Warn("没有勾选任何清理项"); return; }
        if (_cleanRecycle.Checked)
            items.Add(new CleanItem { Group = "回收站", Name = "回收站（所有分区）", Path = @"%SystemDrive%\$Recycle.bin\*" });

        long est = 0;
        foreach (var it in items)
            if (_cleanSize.TryGetValue(it.Group + "|" + it.Name, out long v) && v > 0) est += v;

        var sb = new StringBuilder();
        sb.AppendLine($"即将清理以下 {items.Count} 个项目：\n");
        foreach (var it in items.Take(14)) sb.AppendLine("   · " + it.Name);
        if (items.Count > 14) sb.AppendLine($"   … 以及另外 {items.Count - 14} 个项目");
        sb.AppendLine($"\n预计释放：{(est > 0 ? JunkCleaner.Fmt(est) : "未扫描，无法预估")}");
        if (_cleanRecycle.Checked) sb.AppendLine("\n⚠ 回收站会被彻底清空，删除的文件将无法找回！");
        sb.AppendLine("\n确定要清理吗？");

        if (MessageBox.Show(sb.ToString(), "垃圾清理",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        { Log.Info("用户取消清理"); return; }

        _busy = true;
        _main.SetBusy(true, "正在清理…");
        try
        {
            Log.Info("════════ 开始垃圾清理 ════════");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var prog = new Progress<string>(s => _status.Text = s);
            var (freed, failed, notes) = await JunkCleaner.CleanAsync(items, prog);
            sw.Stop();

            foreach (var n in notes) Log.Info(n);
            Log.Ok($"清理完成：释放 {JunkCleaner.Fmt(freed)}，{failed} 项未能完全清理，耗时 {sw.Elapsed.TotalSeconds:F1}s");
            MessageBox.Show(
                $"清理完成！\n\n释放空间：{JunkCleaner.Fmt(freed)}\n" +
                (failed > 0 ? $"有 {failed} 项因文件被占用或无权限未能完全清理。\n" : "") +
                "\n提示：被占用的文件（如正在运行的浏览器缓存）重启后再清理效果更好。",
                "垃圾清理", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await ScanCleanAsync();
        }
        catch (Exception ex) { Log.Err("清理异常：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "清理完成"); }
    }

    // ===============================================================
    // 2. 应用管理
    // ===============================================================
    private TabPage BuildAppPage()
    {
        var page = new TabPage("应用管理") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bLoad = Theme.Btn("刷新列表", 110, 34, true);
        bLoad.Location = new Point(0, 6);
        bLoad.Click += async (_, _) => await LoadAppxAsync();
        var bDel = Theme.Btn("卸载选中", 130, 34);
        bDel.Location = new Point(122, 6);
        bDel.Click += async (_, _) => await UninstallAppxAsync();
        var bSelBig = Theme.Btn("勾选大体积(>200MB)", 170, 34);
        bSelBig.Location = new Point(264, 6);
        bSelBig.Click += (_, _) =>
        {
            foreach (DataGridViewRow r in _appGrid.Rows)
                if (r.Tag is AppxItem a)
                    r.Cells[0].Value = a.SizeBytes > 200L * 1024 * 1024;
        };
        var bNone = Theme.Btn("全不选", 90, 34);
        bNone.Location = new Point(446, 6);
        bNone.Click += (_, _) => { foreach (DataGridViewRow r in _appGrid.Rows) r.Cells[0].Value = false; };
        btns.Controls.Add(bLoad); btns.Controls.Add(bDel); btns.Controls.Add(bSelBig); btns.Controls.Add(bNone);

        Theme.Grid(_appGrid);
        _appGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _appGrid.Columns.Add(new DataGridViewCheckBoxColumn
        { Name = "chk", HeaderText = "选", Width = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _appGrid.Columns.Add("name", "应用名称");
        _appGrid.Columns.Add("size", "占用");
        _appGrid.Columns.Add("ver", "版本");
        _appGrid.Columns.Add("pub", "发布者");
        _appGrid.Columns.Add("full", "包全名");
        _appGrid.Columns["name"].FillWeight = 110;
        _appGrid.Columns["size"].FillWeight = 45;
        _appGrid.Columns["ver"].FillWeight = 55;
        _appGrid.Columns["pub"].FillWeight = 70;
        _appGrid.Columns["full"].FillWeight = 160;

        page.Controls.Add(_appGrid);
        page.Controls.Add(btns);
        return page;
    }

    private async Task LoadAppxAsync()
    {
        if (_busy) return;
        _busy = true;
        _main.SetBusy(true, "正在枚举应用…");
        try
        {
            Log.Info("正在枚举已安装的应用（Appx）…");
            _appGrid.Rows.Clear();
            var list = await SystemItems.AppxAsync();
            foreach (var a in list)
            {
                int i = _appGrid.Rows.Add(false, a.Name, a.SizeText, a.Version, a.Publisher, a.FullName);
                _appGrid.Rows[i].Tag = a;
                if (a.NonRemovable)
                {
                    _appGrid.Rows[i].Cells[1].Style.ForeColor = Theme.Idle;
                    _appGrid.Rows[i].Cells["chk"].ReadOnly = true;
                }
            }
            long total = list.Sum(x => x.SizeBytes);
            Log.Ok($"共 {list.Count} 个应用，合计 {JunkCleaner.Fmt(total)}；已按占用从大到小排序");
            _status.Text = $"应用 {list.Count} 个，合计 {JunkCleaner.Fmt(total)}";
        }
        catch (Exception ex) { Log.Err("枚举应用失败：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    private async Task UninstallAppxAsync()
    {
        if (_busy) return;
        var sel = new List<AppxItem>();
        foreach (DataGridViewRow r in _appGrid.Rows)
            if ((r.Cells[0].Value is bool b && b) && r.Tag is AppxItem a)
                sel.Add(a);
        if (sel.Count == 0) { Log.Warn("没有勾选应用"); return; }

        long total = sel.Sum(x => x.SizeBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"即将卸载以下 {sel.Count} 个应用：\n");
        foreach (var a in sel.Take(15)) sb.AppendLine($"   · {a.Name}  ({a.SizeText})");
        if (sel.Count > 15) sb.AppendLine($"   … 以及另外 {sel.Count - 15} 个");
        sb.AppendLine($"\n可释放约 {JunkCleaner.Fmt(total)}。");
        sb.AppendLine("\n⚠ 卸载后无法通过本工具恢复，需要到 Microsoft Store 重新安装。");
        sb.AppendLine("⚠ 部分应用（如照片、计算器、终端）卸载后会影响日常使用。\n\n确定继续吗？");

        if (MessageBox.Show(sb.ToString(), "卸载应用",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        { Log.Info("用户取消卸载"); return; }

        _busy = true;
        _main.SetBusy(true, "正在卸载…");
        try
        {
            Log.Info("════════ 开始卸载应用 ════════");
            var prog = new Progress<string>(s => _status.Text = s);
            var (ok, fail, names) = await SystemItems.UninstallAppxAsync(sel, prog);
            Log.Ok($"卸载完成：成功 {ok} 个，失败 {fail} 个");
            if (names.Count > 0) Log.Warn("失败的应用：" + string.Join(", ", names));

            MessageBox.Show(
                $"卸载完成！\n\n成功：{ok} 个\n失败：{fail} 个" +
                (names.Count > 0 ? "\n\n失败清单：\n" + string.Join("\n", names.Take(10)) : "") +
                "\n\n部分应用可能需要重启后才会完全消失。",
                "应用管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await LoadAppxAsync();
        }
        catch (Exception ex) { Log.Err("卸载异常：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    // ===============================================================
    // 3. 系统服务
    // ===============================================================
    private TabPage BuildSvcPage()
    {
        var page = new TabPage("系统服务") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bLoad = Theme.Btn("刷新", 80, 34, true);
        bLoad.Location = new Point(0, 6);
        bLoad.Click += (_, _) => LoadServices();
        var bStart = Theme.Btn("启动", 80, 34);
        bStart.Location = new Point(92, 6);
        bStart.Click += async (_, _) => await SvcControl(true);
        var bStop = Theme.Btn("停止", 80, 34);
        bStop.Location = new Point(184, 6);
        bStop.Click += async (_, _) => await SvcControl(false);
        var bAuto = Theme.Btn("设为自动", 100, 34);
        bAuto.Location = new Point(276, 6);
        bAuto.Click += async (_, _) => await SvcSet("2");
        var bMan = Theme.Btn("设为手动", 100, 34);
        bMan.Location = new Point(388, 6);
        bMan.Click += async (_, _) => await SvcSet("3");
        var bDis = Theme.Btn("设为禁用", 100, 34);
        bDis.Location = new Point(500, 6);
        bDis.Click += async (_, _) => await SvcSet("4");
        var bPropose = Theme.Btn("禁用推荐项", 120, 34);
        bPropose.Location = new Point(612, 6);
        bPropose.Click += async (_, _) => await SvcDisableRecommended();

        _svcFilter.Location = new Point(748, 10);
        _svcFilter.Width = 160;
        _svcFilter.BackColor = Theme.Card;
        _svcFilter.ForeColor = Theme.Text;
        _svcFilter.BorderStyle = BorderStyle.FixedSingle;
        _svcFilter.PlaceholderText = "筛选服务名…";
        _svcFilter.TextChanged += (_, _) => LoadServices();

        _svcMs.Location = new Point(918, 13);
        _svcMs.CheckedChanged += (_, _) => LoadServices();

        btns.Controls.Add(bLoad); btns.Controls.Add(bStart); btns.Controls.Add(bStop);
        btns.Controls.Add(bAuto); btns.Controls.Add(bMan); btns.Controls.Add(bDis);
        btns.Controls.Add(bPropose); btns.Controls.Add(_svcFilter); btns.Controls.Add(_svcMs);

        Theme.Grid(_svcGrid);
        _svcGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _svcGrid.Columns.Add("name", "服务名");
        _svcGrid.Columns.Add("disp", "显示名称");
        _svcGrid.Columns.Add("stat", "状态");
        _svcGrid.Columns.Add("type", "启动类型");
        _svcGrid.Columns.Add("path", "可执行路径");
        _svcGrid.Columns["name"].FillWeight = 80;
        _svcGrid.Columns["disp"].FillWeight = 100;
        _svcGrid.Columns["stat"].FillWeight = 40;
        _svcGrid.Columns["type"].FillWeight = 45;
        _svcGrid.Columns["path"].FillWeight = 180;

        page.Controls.Add(_svcGrid);
        page.Controls.Add(btns);
        return page;
    }

    private void LoadServices()
    {
        try
        {
            string kw = (_svcFilter.Text ?? "").Trim();
            var list = SystemItems.Services(_svcMs.Checked);
            if (kw.Length > 0)
                list = list.Where(s => s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                                    || s.Display.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

            _svcGrid.Rows.Clear();
            foreach (var s in list)
            {
                int i = _svcGrid.Rows.Add(s.Name, s.Display, s.Status, s.StartType, s.Path);
                _svcGrid.Rows[i].Tag = s;
                _svcGrid.Rows[i].Cells["stat"].Style.ForeColor = s.Running ? Theme.Ok : Theme.Idle;
                _svcGrid.Rows[i].Cells["type"].Style.ForeColor =
                    s.StartNum == "4" ? Theme.Bad : s.StartNum == "2" || s.StartNum == "5" ? Theme.Warn : Theme.SubText;
            }
            _status.Text = $"服务 {list.Count} 个（{( _svcMs.Checked ? "含" : "不含")}微软服务）";
        }
        catch (Exception ex) { Log.Err("加载服务失败：" + ex.Message); }
    }

    private SvcItem SelectedSvc()
        => _svcGrid.CurrentRow?.Tag as SvcItem;

    private async Task SvcControl(bool start)
    {
        var s = SelectedSvc();
        if (s == null) { Log.Warn("请先选中一个服务"); return; }
        Log.Step($"{(start ? "启动" : "停止")}服务 {s.Name}");
        bool ok = await SystemItems.ControlServiceAsync(s, start);
        if (ok) Log.Ok($"服务 {s.Name} {(start ? "已启动" : "已停止")}");
        else Log.Warn($"服务 {s.Name} 操作未成功（可能被系统保护或无权限）");
        LoadServices();
    }

    private async Task SvcSet(string num)
    {
        var s = SelectedSvc();
        if (s == null) { Log.Warn("请先选中一个服务"); return; }
        string desc = num switch { "2" => "自动", "3" => "手动", "4" => "禁用", _ => num };
        if (num == "4" && MessageBox.Show(
                $"确定要把服务「{s.Display}」设为禁用吗？\n\n" +
                "禁用系统服务可能导致某些功能异常，请确认你了解该服务的作用。",
                "禁用服务", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        bool ok = await SystemItems.SetServiceAsync(s, num);
        if (ok) Log.Ok($"服务 {s.Name} 启动类型已设为 {desc}");
        else Log.Err($"服务 {s.Name} 设置失败（可能受系统保护）");
        LoadServices();
    }

    /// <summary>可安全禁用的常见服务（不含关键系统服务）</summary>
    private static readonly (string Name, string Why)[] RecommendedOff =
    {
        ("DiagTrack",        "连接用户体验和遥测（遥测上报）"),
        ("dmwappushservice", "WAP 推送消息路由（遥测相关）"),
        ("WerSvc",           "Windows 错误报告"),
        ("Fax",              "传真服务"),
        ("RemoteRegistry",   "远程注册表（易被利用，建议禁用）"),
        ("RetailDemo",       "零售演示模式"),
        ("MapsBroker",       "下载的地图管理器"),
        ("lfsvc",            "地理位置服务"),
        ("TabletInputService","触摸键盘与手写面板（无触摸屏可禁）"),
        ("PrintNotify",      "打印通知（无打印机可禁）"),
        ("WpcMonSvc",        "家长控制"),
        ("PhoneSvc",         "电话服务"),
        ("WalletService",    "钱包服务"),
        ("SensorDataService","传感器数据服务"),
        ("SensrSvc",         "传感器监视服务"),
        ("TrkWks",           "分布式链接跟踪客户端"),
        ("WMPNetworkSvc",    "WMP 网络共享服务"),
        ("XblAuthManager",   "Xbox Live 身份验证管理器"),
        ("XblGameSave",      "Xbox Live 游戏保存"),
        ("XboxNetApiSvc",    "Xbox Live 网络服务"),
    };

    private async Task SvcDisableRecommended()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"以下 {RecommendedOff.Length} 个服务将被设为「禁用」并停止：\n");
        foreach (var (n, why) in RecommendedOff) sb.AppendLine($"   · {n}  —  {why}");
        sb.AppendLine("\n⚠ 若你使用了打印机、Xbox、触摸屏、地图等功能，请先取消对应项。");
        sb.AppendLine("\n注意：本工具只会禁用「存在的」服务，不存在的自动跳过。\n\n确定继续吗？");

        if (MessageBox.Show(sb.ToString(), "禁用推荐服务",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        { Log.Info("用户取消"); return; }

        int ok = 0, skip = 0;
        var all = SystemItems.Services(true).ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var (n, why) in RecommendedOff)
        {
            if (!all.TryGetValue(n, out var s)) { skip++; Log.Info($"服务 {n} 不存在，跳过"); continue; }
            if (await SystemItems.SetServiceAsync(s, "4")) { ok++; Log.Ok($"  {n} 已禁用（{why}）"); }
            else Log.Warn($"  {n} 禁用失败");
        }
        Log.Ok($"完成：禁用 {ok} 个，{skip} 个不存在");
        LoadServices();
    }

    // ===============================================================
    // 4. 启动项
    // ===============================================================
    private TabPage BuildStartPage()
    {
        var page = new TabPage("启动项") { BackColor = Theme.Bg, Padding = new Padding(0) };

        var btns = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.Transparent };
        var bLoad = Theme.Btn("刷新", 80, 34, true);
        bLoad.Location = new Point(0, 6);
        bLoad.Click += async (_, _) => await LoadStartupAsync();
        var bOff = Theme.Btn("禁用选中", 110, 34);
        bOff.Location = new Point(92, 6);
        bOff.Click += async (_, _) => await ToggleStartup(false);
        var bOn = Theme.Btn("启用选中", 110, 34);
        bOn.Location = new Point(214, 6);
        bOn.Click += async (_, _) => await ToggleStartup(true);
        var bTasks = Theme.Btn("加载计划任务（较慢）", 170, 34);
        bTasks.Location = new Point(336, 6);
        bTasks.Click += async (_, _) => await LoadTasksAsync();
        var bOpen = Theme.Btn("打开任务计划程序", 150, 34);
        bOpen.Location = new Point(518, 6);
        bOpen.Click += (_, _) => Cmd.Start("taskschd.msc");
        btns.Controls.Add(bLoad); btns.Controls.Add(bOff); btns.Controls.Add(bOn);
        btns.Controls.Add(bTasks); btns.Controls.Add(bOpen);

        Theme.Grid(_startGrid);
        _startGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _startGrid.Columns.Add("src", "来源");
        _startGrid.Columns.Add("name", "名称");
        _startGrid.Columns.Add("state", "状态");
        _startGrid.Columns.Add("cmd", "命令 / 路径");
        _startGrid.Columns["src"].FillWeight = 70;
        _startGrid.Columns["name"].FillWeight = 90;
        _startGrid.Columns["state"].FillWeight = 35;
        _startGrid.Columns["cmd"].FillWeight = 220;

        page.Controls.Add(_startGrid);
        page.Controls.Add(btns);
        return page;
    }

    private async Task LoadStartupAsync()
    {
        try
        {
            Log.Info("正在枚举启动项…");
            var list = SystemItems.Startup();
            SystemItems.ApplyApprovedState(list);
            _startGrid.Rows.Clear();
            foreach (var s in list)
            {
                int i = _startGrid.Rows.Add(s.Source, s.Name, s.State, s.Command);
                _startGrid.Rows[i].Tag = s;
                _startGrid.Rows[i].Cells["state"].Style.ForeColor = s.Enabled ? Theme.Ok : Theme.Bad;
            }
            Log.Ok($"启动项 {list.Count} 个（注册表 Run + 启动文件夹）");
            _status.Text = $"启动项 {list.Count} 个";
            await Task.CompletedTask;
        }
        catch (Exception ex) { Log.Err("枚举启动项失败：" + ex.Message); }
    }

    private async Task LoadTasksAsync()
    {
        if (_busy) return;
        _busy = true;
        _main.SetBusy(true, "正在枚举计划任务…");
        try
        {
            Log.Info("正在枚举计划任务（可能较慢）…");
            var tasks = await SystemItems.TasksAsync();
            int added = 0, bootLogon = 0;
            foreach (var s in tasks)
            {
                bool bootLogonTask = s.Command != "(见任务计划程序)" || true;
                int i = _startGrid.Rows.Add(s.Source, s.Name, s.State, s.Command);
                _startGrid.Rows[i].Tag = s;
                _startGrid.Rows[i].Cells["state"].Style.ForeColor = s.Enabled ? Theme.Ok : Theme.Bad;
                if (!s.CanToggle) _startGrid.Rows[i].Cells["name"].Style.ForeColor = Theme.Idle;
                added++;
                if (s.Command != "(见任务计划程序)") bootLogon++;
            }
            Log.Ok($"计划任务 {added} 条（系统任务为灰色，不允许在此禁用）");
            _status.Text = $"启动项 + 计划任务 共 {_startGrid.Rows.Count} 条";
        }
        catch (Exception ex) { Log.Err("枚举计划任务失败：" + ex.Message); }
        finally { _busy = false; _main.SetBusy(false, "完成"); }
    }

    private async Task ToggleStartup(bool enable)
    {
        var sel = new List<StartupItem>();
        foreach (DataGridViewRow r in _startGrid.SelectedRows)
            if (r.Tag is StartupItem s) sel.Add(s);
        if (sel.Count == 0) { Log.Warn("请先选中要操作的行"); return; }

        var blocked = sel.Where(s => !s.CanToggle).ToList();
        if (blocked.Count > 0)
        {
            Log.Warn($"{blocked.Count} 项是系统任务/只读项，已跳过：" +
                     string.Join(", ", blocked.Take(4).Select(b => b.Name)));
        }

        int ok = 0;
        foreach (var s in sel.Where(x => x.CanToggle))
        {
            bool r = s.Kind == "Task"
                ? await SystemItems.ToggleTaskAsync(s, enable)
                : await SystemItems.ToggleStartupAsync(s, enable);
            if (r) ok++;
        }
        Log.Ok($"启动项{(enable ? "启用" : "禁用")}完成：{ok} / {sel.Count(s => s.CanToggle)} 项");
        await LoadStartupAsync();
    }

    // ===============================================================
    // 5. 系统激活
    // ===============================================================
    private Label _masStatus;

    private TabPage BuildActivatePage()
    {
        var page = new TabPage("系统激活") { BackColor = Theme.Bg, Padding = new Padding(16) };

        var card = new Card { Dock = DockStyle.Top, Height = 220, Accent2 = Theme.Warn, Padding = new Padding(16, 12, 16, 12) };
        var t = Theme.Lbl("Windows / Office 激活", 11f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Top; t.Height = 26;

        var body = Theme.Lbl(
            "本工具不内置激活脚本。如果你有 MAS（Microsoft Activation Scripts）之类的激活工具，\n" +
            "把它放到本工具同一目录，或放到 D:\\github\\Release\\Bin\\ 下，这里会自动识别并可以一键调用。\n\n" +
            "支持 HWID / Ohook / KMS / TSforge 等方式，在不改变机器码的前提下激活。\n\n" +
            "⚠ 激活后会修改系统许可状态，属于系统级改动，请自行确认合规性。",
            9f, Theme.SubText);
        body.Dock = DockStyle.Fill;

        _masStatus = Theme.Lbl("正在检测…", 9f, Theme.Warn);
        _masStatus.Dock = DockStyle.Bottom; _masStatus.Height = 26;

        card.Controls.Add(body);
        card.Controls.Add(_masStatus);
        card.Controls.Add(t);

        var btns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent };
        var bCheck = Theme.Btn("重新检测", 110, 36, true);
        bCheck.Click += (_, _) => DetectMas();
        var bRun = Theme.Btn("调用激活工具", 140, 36);
        bRun.Click += (_, _) => RunMas();
        var bLic = Theme.Btn("查看当前激活状态", 150, 36);
        bLic.Click += async (_, _) =>
        {
            Log.Info("查询当前 Windows 许可状态…");
            var r = await Cmd.RunAsync("cscript",
                @"//nologo C:\Windows\System32\slmgr.vbs /dli", 60000);
            Log.Info(r.All.Trim());
            _status.Text = "激活状态已输出到日志";
        };
        btns.Controls.Add(bCheck); btns.Controls.Add(bRun); btns.Controls.Add(bLic);

        var warn = new Card { Dock = DockStyle.Top, Height = 110, Accent2 = Theme.Bad, Padding = new Padding(16, 10, 16, 10) };
        var wt = Theme.Lbl("关于其它 ZyperWin++ 功能", 10f, Theme.Text, FontStyle.Bold);
        wt.Dock = DockStyle.Top; wt.Height = 24;
        var wb = Theme.Lbl(
            "• 卸载 Edge / WebView2、卸载 Office(C2R)、停止 Windows 更新到 2999 年：\n" +
            "  属于高破坏性操作，本工具不做自动调用。原 ZyperWin++ 目录里的 Bin\\Edge、Bin\\Update、\n" +
            "  Bin\\UnInstallC2R.vbs 仍然可用，需要时请直接运行那些脚本。\n" +
            "• 关闭 Windows Defender：会显著降低系统安全性，本工具不提供。",
            8.5f, Theme.SubText);
        wb.Dock = DockStyle.Fill;
        warn.Controls.Add(wb); warn.Controls.Add(wt);

        page.Controls.Add(warn);
        page.Controls.Add(btns);
        page.Controls.Add(card);
        return page;
    }

    private static readonly string[] MasNames = { "MAS_AIO_CN.cmd", "MAS_AIO.cmd", "MAS_AIO_CN.bat" };

    private static readonly string[] MasSearchDirs =
    {
        AppContext.BaseDirectory,
        @"D:\github\Release\Bin",
        @"D:\github\Release",
        Path.Combine(AppContext.BaseDirectory, "Bin"),
    };

    private string FindMas()
    {
        foreach (var d in MasSearchDirs)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var n in MasNames)
                {
                    var p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return null;
    }

    private void DetectMas()
    {
        var p = FindMas();
        if (p != null)
        {
            _masStatus.Text = "✔ 已找到激活工具：" + p;
            _masStatus.ForeColor = Theme.Ok;
            Log.Ok("找到激活工具：" + p);
        }
        else
        {
            _masStatus.Text = "✘ 未找到激活脚本（MAS_AIO_CN.cmd），请放到本工具目录或 D:\\github\\Release\\Bin\\ 下";
            _masStatus.ForeColor = Theme.Bad;
            Log.Warn("未找到激活脚本。搜索路径：" + string.Join(" ; ", MasSearchDirs));
        }
    }

    private void RunMas()
    {
        var p = FindMas();
        if (p == null)
        {
            DetectMas();
            MessageBox.Show(
                "未找到激活脚本。\n\n请把 MAS_AIO_CN.cmd 放到以下任一位置：\n" +
                "  · " + AppContext.BaseDirectory + "\n" +
                "  · D:\\github\\Release\\Bin\\\n\n放好后点「重新检测」。",
                "系统激活", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                $"即将调用：\n{p}\n\n" +
                "会弹出一个命令行窗口，请在那个窗口里按提示操作完成激活。\n\n确定继续吗？",
                "系统激活", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        Log.Warn("用户调用激活工具：" + p);
        try
        {
            Cmd.Start("cmd.exe", $"/k \"{p}\"");
            Log.Ok("已启动激活工具，请在命令行窗口中完成操作");
        }
        catch (Exception ex) { Log.Err("启动失败：" + ex.Message); }
    }

    /// <summary>供 --autotest 驱动：自动跑一次清理扫描（只读，不删除）</summary>
    public async Task AutoScanAsync()
    {
        EnsureLoaded();
        await ScanCleanAsync();
        LoadServices();
        await LoadStartupAsync();
    }

    // ===============================================================
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Log.Info("载入系统工具页…");
            BuildCleanTree();
            SelectSafeClean();
            LoadServices();
            DetectMas();
            Log.Ok("系统工具页就绪。可点「扫描占用」「刷新列表」等按钮加载数据。");
        }
        catch (Exception ex) { Log.Err("载入系统工具页失败：" + ex.Message); }
    }
}
