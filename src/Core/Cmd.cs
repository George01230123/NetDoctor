using System.Diagnostics;
using System.Text;

using NetDoctor.Core;

namespace NetDoctor;

internal sealed class CmdResult
{
    public int ExitCode;
    public string StdOut = "";
    public string StdErr = "";
    public bool Ok => ExitCode == 0;
    public string All => (StdOut + "\n" + StdErr).Trim();
    public override string ToString() => All;
}

internal static class Cmd
{
    private static Encoding Gbk()
    {
        try { return Encoding.GetEncoding(936); } catch { return Encoding.UTF8; }
    }

    public static CmdResult Run(string file, string args, int timeoutMs = 60000)
        => RunAsync(file, args, timeoutMs).GetAwaiter().GetResult();

    /// <summary>异步执行命令，绝不阻塞界面线程</summary>
    public static async Task<CmdResult> RunAsync(string file, string args, int timeoutMs = 60000)
    {
        var r = new CmdResult();
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Gbk(),
                StandardErrorEncoding = Gbk(),
            };
            using var p = new Process { StartInfo = psi };
            if (!p.Start())
            {
                r.ExitCode = -2;
                r.StdErr = "进程启动失败";
                return r;
            }
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                r.ExitCode = -1;
                r.StdErr = $"[超时 {timeoutMs}ms] {file} {args}";
                return r;
            }

            r.StdOut = await soTask;
            r.StdErr = await seTask;
            r.ExitCode = p.ExitCode;
        }
        catch (Exception ex)
        {
            r.ExitCode = -2;
            r.StdErr = ex.Message;
        }
        return r;
    }

    public static Task<CmdResult> Netsh(string args, int t = 60000) => RunAsync("netsh", args, t);
    public static Task<CmdResult> Ipconfig(string args, int t = 60000) => RunAsync("ipconfig", args, t);

    /// <summary>用 cmd.exe 执行（支持管道/重定向）</summary>
    public static Task<CmdResult> Shell(string cmdline, int timeoutMs = 120000)
        => RunAsync("cmd.exe", "/c " + cmdline, timeoutMs);

    /// <summary>后台启动进程，不等待</summary>
    public static void Start(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>用资源管理器打开路径/网址</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>
/// 随程序发布的内嵌脚本。
///
/// 为什么这样做：单文件发布时如果依赖"随包内容文件"，启用 IncludeAllContentForSelfExtract 后
/// AppContext.BaseDirectory 会指向临时解包目录，日志和快照就会跑丢；所以脚本编译进程序集，
/// 首次运行时释放到磁盘。
///
/// 释放位置的判定顺序：
///   1. 宿主显式指定的目录（ND_SetDataDir）—— 原生 DLL 场景下由易语言调用方决定
///   2. 宿主进程 exe 所在目录（NativeAOT / 被别的程序托管时，AppContext.BaseDirectory 不可靠）
///   3. AppContext.BaseDirectory（普通托管 exe）
/// 最终都再拼一个子目录，避免把宿主目录搞乱。
/// </summary>
internal static class EmbeddedScripts
{
    private static string _override;
    private static string _dir;

    /// <summary>宿主显式指定数据目录（原生 DLL 用）。传空则恢复自动判定。</summary>
    public static void SetDataDir(string dir)
    {
        _override = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _dir = null;
    }

    private static string HostExeDir()
    {
        // NativeAOT 下 GetModuleHandle(null) 得到的是宿主进程的主模块
        try
        {
            var sb = new StringBuilder(1024);
            IntPtr h = NativeMethods.GetModuleHandleW(null);
            if (h != IntPtr.Zero &&
                NativeMethods.GetModuleFileNameW(h, sb, sb.Capacity) > 0)
            {
                var exe = sb.ToString();
                if (!string.IsNullOrEmpty(exe))
                {
                    var d = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
        }
        catch { }

        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p))
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d)) return d;
            }
        }
        catch { }
        return null;
    }

    public static string Dir
    {
        get
        {
            if (_dir != null) return _dir;

            string baseDir = _override ?? HostExeDir() ?? AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

            _dir = Path.Combine(baseDir, _override != null ? ".netdoctor" : ".runtime");
            return _dir;
        }
    }

    /// <summary>释放全部内嵌脚本（内容变了会重写），返回释放目录</summary>
    public static string Ensure()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var asm = typeof(EmbeddedScripts).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) continue;
                // 资源名形如 NetDoctor.Data.sysitems.ps1 —— 必须取最后两段，
                // 只 Split('.') 取末段会得到 "ps1"，两个脚本会互相覆盖
                var parts = res.Split('.');
                if (parts.Length < 2) continue;
                string leaf = parts[^2] + "." + parts[^1];
                string path = Path.Combine(Dir, leaf);

                using var s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();

                // 内容一致就不重写，避免每次都动磁盘
                if (File.Exists(path))
                {
                    try
                    {
                        var old = File.ReadAllBytes(path);
                        if (old.Length == bytes.Length && old.AsSpan().SequenceEqual(bytes)) continue;
                    }
                    catch { }
                }
                File.WriteAllBytes(path, bytes);
                Log.Info($"已释放内嵌脚本：{path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("释放内嵌脚本失败：" + ex.Message);
        }
        return Dir;
    }

    /// <summary>取某个脚本的完整路径（不存在时先释放）</summary>
    public static string PathOf(string fileName)
    {
        Ensure();
        return Path.Combine(Dir, fileName);
    }
}

/// <summary>全局日志（界面 + 文件）</summary>
internal static class Log
{
    private static readonly object _lock = new();
    private static string _file;

    public static event Action<string> Line;

    public static string FilePath
    {
        get
        {
            if (_file != null) return _file;
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            try { Directory.CreateDirectory(dir); } catch { }
            _file = Path.Combine(dir, $"NetDoctor_{DateTime.Now:yyyyMMdd}.log");
            return _file;
        }
    }

    public static void Write(string msg, string tag = "INFO")
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {msg}";
        lock (_lock)
        {
            try { System.IO.File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            try { Line?.Invoke(line); } catch { }
        }
    }

    public static void Info(string m) => Write(m, "INFO");
    public static void Ok(string m) => Write(m, " OK ");
    public static void Warn(string m) => Write(m, "WARN");
    public static void Err(string m) => Write(m, "FAIL");
    public static void Step(string m) => Write(m, "STEP");

    /// <summary>记录命令执行结果</summary>
    public static async Task<bool> RunLogged(string title, Func<Task<CmdResult>> action)
    {
        Write(title, "STEP");
        var r = await action();
        if (r.Ok) Ok($"{title} → 成功");
        else Err($"{title} → 失败 (exit={r.ExitCode}) {Trunc(r.All)}");
        return r.Ok;
    }

    public static async Task<bool> RunLogged(string title, string file, string args, int timeout = 60000)
        => await RunLogged(title, () => Cmd.RunAsync(file, args, timeout));

    private static string Trunc(string s, int n = 300)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", "").Replace("\n", " | ").Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
