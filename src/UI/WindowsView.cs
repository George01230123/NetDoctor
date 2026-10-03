using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class WindowsView : Panel
{
    private readonly MainForm _main;
    private readonly TabControl _tabs = new();
    private readonly Dictionary<string, DataGridView> _grids = new();
    private readonly TextBox _log = new();
    private readonly Label _summary = new();
    private readonly Label _progressText = new();
    private readonly ProgressBar _progress = new();
    private readonly Dictionary<string, List<OptStatus>> _status = new();
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);   // 跨分类记住勾选
    private bool _busy;
    private bool _loaded;

    private const int ColCheck = 0, ColName = 1, ColState = 2, ColCur = 3;

    public WindowsView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        // ---------------- 顶部 ----------------
        var head = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent };
        var t = Theme.Lbl("Windows 优化", 15f, Theme.Text, FontStyle.Bold);
        t.Dock = DockStyle.Left; t.Width = 190;
        _summary = Theme.Lbl("正在载入配置…", 9f, Theme.SubText);
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        head.Controls.Add(_summary);
        head.Controls.Add(t);

        var hintText = Theme.Lbl("正在载入…", 8.5f, Theme.Idle);
        hintText.Dock = DockStyle.Top;
        hintText.Height = 22;
        hintText.AutoEllipsis = true;
        _hintLabel = hintText;

        // ---------------- 日志 ----------------
        var logCard = new Card { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(12, 6, 12, 6), Accent2 = Theme.Accent };
        var lt = Theme.Lbl("执行日志", 9.5f, Theme.Text, FontStyle.Bold);
        lt.Dock = DockStyle.Top; lt.Height = 22;
        Theme.StyleOutput(_log);
        _log.Dock = DockStyle.Fill;
        logCard.Controls.Add(_log); logCard.Controls.Add(lt);

        // ---------------- 状态栏 ----------------
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.Transparent };
        _progressText.Dock = DockStyle.Left; _progressText.Width = 260;
        _progressText.Font = Theme.F(8.5f); _progressText.ForeColor = Theme.SubText;
        _progressText.Text = "就绪";
        _progress.Dock = DockStyle.Left; _progress.Width = 220;
        _progress.Style = ProgressBarStyle.Continuous;
        var note = Theme.Lbl("提示：改动注册表的项即时生效；涉及服务/电源的项需注销或重启后完全生效。",
            8.5f, Theme.Idle);
        note.Dock = DockStyle.Fill;
        bar.Controls.Add(note);
        bar.Controls.Add(_progress);
        bar.Controls.Add(_progressText);

        // ---------------- 按钮条 ----------------
        // 这排有 8 个按钮，合计约 940px；而可用宽度默认只有 942px、最小窗口更只剩 634px。
        // 原来写死坐标（0/142/…/894，累计 1024px）必然溢出，最小窗口下右侧几个按钮
        // 会被整块切掉。改用流式布局并按实际宽度分布：放不下时自动换行，
        // 宁可占两行也不让按钮消失。
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
        };

        var bApply = Theme.Btn("应用勾选项", 118, 38, true);
        bApply.Margin = new Padding(0, 4, 8, 4);
        bApply.Click += async (_, _) => await ApplyChecked();
        var bRestore = Theme.Btn("还原勾选项", 118, 38);
        bRestore.Margin = new Padding(0, 4, 8, 4);
        bRestore.Click += async (_, _) => await RestoreChecked();
        var bSelAll = Theme.Btn("全选本页", 94, 38);
        bSelAll.Margin = new Padding(0, 4, 8, 4);
        bSelAll.Click += (_, _) => SetAllCurrentPage(true);
        var bSelNone = Theme.Btn("全不选", 84, 38);
        bSelNone.Margin = new Padding(0, 4, 8, 4);
        bSelNone.Click += (_, _) => SetAllCurrentPage(false);
        var bSafe = Theme.Btn("只选推荐项", 110, 38);
        bSafe.Margin = new Padding(0, 4, 8, 4);
        bSafe.Click += (_, _) => SelectRecommended();
        var bRefresh = Theme.Btn("重新检测状态", 118, 38);
        bRefresh.Margin = new Padding(0, 4, 8, 4);
        bRefresh.Click += (_, _) => RefreshCurrent(recount: true);
        var bRestoreAll = Theme.Btn("全部还原", 104, 38);
        bRestoreAll.Margin = new Padding(0, 4, 8, 4);
        bRestoreAll.Click += async (_, _) => await RestoreAll();
        var bSnap = Theme.Btn("打开快照目录", 118, 38);
        bSnap.Margin = new Padding(0, 4, 0, 4);
        bSnap.Click += (_, _) => Cmd.Open(Backup.Root);

        // 八个按钮原来写死坐标（0/142/284/…/894），累计需要 1024px，
        // 而这一行可用宽度只有 942px（默认窗口）到 642px（最小窗口）——
        // 必然溢出，最小窗口下右侧几个按钮会被整个切掉。
        // 改为按容器实际宽度自适应间距：先保证两侧留白，再均分剩余空间。
        btns.Controls.Add(bApply); btns.Controls.Add(bRestore); btns.Controls.Add(bSelAll);
        btns.Controls.Add(bSelNone); btns.Controls.Add(bSafe); btns.Controls.Add(bRefresh);
        btns.Controls.Add(bRestoreAll); btns.Controls.Add(bSnap);

        // ---------------- 分类标签 ----------------
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.F(9f);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.DrawItem += DrawTab;
        _tabs.SelectedIndexChanged += (_, _) => UpdateSummary();

        // 标签宽度按可用宽度分配：7 个分类 × 固定 118px = 826px，
        // 而内容区最窄只有约 620px —— 溢出 200px，末尾分类的文字会被压扁看不清。
        // 改为均分可用宽度。
        bool tabSizing = false;
        void LayoutTabs()
        {
            // 改 ItemSize 会触发布局并再次 Resize，直接改会无限递归（实测栈溢出 0xC0000409）
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
            if (tabSizing || !_tabs.IsHandleCreated) return;
            _tabs.BeginInvoke(new Action(LayoutTabs));
        };
        this.Resize += (_, _) => { if (IsHandleCreated && !tabSizing) BeginInvoke(new Action(LayoutTabs)); };
        HandleCreated += (_, _) => BeginInvoke(new Action(LayoutTabs));

        Controls.Add(_tabs);
        Controls.Add(btns);
        Controls.Add(bar);
        Controls.Add(logCard);
        Controls.Add(hintText);
        Controls.Add(head);

        Log.Line += OnLogLine;
        HandleDestroyed += (_, _) => Log.Line -= OnLogLine;

        VisibleChanged += (_, _) => { if (Visible) EnsureLoaded(); };
        _loaded = false;
    }

    // ---------------------------------------------------------------
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Log.Info("正在载入内置 Windows 优化配置…");
            var all = WindowsOptimizer.Load();
            Log.Ok($"已载入 {all.Count} 条优化项，共 {WindowsOptimizer.CategoryOrder.Length} 个分类");

            foreach (var cat in WindowsOptimizer.CategoryOrder)
            {
                var items = WindowsOptimizer.ByCategory(cat);
                if (items.Count == 0) continue;

                var page = new TabPage($"{WindowsOptimizer.CategoryTitle(cat)} ({items.Count})")
                {
                    BackColor = Theme.Bg,
                    Padding = new Padding(0),
                };
                page.Tag = cat;

                var grid = new DataGridView { Dock = DockStyle.Fill };
                BuildGrid(grid);
                page.Controls.Add(grid);
                _tabs.TabPages.Add(page);
                _grids[cat] = grid;
            }

            RefreshAll(recount: true);
        }
        catch (Exception ex)
        {
            Log.Err("载入优化配置失败：" + ex.Message);
            _summary.Text = "载入失败：" + ex.Message;
        }
    }

    private void BuildGrid(DataGridView g)
    {
        Theme.Grid(g);
        g.SelectionMode = DataGridViewSelectionMode.CellSelect;
        g.MultiSelect = false;
        g.EditMode = DataGridViewEditMode.EditOnEnter;
        g.AllowUserToResizeRows = false;
        g.RowTemplate.Height = 26;
        g.ColumnHeadersHeight = 28;

        var colChk = new DataGridViewCheckBoxColumn
        {
            Name = "chk", HeaderText = "选", Width = 40, FillWeight = 4,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FlatStyle = FlatStyle.Flat,
        };
        g.Columns.Add(colChk);

        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "优化项", FillWeight = 62,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "state", HeaderText = "状态", FillWeight = 12,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "cur", HeaderText = "当前值", FillWeight = 22,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        g.CellValueChanged += (s, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColCheck) return;
            var grid = (DataGridView)s;
            var st = grid.Rows[e.RowIndex].Tag as OptStatus;
            if (st == null) return;
            bool on = grid.Rows[e.RowIndex].Cells[ColCheck].Value is bool b && b;
            if (on) _checked.Add(Key(st.Item)); else _checked.Remove(Key(st.Item));
            UpdateSummary();
        };
        // 单击即切换复选框
        g.CellClick += (s, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColCheck) return;
            var grid = (DataGridView)s;
            var cell = grid.Rows[e.RowIndex].Cells[ColCheck];
            cell.Value = !(cell.Value is bool b && b);
            grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        g.CurrentCellDirtyStateChanged += (s, e) =>
        {
            var grid = (DataGridView)s;
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private static string Key(OptItem it) => it.Category + "|" + it.Name;

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

    // ---------------------------------------------------------------
    private string CurrentCat => _tabs.SelectedTab?.Tag as string;

    public void RefreshAll(bool recount)
    {
        foreach (var kv in _grids) RefreshCategory(kv.Key, kv.Value);
        UpdateSummary();
    }

    private void RefreshCurrent(bool recount)
    {
        var cat = CurrentCat;
        if (cat == null || !_grids.TryGetValue(cat, out var g)) return;
        RefreshCategory(cat, g);
        UpdateSummary();
    }

    private void RefreshCategory(string cat, DataGridView g)
    {
        var items = WindowsOptimizer.ByCategory(cat);
        var list = new List<OptStatus>(items.Count);
        foreach (var it in items) list.Add(WindowsOptimizer.Check(it));
        _status[cat] = list;

        g.SuspendLayout();
        g.Rows.Clear();
        foreach (var st in list)
        {
            var it = st.Item;
            string state = st.Unknown ? "无法判定" : st.Applied ? "已优化" : "未优化";
            if (st.Unknown) state = st.Applied ? "已优化" : "状态未知";

            int i = g.Rows.Add(_checked.Contains(Key(it)), it.Name, state, st.Current);
            var row = g.Rows[i];
            row.Tag = st;

            if (it.HighRisk)
            {
                row.Cells[ColName].Style.ForeColor = Theme.Bad;
                row.Cells[ColName].ToolTipText = "降低系统防护，请确认清楚再勾选";
            }

            Color col = st.Unknown ? Theme.Idle : st.Applied ? Theme.Ok : Theme.Warn;
            row.Cells[ColState].Style.ForeColor = col;
            if (st.Applied)
                row.Cells[ColName].Style.SelectionForeColor = Color.White;

            row.Cells[ColCur].Style.ForeColor = Theme.SubText;
            row.Cells[ColCur].ToolTipText = st.Current;
        }
        g.ResumeLayout();
    }

    private void UpdateSummary()
    {
        int applied = 0, total = 0, unknown = 0;
        foreach (var kv in _status)
            foreach (var s in kv.Value)
            {
                total++;
                if (s.Applied) applied++;
                if (s.Unknown) unknown++;
            }
        var cat = CurrentCat;
        string catInfo = "";
        if (cat != null && _status.TryGetValue(cat, out var lst))
        {
            int ca = lst.Count(s => s.Applied);
            catInfo = $"　当前分类：{WindowsOptimizer.CategoryTitle(cat)} {ca}/{lst.Count} 已优化";
        }
        _summary.Text = $"共 {total} 项优化 · 已优化 {applied} · 未优化 {total - applied - unknown}" +
                        (unknown > 0 ? $" · 状态未知 {unknown}" : "") +
                        $"　已勾选 {_checked.Count} 项{catInfo}";
        _summary.ForeColor = Theme.SubText;

        if (_tabs.SelectedTab != null)
        {
            var c = _tabs.SelectedTab.Tag as string;
            var catHint = WindowsOptimizer.CategoryHint(c ?? "");
            if (_hintLabel != null) _hintLabel.Text = catHint.Length > 0 ? catHint : HintHead;
        }
    }

    private Label _hintLabel;
    private const string HintHead =
        "配置源自 ZyperWin++ 的声明式优化表（151 项，含官方还原方案）。改动前会自动写入 backup\\backup.ini 快照。红色项会降低系统防护，默认不勾选。";

    private void SetAllCurrentPage(bool on)
    {
        var cat = CurrentCat;
        if (cat == null || !_status.TryGetValue(cat, out var lst)) return;
        foreach (var st in lst)
        {
            if (on) _checked.Add(Key(st.Item)); else _checked.Remove(Key(st.Item));
        }
        if (_grids.TryGetValue(cat, out var g))
        {
            foreach (DataGridViewRow r in g.Rows)
                r.Cells[ColCheck].Value = on;
        }
        UpdateSummary();
        Log.Info($"{(on ? "全选" : "全不选")}「{WindowsOptimizer.CategoryTitle(cat)}」{lst.Count} 项");
    }

    /// <summary>供概览页快捷按钮调用：勾选全部推荐项（自动排除安全风险项）</summary>
    public void SelectRecommendedPublic()
    {
        EnsureLoaded();
        SelectRecommended();
    }

    /// <summary>只勾选推荐项：排除高风险与「无法判定」的项</summary>
    private void SelectRecommended()
    {
        int n = 0;
        foreach (var kv in _status)
            foreach (var st in kv.Value)
            {
                if (st.Item.HighRisk) { _checked.Remove(Key(st.Item)); continue; }
                _checked.Add(Key(st.Item));
                n++;
            }
        foreach (var kv in _grids)
        {
            foreach (DataGridViewRow r in kv.Value.Rows)
                if (r.Tag is OptStatus st)
                    r.Cells[ColCheck].Value = _checked.Contains(Key(st.Item));
        }
        UpdateSummary();
        Log.Ok($"已勾选全部推荐项（{n} 项，已排除安全风险项）。请检查后再点「应用勾选项」。");
    }

    private List<OptItem> CheckedItems()
    {
        var res = new List<OptItem>();
        foreach (var kv in _status)
            foreach (var st in kv.Value)
                if (_checked.Contains(Key(st.Item)))
                    res.Add(st.Item);
        return res;
    }

    // ---------------------------------------------------------------
    private async Task ApplyChecked()
    {
        var items = CheckedItems();
        if (items.Count == 0) { Log.Warn("没有勾选任何优化项"); return; }

        int highRisk = items.Count(i => i.HighRisk);
        int skipped = items.Count(i => i.IsKeyExistenceCheck);

        var msg = new StringBuilder();
        msg.AppendLine($"即将应用 {items.Count} 项 Windows 优化。\n");
        if (highRisk > 0) msg.AppendLine($"⚠ 其中 {highRisk} 项会降低系统防护（红色项）：");
        foreach (var i in items.Where(i => i.HighRisk).Take(6)) msg.AppendLine("     · " + i.Name);
        if (highRisk > 6) msg.AppendLine($"     … 等共 {highRisk} 项");
        if (highRisk > 0) msg.AppendLine();
        if (skipped > 0) msg.AppendLine($"（{skipped} 项为整键操作，状态无法自动判定）\n");
        msg.AppendLine("优化前会自动生成注册表原值快照，可随时还原。\n\n确定继续吗？");

        if (MessageBox.Show(msg.ToString(), "应用 Windows 优化",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            Log.Info("用户取消应用优化");
            return;
        }

        await RunBatch(items, applying: true);
    }

    private async Task RestoreChecked()
    {
        var items = CheckedItems();
        if (items.Count == 0) { Log.Warn("没有勾选任何项"); return; }
        if (MessageBox.Show(
                $"将按配置声明的还原方案，把勾选的 {items.Count} 项恢复为 Windows 默认。\n\n确定继续吗？",
                "还原 Windows 优化", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await RunBatch(items, applying: false);
    }

    private async Task RestoreAll()
    {
        var items = WindowsOptimizer.Load();
        if (MessageBox.Show(
                $"将还原全部 {items.Count} 项优化为 Windows 默认状态。\n\n" +
                "这会清除本工具以及其它同类工具写入的优化注册表项。\n确定继续吗？",
                "全部还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        await RunBatch(items, applying: false);
    }

    private async Task RunBatch(List<OptItem> items, bool applying)
    {
        if (_busy) { Log.Warn("已有任务在执行"); return; }
        _busy = true;
        _main.SetBusy(true, applying ? "正在应用优化…" : "正在还原…");

        int ok = 0, fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string reason = applying ? $"Windows 优化（{items.Count} 项）" : $"Windows 优化还原（{items.Count} 项）";
            Log.Info($"════════ {reason} 开始 ════════");

            // 1) 网络层快照
            await Backup.Snapshot(reason);

            // 2) 注册表原值快照
            try
            {
                var txt = WindowsOptimizer.SnapshotValues(items, reason);
                File.AppendAllText(Path.Combine(Backup.Root, "backup.ini"), txt, Encoding.UTF8);
                Log.Ok("已记录各优化项在操作前的注册表原值");
            }
            catch (Exception ex) { Log.Warn("原值快照写入失败：" + ex.Message); }

            // 3) 逐项执行
            _progress.Maximum = items.Count;
            _progress.Value = 0;
            _progressText.Text = $"0 / {items.Count}";

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                _progressText.Text = $"{i + 1} / {items.Count}";
                _progress.Value = i + 1;

                Log.Step($"[{(applying ? "优化" : "还原")}] {it.Name}");
                bool r;
                try
                {
                    r = applying
                        ? WindowsOptimizer.Apply(it, m => Log.Info(m))
                        : WindowsOptimizer.Restore(it, m => Log.Info(m));
                }
                catch (Exception ex)
                {
                    Log.Err($"  {it.Name} 异常：{ex.Message}");
                    r = false;
                }

                if (r) { ok++; Log.Ok($"  {it.Name} → {(applying ? "已优化" : "已还原")}"); }
                else { fail++; Log.Warn($"  {it.Name} → 部分失败（见上方明细）"); }

                if (i % 5 == 4) await Task.Delay(1);   // 让界面喘口气
            }

            // 4) 生效通知
            WindowsOptimizer.BroadcastSettingChange();
            try
            {
                if (applying) { Cmd.Run("cmd.exe", "/c gpupdate /target:user /force", 60000); }
            }
            catch { }

            sw.Stop();
            Log.Info($"════════ 结束：成功 {ok}，失败/部分失败 {fail}，耗时 {sw.Elapsed.TotalSeconds:F1}s ════════");
            _progressText.Text = $"完成：成功 {ok}，失败 {fail}";

            var ans = MessageBox.Show(
                $"{(applying ? "优化" : "还原")}完成。\n\n成功 {ok} 项，失败 {fail} 项。" +
                "\n\n部分设置（任务栏、资源管理器、主题相关）需要重启 explorer 或注销后才会看到效果。\n是否现在重启资源管理器？",
                "夕颜若雪网络工具", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ans == DialogResult.Yes)
            {
                Log.Warn("重启资源管理器以应用界面相关设置");
                Cmd.Run("cmd.exe",
                    "/c taskkill /f /im explorer.exe & start explorer.exe", 30000);
            }
        }
        catch (Exception ex)
        {
            Log.Err("批处理异常：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _main.SetBusy(false, "完成");
            RefreshAll(recount: true);
        }
    }

    // ---------------------------------------------------------------
    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLogLine(line)); } catch { } return; }
        _log.AppendText(line + Environment.NewLine);
    }
}
