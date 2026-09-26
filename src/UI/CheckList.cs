using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

/// <summary>
/// 检查结果列表：彩色状态点 + 名称 + 详情 + 建议。
/// 不依赖 Panel.AutoScroll，改为自绘 + 外部垂直滚动条，避免子控件位移叠加导致错位。
/// </summary>
internal sealed class CheckList : Panel
{
    private readonly List<Check> _items = new();
    private readonly VScrollBar _sb = new();
    private int _scroll;
    private const int RowH = 46;
    private const int Pad = 8;

    public CheckList()
    {
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _sb.Dock = DockStyle.Right;
        _sb.Width = 12;
        _sb.Visible = false;
        _sb.Scroll += (_, _) => { _scroll = _sb.Value; Invalidate(); };
        Controls.Add(_sb);
    }

    public List<Check> Items => _items;

    public void Clear() { _items.Clear(); _scroll = 0; UpdateScroll(); Invalidate(); }

    public void Add(Check c)
    {
        _items.Add(c);
        UpdateScroll();
        Invalidate();
    }

    public void AddRange(IEnumerable<Check> cs)
    {
        foreach (var c in cs) _items.Add(c);
        UpdateScroll();
        Invalidate();
    }

    private int ViewH => Math.Max(1, Height);
    private int TotalH => _items.Count * RowH + Pad;

    private void UpdateScroll()
    {
        int total = TotalH;
        bool need = total > ViewH;
        _sb.Visible = need;
        if (need)
        {
            _sb.Minimum = 0;
            _sb.Maximum = Math.Max(0, total - ViewH + (_sb.LargeChange - 1));
            _sb.LargeChange = Math.Max(1, ViewH);
            _sb.SmallChange = RowH;
        }
        else _scroll = 0;

        int max = Math.Max(0, total - ViewH);
        if (_scroll > max) _scroll = max;
        if (_sb.Visible && _sb.Value != _scroll) _sb.Value = Math.Min(_scroll, _sb.Maximum);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_sb.Visible)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 3, 0, Math.Max(0, TotalH - ViewH));
            _sb.Value = Math.Min(_scroll, _sb.Maximum);
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillRectangle(bg, ClientRectangle);

        int listW = Width - (_sb.Visible ? _sb.Width : 0);
        if (_items.Count == 0)
        {
            using var f = Theme.F(9.5f);
            TextRenderer.DrawText(g, "点击右上角「开始全面检测」开始…", f,
                new Rectangle(0, 14, listW, 34), Theme.Idle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int y = Pad - _scroll;
        foreach (var c in _items)
        {
            var row = new Rectangle(0, y, Math.Max(10, listW - 2), RowH - 6);
            if (row.Bottom > 0 && row.Top < Height)
            {
                using (var path = Theme.Round(row, 6))
                using (var br = new SolidBrush(Theme.Panel))
                    g.FillPath(br, path);

                Color col = c.Level switch
                {
                    Sev.Ok => Theme.Ok,
                    Sev.Warn => Theme.Warn,
                    Sev.Bad => Theme.Bad,
                    _ => Theme.Idle,
                };

                using (var br = new SolidBrush(col))
                using (var path = Theme.Round(new Rectangle(0, y + 4, 3, RowH - 14), 2))
                    g.FillPath(br, path);

                using (var br = new SolidBrush(col))
                    g.FillEllipse(br, 15, y + 15, 10, 10);

                int textW = Math.Max(60, listW - 34 - 210 - 10);

                using (var f = Theme.F(9.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, c.Name, f,
                        new Rectangle(34, y + 3, textW, 22), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                using (var f = Theme.F(9f))
                    TextRenderer.DrawText(g, c.Detail, f,
                        new Rectangle(listW - 216, y + 3, 206, 22), col,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (!string.IsNullOrEmpty(c.Hint))
                    using (var f = Theme.F(8.5f))
                        TextRenderer.DrawText(g, "→ " + c.Hint, f,
                            new Rectangle(34, y + 24, Math.Max(60, listW - 44), 18), Theme.SubText,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            y += RowH;
        }
    }
}
