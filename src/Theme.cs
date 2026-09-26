using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetDoctor;

/// <summary>暗色主题配色 + 通用控件工厂</summary>
internal static class Theme
{
    public static readonly Color Bg        = Color.FromArgb(0x15, 0x17, 0x1C);
    public static readonly Color Panel     = Color.FromArgb(0x1C, 0x1F, 0x26);
    public static readonly Color Card      = Color.FromArgb(0x23, 0x27, 0x30);
    public static readonly Color CardHover = Color.FromArgb(0x2B, 0x30, 0x3B);
    public static readonly Color Border    = Color.FromArgb(0x33, 0x39, 0x45);
    public static readonly Color Text      = Color.FromArgb(0xE6, 0xEA, 0xF2);
    public static readonly Color SubText   = Color.FromArgb(0x9A, 0xA3, 0xB2);
    public static readonly Color Accent    = Color.FromArgb(0x4C, 0x9A, 0xFF);
    public static readonly Color AccentDim = Color.FromArgb(0x2A, 0x4A, 0x78);

    public static readonly Color Ok   = Color.FromArgb(0x3E, 0xD0, 0x8A);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0xB3, 0x3E);
    public static readonly Color Bad  = Color.FromArgb(0xFF, 0x63, 0x63);
    public static readonly Color Idle = Color.FromArgb(0x6A, 0x74, 0x86);

    public const string FontName = "微软雅黑";

    public static Font F(float size, FontStyle style = FontStyle.Regular)
        => new Font(FontName, size, style, GraphicsUnit.Point);

    /// <summary>导航按钮文本用（统一字号与度量）</summary>
    public static readonly Font NavFont = new Font(FontName, 10.5f, FontStyle.Regular, GraphicsUnit.Point);

    public static Label Lbl(string text, float size = 9f, Color? color = null,
                            FontStyle style = FontStyle.Regular, ContentAlignment align = ContentAlignment.MiddleLeft)
        => new Label
        {
            Text = text,
            Font = F(size, style),
            ForeColor = color ?? Text,
            BackColor = Color.Transparent,
            TextAlign = align,
            AutoSize = false,
            Margin = new Padding(0),
        };

    public static Button Btn(string text, int w = 120, int h = 34, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            Font = F(9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(w, h),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            BackColor = primary ? AccentDim : Card,
            ForeColor = primary ? Color.White : Text,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Accent : CardHover;
        b.FlatAppearance.MouseDownBackColor = primary ? AccentDim : Border;
        return b;
    }

    public static CheckBox Chk(string text, bool @checked = false)
        => new CheckBox
        {
            Text = text,
            Checked = @checked,
            Font = F(9f),
            ForeColor = Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
        };

    public static ComboBox Cmb(int w = 200)
        => new ComboBox
        {
            Font = F(9f),
            Width = w,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = Text,
        };

    /// <summary>把日志/输出框刷成暗色</summary>
    public static void StyleOutput(TextBox tb)
    {
        tb.BackColor = Color.FromArgb(0x10, 0x12, 0x16);
        tb.ForeColor = Color.FromArgb(0xC8, 0xD2, 0xE0);
        tb.BorderStyle = BorderStyle.None;
        tb.Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        tb.ScrollBars = ScrollBars.Vertical;
        tb.Multiline = true;
        tb.ReadOnly = true;
        tb.WordWrap = false;
    }

    public static void Grid(DataGridView g)
    {
        g.BackgroundColor = Panel;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Border;
        g.EnableHeadersVisualStyles = false;
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.ReadOnly = true;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        g.ColumnHeadersHeight = 30;
        g.ColumnHeadersDefaultCellStyle.BackColor = Card;
        g.ColumnHeadersDefaultCellStyle.ForeColor = SubText;
        g.ColumnHeadersDefaultCellStyle.Font = F(9f, FontStyle.Bold);
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Card;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.DefaultCellStyle.BackColor = Panel;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = AccentDim;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Font = F(9f);
        g.RowTemplate.Height = 26;
    }

    /// <summary>圆角矩形路径</summary>
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>带左侧色条的卡片容器</summary>
internal sealed class Card : Panel
{
    public Color Accent2 = Theme.Border;
    public Card()
    {
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Padding = new Padding(14, 10, 14, 10);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, 8))
        using (var br = new SolidBrush(Theme.Panel))
        using (var pen = new Pen(Theme.Border))
        {
            e.Graphics.FillPath(br, path);
            e.Graphics.DrawPath(pen, path);
        }
        using (var br = new SolidBrush(Accent2))
        using (var path = Theme.Round(new Rectangle(0, 8, 4, Math.Max(4, Height - 16)), 2))
            e.Graphics.FillPath(br, path);
        base.OnPaint(e);
    }
}

/// <summary>状态圆点</summary>
internal sealed class Dot : Control
{
    private Color _c = Theme.Idle;
    public Color DotColor { get => _c; set { _c = value; Invalidate(); } }
    public Dot() { DoubleBuffered = true; Size = new Size(14, 14); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var br = new SolidBrush(_c);
        e.Graphics.FillEllipse(br, 1, 1, Width - 3, Height - 3);
        using var gl = new Pen(Color.FromArgb(70, _c), 3);
        e.Graphics.DrawEllipse(gl, 0, 0, Width - 2, Height - 2);
    }
}

/// <summary>左侧导航按钮</summary>
internal sealed class NavButton : Button
{
    private bool _active;
    public bool Active
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }
    public NavButton(string text)
    {
        Text = text;
        Font = Theme.NavFont;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Height = 46;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        ForeColor = Theme.SubText;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
        using (var br = new SolidBrush(_active ? Color.FromArgb(0x28, 0x33, 0x45)
                                     : hot ? Color.FromArgb(0x1F, 0x23, 0x2B) : Theme.Panel))
            g.FillRectangle(br, ClientRectangle);

        if (_active)
            using (var bar = new SolidBrush(Theme.Accent))
                g.FillRectangle(bar, 0, 0, 3, Height);

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(22, 0, Math.Max(10, Width - 26), Height),
            _active ? Theme.Text : Theme.SubText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}
