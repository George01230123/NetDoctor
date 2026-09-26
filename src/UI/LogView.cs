using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NetDoctor.Core;

namespace NetDoctor.UI;

internal sealed class LogView : Panel
{
    private readonly MainForm _main;
    private readonly TextBox _txt = new();
    private readonly StringBuilder _buffer = new();
    private int _flushed;

    public LogView(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(22, 16, 22, 16);

        var title = Theme.Lbl("运行日志", 15f, Theme.Text, FontStyle.Bold);
        title.Dock = DockStyle.Top; title.Height = 40;

        var desc = Theme.Lbl("所有检测与修复动作都会记录在此，并同步写入 logs 目录下的日志文件。", 9f, Theme.SubText);
        desc.Dock = DockStyle.Top; desc.Height = 24;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.Transparent };
        var bOpen = Theme.Btn("打开日志文件", 130, 34, true);
        bOpen.Location = new Point(0, 4);
        bOpen.Click += (_, _) => Cmd.Open(Log.FilePath);
        var bDir = Theme.Btn("打开日志目录", 130, 34);
        bDir.Location = new Point(142, 4);
        bDir.Click += (_, _) => Cmd.Open(Path.GetDirectoryName(Log.FilePath));
        var bCopy = Theme.Btn("复制全部", 110, 34);
        bCopy.Location = new Point(284, 4);
        bCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_buffer.ToString()); Log.Ok("日志已复制到剪贴板"); }
            catch (Exception ex) { Log.Err("复制失败：" + ex.Message); }
        };
        var bClear = Theme.Btn("清空显示", 110, 34);
        bClear.Location = new Point(406, 4);
        bClear.Click += (_, _) => { _buffer.Clear(); _flushed = 0; _txt.Clear(); };
        bar.Controls.Add(bOpen); bar.Controls.Add(bDir); bar.Controls.Add(bCopy); bar.Controls.Add(bClear);

        Theme.StyleOutput(_txt);
        _txt.Dock = DockStyle.Fill;

        Controls.Add(_txt);
        Controls.Add(bar);
        Controls.Add(desc);
        Controls.Add(title);

        Log.Line += OnLogLine;
        HandleDestroyed += (_, _) => Log.Line -= OnLogLine;
        // 本视图可能整场不被打开，句柄也不会创建，因此必须缓存全文，切换回来时再一次性填充
        VisibleChanged += (_, _) => { if (Visible) Flush(); };
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed) return;

        // 注意：日志视图在未被打开时句柄不存在，此时也要继续缓存，
        // 否则第一次打开时会发现一个字都没有。
        lock (_buffer)
        {
            _buffer.AppendLine(line);
            if (_buffer.Length > 500_000) _buffer.Remove(0, 120_000);
        }

        if (!IsHandleCreated) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnLogLine(line)); } catch { }
            return;
        }
        Flush();
    }

    private void Flush()
    {
        if (!IsHandleCreated || IsDisposed) return;
        string text;
        lock (_buffer) text = _buffer.ToString();
        if (text.Length == _flushed && _txt.TextLength == text.Length) return;

        _flushed = text.Length;
        _txt.Text = text;
        _txt.SelectionStart = _txt.TextLength;
        _txt.ScrollToCaret();
    }
}
