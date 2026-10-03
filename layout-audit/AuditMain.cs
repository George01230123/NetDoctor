using System.Reflection;
using System.Text;
using NetDoctor.UI;

namespace NetDoctorTest;

/// <summary>
/// 布局审计：把每个视图挂到真实尺寸的窗体上触发布局，
/// 然后递归测量控件的实际边界，找出
///   ① 超出父容器（右侧/底部被裁切）
///   ② 尺寸过小（几乎不可见）
///   ③ 同级可见控件互相重叠
/// 用坐标说话，比逐张看图可靠。
/// </summary>
internal static class LayoutAuditMain
{
    private sealed class Issue
    {
        public string View = "", Tab = "", Kind = "", Detail = "";
    }

    private static readonly List<Issue> _issues = new();

    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.OutputEncoding = Encoding.UTF8;

        // 要审计的窗口尺寸：默认大小 + 最小允许大小 + 一个偏窄的尺寸
        var sizes = new[]
        {
            new Size(1180, 760),
            new Size(880, 540),
            new Size(1024, 640),
        };

        Console.WriteLine("═════════════ UI 布局审计 ═════════════");
        Console.WriteLine("检查项：超出父容器 / 尺寸过小 / 同级可见控件重叠");
        Console.WriteLine();

        foreach (var size in sizes)
        {
            Console.WriteLine($"########## 窗口尺寸 {size.Width}×{size.Height} ##########");
            AuditAtSize(size);
            Console.WriteLine();
        }

        Console.WriteLine("═════════════ 汇总 ═════════════");
        if (_issues.Count == 0)
        {
            Console.WriteLine("未发现问题。");
        }
        else
        {
            Console.WriteLine($"共 {_issues.Count} 处：");
            foreach (var g in _issues.GroupBy(i => i.Kind))
            {
                Console.WriteLine($"\n── {g.Key}（{g.Count()} 处）");
                foreach (var i in g.Take(40))
                    Console.WriteLine($"   [{i.View}] {i.Detail}");
                if (g.Count() > 40) Console.WriteLine($"   … 另有 {g.Count() - 40} 处");
            }
        }

        var outFile = Path.Combine(AppContext.BaseDirectory, "layout_issues.txt");
        File.WriteAllLines(outFile, _issues.Select(i => $"{i.Kind}\t{i.View}\t{i.Tab}\t{i.Detail}"), Encoding.UTF8);
        Console.WriteLine($"\n明细已写入：{outFile}");
    }

    private static void AuditAtSize(Size size)
    {
        MainForm form;
        try
        {
            form = new MainForm(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  创建 MainForm 失败：{ex.Message}");
            return;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);   // 挪出屏幕，避免干扰
        form.ClientSize = size;
        form.Show();
        Application.DoEvents();

        // 逐个视图：用 MainForm 自己的切换逻辑，保证和真实运行一致
        var views = new[] { "home", "diag", "repair", "optimize", "windows", "tools", "hardware", "log" };
        foreach (var key in views)
        {
            try
            {
                form.ShowView(key);
                form.PerformLayout();
                // BeginInvoke 排的布局是异步的，要多跑几轮消息循环才能落定
                for (int k = 0; k < 5; k++) { Application.DoEvents(); Thread.Sleep(40); }
                Application.DoEvents();

                var content = GetField<Panel>(form, "_content");
                if (content == null) { Console.WriteLine("  找不到 _content"); break; }

                // 诊断：把关键容器与按钮的实际宽度打出来，
                // 否则只能看到"位置不对"却不知道布局函数拿到的是什么值
                if (key == "windows" || key == "tools" || key == "diag")
                {
                    Console.WriteLine($"    [诊断] {key}: content={content.ClientSize.Width}");
                    foreach (var p in FindAll<Panel>(content).Take(6))
                        Console.WriteLine($"      Panel {p.Name} {p.Width}×{p.Height} dock={p.Dock}");
                    foreach (var b in FindAll<Button>(content).Take(10))
                        Console.WriteLine($"      Button \"{Trim(b.Text)}\" @{b.Left},{b.Top} {b.Width}×{b.Height}");
                }

                // 该视图当前显示的控件
                var view = content.Controls.Cast<Control>().FirstOrDefault(c => c.Visible);
                if (view == null) { Console.WriteLine($"  [{key}] 无可见视图"); continue; }

                AuditControl(view, view, key, "(root)", size, content.ClientSize, 0);

                // 若视图内有 TabControl，逐页审计
                foreach (var tc in FindAll<TabControl>(view))
                {
                    for (int i = 0; i < tc.TabPages.Count; i++)
                    {
                        tc.SelectedIndex = i;
                        form.PerformLayout();
                        Application.DoEvents();
                        Thread.Sleep(30);
                        Application.DoEvents();
                        var page = tc.TabPages[i];
                        AuditControl(page, view, key, page.Text, size, content.ClientSize, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{key}] 审计异常：{ex.Message}");
            }
        }

        form.Hide();
        form.Dispose();
        Application.DoEvents();
    }

    private static void AuditControl(Control root, Control view, string viewKey, string tab,
                                     Size winSize, Size viewSize, int depth)
    {
        var bad = new List<Control>();
        CollectBad(root, root, viewKey, tab, winSize, bad, depth);
        if (bad.Count > 0)
            Console.WriteLine($"  [{viewKey}/{tab}] 发现 {bad.Count} 处问题");
    }

    private static void CollectBad(Control root, Control parent, string viewKey, string tab,
                                   Size winSize, List<Control> bad, int depth)
    {
        if (depth > 24) return;   // 防深递归

        // 同级可见重叠检测。
        // 必须排除「父子嵌套」：卡片里放子面板是正常结构，
        // 子控件的坐标本来就是相对父容器的，两者边界必然相交 ——
        // 不排除的话会把每个卡片都报成重叠，制造大量误报。
        var sibs = parent.Controls.Cast<Control>()
                         .Where(c => c.Visible && c.Width > 4 && c.Height > 4)
                         .ToList();
        for (int i = 0; i < sibs.Count; i++)
        {
            for (int j = i + 1; j < sibs.Count; j++)
            {
                var a = sibs[i].Bounds;
                var b = sibs[j].Bounds;
                var inter = Rectangle.Intersect(a, b);
                if (inter.Width > 6 && inter.Height > 6)
                {
                    _issues.Add(new Issue
                    {
                        View = viewKey,
                        Tab = tab,
                        Kind = "同级重叠",
                        Detail = $"{winSize.Width}px · {Describe(sibs[i])} ↔ {Describe(sibs[j])} 交叠 {inter.Width}×{inter.Height}",
                    });
                }
            }
        }

        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;

            // 只在「当前处理的容器就是 TabControl」时跳过未选中的标签页。
            // 不能无条件跳过：外层会逐页切换并单独审计每一页，
            // 若无条件跳过，那些页的内容就永远查不到了。
            if (parent is TabControl tcp && c is TabPage tp2 && tcp.SelectedTab != tp2) continue;

            // 可滚动容器里的内容超出可视区是**设计行为**（靠滚动访问），
            // 不是布局缺陷；只有不可滚动容器里的超出才是真问题。
            bool inScrollable = parent is ScrollableControl sc && sc.AutoScroll;

            // 越界：控件右/下超出父容器可见区
            int overR = c.Right - parent.ClientSize.Width;
            int overB = c.Bottom - parent.ClientSize.Height;
            if (!inScrollable && (c.Dock == DockStyle.None || c.Dock == DockStyle.Top || c.Dock == DockStyle.Left) && overR > 2)
            {
                _issues.Add(new Issue
                {
                    View = viewKey, Tab = tab, Kind = "右侧超出",
                    Detail = $"{winSize.Width}px · {Describe(c)} 右边缘超出父容器 {overR}px（父宽 {parent.ClientSize.Width}）",
                });
            }
            if (!inScrollable && (c.Dock == DockStyle.None || c.Dock == DockStyle.Top) && overB > 2)
            {
                _issues.Add(new Issue
                {
                    View = viewKey, Tab = tab, Kind = "底部超出",
                    Detail = $"{winSize.Width}px · {Describe(c)} 下边缘超出父容器 {overB}px（父高 {parent.ClientSize.Height}）",
                });
            }

            // 尺寸过小
            if (c is Button || c is CheckBox || c is RadioButton)
            {
                if (c.Height < 20)
                    _issues.Add(new Issue { View = viewKey, Tab = tab, Kind = "尺寸过小",
                        Detail = $"{winSize.Width}px · {Describe(c)} 高度仅 {c.Height}px" });
                if (c.Width < 20)
                    _issues.Add(new Issue { View = viewKey, Tab = tab, Kind = "尺寸过小",
                        Detail = $"{winSize.Width}px · {Describe(c)} 宽度仅 {c.Width}px" });
            }
            if (c is Label && c.Height < 10 && c.Text.Length > 0)
                _issues.Add(new Issue { View = viewKey, Tab = tab, Kind = "尺寸过小",
                    Detail = $"{winSize.Width}px · {Describe(c)} 高度仅 {c.Height}px" });

            // 零尺寸但有内容
            if ((c.Width <= 0 || c.Height <= 0) && c is not Panel)
                _issues.Add(new Issue { View = viewKey, Tab = tab, Kind = "零尺寸",
                    Detail = $"{winSize.Width}px · {Describe(c)} 尺寸 {c.Width}×{c.Height}" });

            CollectBad(root, c, viewKey, tab, winSize, bad, depth + 1);
        }
    }

    private static string Describe(Control c)
    {
        string t = c.Text ?? "";
        if (t.Length > 18) t = t.Substring(0, 18) + "…";
        t = t.Replace("\r", " ").Replace("\n", " ");
        string name = c.Name.Length > 0 ? c.Name : c.GetType().Name;
        return $"{name}(\"{t}\") @{c.Left},{c.Top} {c.Width}×{c.Height}";
    }

    private static string Trim(string s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 14 ? s.Substring(0, 14) + "…" : s;
    }

    private static T GetField<T>(object obj, string name) where T : class
    {
        var f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(obj) as T;
    }

    private static IEnumerable<T> FindAll<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) yield return t;
            foreach (var sub in FindAll<T>(c)) yield return sub;
        }
    }
}
