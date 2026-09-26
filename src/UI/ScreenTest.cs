using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetDoctor.UI;

/// <summary>全屏屏幕测试画面（按 ESC 或点击退出）</summary>
internal sealed class ScreenTest : Form
{
    private enum Mode { Solid, Gradient, Grid, Text }

    private readonly Mode _mode;
    private readonly Color _color;
    private int _step;

    private ScreenTest(Mode mode, Color color)
    {
        _mode = mode;
        _color = color;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Default;

        // 覆盖整个虚拟桌面（多屏一起测）
        var b = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(b.Left, b.Top, b.Width, b.Height);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MouseDown += (_, _) =>
        {
            if (_mode == Mode.Grid)
            {
                _step++;
                if (_step > 2) Close();
                Invalidate();
            }
            else Close();
        };
    }

    private static ScreenTest Show(Mode m, Color c)
    {
        var f = new ScreenTest(m, c);
        f.Show();
        f.Activate();
        f.Focus();
        NetDoctor.Log.Info($"屏幕测试：{m} 全屏显示（ESC 或点击退出）");
        return f;
    }

    public static void ShowSolid(Color c, string name) => Show(Mode.Solid, c);
    public static void ShowGradient() => Show(Mode.Gradient, Color.Empty);
    public static void ShowGrid() => Show(Mode.Grid, Color.Empty);
    public static void ShowText() => Show(Mode.Text, Color.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;

        switch (_mode)
        {
            case Mode.Solid:
                using (var br = new SolidBrush(_color)) g.FillRectangle(br, r);
                break;

            case Mode.Gradient:
                // 黑→白 灰阶条 + 平滑渐变，用于查色带
                int bars = 32;
                int bw = Math.Max(1, r.Width / bars);
                for (int i = 0; i < bars; i++)
                {
                    int v = (int)(255.0 * i / (bars - 1));
                    using var br = new SolidBrush(Color.FromArgb(v, v, v));
                    g.FillRectangle(br, i * bw, 0, bw, r.Height / 2);
                }
                using (var lg = new LinearGradientBrush(
                    new Rectangle(0, r.Height / 2, Math.Max(1, r.Width), Math.Max(1, r.Height - r.Height / 2)),
                    Color.Black, Color.White, LinearGradientMode.Horizontal))
                    g.FillRectangle(lg, 0, r.Height / 2, r.Width, r.Height - r.Height / 2);

                using (var f = new Font("微软雅黑", 14, FontStyle.Bold))
                    TextRenderer.DrawText(g, "上：32 级灰阶   下：连续渐变   —— 出现竖条纹/色块即为色带(banding)   按 ESC 退出",
                        f, new Rectangle(0, 6, r.Width, 30), Color.Orange,
                        TextFormatFlags.HorizontalCenter);
                break;

            case Mode.Grid:
                g.Clear(Color.White);
                using (var pen = new Pen(Color.FromArgb(120, 120, 120), 1))
                using (var penB = new Pen(Color.Black, 2))
                {
                    int cell = _step == 0 ? 40 : _step == 1 ? 10 : 1;
                    for (int x = 0; x < r.Width; x += cell)
                        g.DrawLine(cell <= 2 ? penB : pen, x, 0, x, r.Height);
                    for (int y = 0; y < r.Height; y += cell)
                        g.DrawLine(cell <= 2 ? penB : pen, 0, y, r.Width, y);
                }
                using (var pen = new Pen(Color.Red, 3))
                    g.DrawRectangle(pen, 2, 2, r.Width - 5, r.Height - 5);
                using (var f = new Font("微软雅黑", 16, FontStyle.Bold))
                    TextRenderer.DrawText(g,
                        $"网格间距 {(_step == 0 ? "40px（查几何失真）" : _step == 1 ? "10px（查边缘对焦/清晰度）" : "1px（查摩尔纹与锐度）")}    " +
                        "点击切换 · 第三次点击退出",
                        f, new Rectangle(0, r.Height / 2 - 20, r.Width, 40), Color.Blue,
                        TextFormatFlags.HorizontalCenter);
                break;

            case Mode.Text:
                g.Clear(Color.White);
                using (var f1 = new Font("微软雅黑", 9))
                using (var f2 = new Font("微软雅黑", 12))
                using (var f3 = new Font("微软雅黑", 16, FontStyle.Bold))
                using (var f4 = new Font("SimSun", 12))
                {
                    int y = 30;
                    TextRenderer.DrawText(g, "文字清晰度测试 —— 检查各字号下的边缘锐利度与彩边（子像素渲染）", f3,
                        new Rectangle(0, y, r.Width, 30), Color.Black, TextFormatFlags.HorizontalCenter);
                    y += 50;
                    foreach (var (f, label) in new (Font, string)[]
                    {
                        (f1, "9pt 微软雅黑：永和九年，岁在癸丑，暮春之初，会于会稽山阴之兰亭。"),
                        (f2, "12pt 微软雅黑：永和九年，岁在癸丑，暮春之初，会于会稽山阴之兰亭，修禊事也。"),
                        (f3, "16pt 加粗：永和九年，岁在癸丑，暮春之初。"),
                        (f4, "12pt 宋体：The quick brown fox jumps over the lazy dog. 0123456789"),
                    })
                    {
                        TextRenderer.DrawText(g, label, f, new Rectangle(40, y, r.Width - 80, 30), Color.Black,
                            TextFormatFlags.Left);
                        y += 36;
                    }
                    y += 20;
                    TextRenderer.DrawText(g,
                        "检查要点：\n" +
                        "· 小字号是否发虚、笔画是否粘连（缩放比例不对会明显发虚）\n" +
                        "· 黑白交界处有无红/蓝彩边（ClearType 子像素渲染，正常现象但过重说明 RGB 排列设置不对）\n" +
                        "· 整行文字是否水平（无倾斜）\n\n按 ESC 或点击退出",
                        f2, new Rectangle(40, y, r.Width - 80, 200), Color.FromArgb(60, 60, 60),
                        TextFormatFlags.Left);
                }
                break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
