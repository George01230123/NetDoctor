using System.Text;

namespace NetDoctor.Core;

internal sealed class CleanItem
{
    public string Group = "";
    public string Name = "";
    public string Path = "";          // 支持环境变量
    public string ExtraCmd = "";      // 需要 dism / powershell 之类的特殊命令
    public string Note = "";          // 风险提示，显示在树上
    public bool IsCommandOnly => !string.IsNullOrEmpty(ExtraCmd);
    public bool HighRisk;
    public bool DefaultOn = true;

    /// <summary>展开环境变量后的真实路径</summary>
    public string Expanded =>
        Environment.ExpandEnvironmentVariables(Path ?? "");

    public override string ToString() => Name;
}

internal static class JunkCleaner
{
    /// <summary>21 项清理目标（对齐 ZyperWin++ 的清理表，另加几项常用项）</summary>
    public static List<CleanItem> Targets() => new()
    {
        // ---- 临时与缓存 ----
        new CleanItem { Group = "临时文件与缓存", Name = "系统临时文件", Path = @"%SystemRoot%\Temp\*" },
        new CleanItem { Group = "临时文件与缓存", Name = "用户临时文件", Path = @"%TEMP%\*" },
        new CleanItem { Group = "临时文件与缓存", Name = "Windows\\Temp (全盘)", Path = @"%SystemDrive%\Windows\Temp\*" },
        new CleanItem { Group = "临时文件与缓存", Name = "缩略图缓存", Path = @"%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db" },
        new CleanItem { Group = "临时文件与缓存", Name = "D3D 着色器缓存", Path = @"%LocalAppData%\Local\D3DSCache\*" },
        new CleanItem { Group = "临时文件与缓存", Name = ".NET 程序集缓存(NativeImages 32)",
            Path = @"%WinDir%\assembly\NativeImages_v4.0.30319_32", DefaultOn = false,
            Note = "删除后 .NET 程序首次启动会变慢（Windows 会自动重建），仅在空间紧张时清理" },
        new CleanItem { Group = "临时文件与缓存", Name = ".NET 程序集缓存(NativeImages 64)",
            Path = @"%WinDir%\assembly\NativeImages_v4.0.30319_64", DefaultOn = false,
            Note = "删除后 .NET 程序首次启动会变慢（Windows 会自动重建），仅在空间紧张时清理" },
        new CleanItem { Group = "临时文件与缓存", Name = "WinSxS 临时文件", Path = @"%SystemRoot%\WinSxS\Temp\*" },
        new CleanItem { Group = "临时文件与缓存", Name = "预读取文件 Prefetch", Path = @"%SystemRoot%\Prefetch\*" },

        // ---- 浏览器与网络 ----
        new CleanItem { Group = "浏览器与网络", Name = "网页缓存 (INetCache)", Path = @"%LocalAppData%\Microsoft\Windows\INetCache\*" },
        new CleanItem { Group = "浏览器与网络", Name = "Cookies", Path = @"%LocalAppData%\Microsoft\Windows\INetCookies\*" },
        new CleanItem { Group = "浏览器与网络", Name = "IE/系统 历史记录", Path = @"%LocalAppData%\Microsoft\Windows\History\*" },
        new CleanItem { Group = "浏览器与网络", Name = "远程桌面连接缓存", Path = @"%LocalAppData%\Microsoft\Terminal Server Client\Cache\*" },
        new CleanItem { Group = "浏览器与网络", Name = "DNS 解析缓存",
            ExtraCmd = "ipconfig /flushdns", DefaultOn = false },

        // ---- Windows 更新 ----
        new CleanItem { Group = "Windows 更新", Name = "更新下载缓存 (SoftwareDistribution)",
            Path = @"%SystemRoot%\SoftwareDistribution\Download\*", DefaultOn = false },
        new CleanItem { Group = "Windows 更新", Name = "传递优化缓存",
            Path = @"%SystemRoot%\SoftwareDistribution\DeliveryOptimization\*", DefaultOn = false },
        new CleanItem { Group = "Windows 更新", Name = "过时的 WinSxS 组件",
            ExtraCmd = "dism /Online /Cleanup-Image /StartComponentCleanup", DefaultOn = false,
            HighRisk = false },

        // ---- 日志与诊断数据 ----
        new CleanItem { Group = "日志与诊断", Name = "Windows 日志", Path = @"%SystemRoot%\Logs\*", DefaultOn = false },
        new CleanItem { Group = "日志与诊断", Name = "错误报告 (WER)", Path = @"%ProgramData%\Microsoft\Windows\WER\ReportQueue\*" },
        new CleanItem { Group = "日志与诊断", Name = "诊断数据", Path = @"%ProgramData%\Microsoft\Diagnosis\*" },
        new CleanItem { Group = "日志与诊断", Name = "崩溃转储 minidump", Path = @"%SystemRoot%\Minidump\*.dmp", DefaultOn = false },
        new CleanItem { Group = "日志与诊断", Name = "系统内存转储 memory.dmp", Path = @"%SystemRoot%\memory.dmp", DefaultOn = false },
        new CleanItem { Group = "日志与诊断", Name = "Defender 扫描记录", Path = @"%ProgramData%\Microsoft\Windows Defender\Scans\*", DefaultOn = false },

        // ---- 回收站 ----
        new CleanItem { Group = "回收站", Name = "回收站（所有用户）", Path = @"%SystemDrive%\$Recycle.bin\*",
            DefaultOn = false, HighRisk = true },
    };

    /// <summary>统计一个清理目标的占用大小（字节）。目录用递归求和，返回 -1 表示无法访问</summary>
    public static long MeasureSize(CleanItem it)
    {
        try
        {
            if (it.IsCommandOnly) return -1;
            string p = it.Expanded;
            bool wild = p.Contains('*') || p.Contains('?');

            if (!wild)
            {
                if (Directory.Exists(p)) return DirSize(p);
                if (File.Exists(p)) return new FileInfo(p).Length;
                return 0;
            }

            // 通配：拆出目录与模式
            int i = p.LastIndexOf('\\');
            if (i <= 0) return -1;
            string dir = p.Substring(0, i);
            string pat = p.Substring(i + 1);

            if (!Directory.Exists(dir)) return 0;
            if (pat.Contains('*') && !pat.Contains('.'))
                return DirSize(dir);      // 形如 dir\*

            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pat, SearchOption.TopDirectoryOnly))
                    try { total += new FileInfo(f).Length; } catch { }
                // 形如 dir\* 也把子目录算进来
                foreach (var d in Directory.EnumerateDirectories(dir, pat, SearchOption.TopDirectoryOnly))
                    total += DirSize(d);
            }
            catch { }
            return total;
        }
        catch { return -1; }
    }

    public static long DirSize(string dir)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 20000)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                    try { total += new FileInfo(f).Length; } catch { }
                foreach (var s in Directory.EnumerateDirectories(d)) stack.Push(s);
            }
            catch { }
        }
        return total;
    }

    /// <summary>执行清理，返回 (释放字节数, 失败项)</summary>
    public static async Task<(long freed, int failed, List<string> notes)> CleanAsync(
        IEnumerable<CleanItem> items, IProgress<string> progress = null)
    {
        long freed = 0;
        int failed = 0;
        var notes = new List<string>();

        foreach (var it in items)
        {
            progress?.Report($"清理：{it.Name}");
            Log.Step($"清理 {it.Name}");

            long before = 0;
            try { before = MeasureSize(it); } catch { }
            if (before < 0) before = 0;

            if (it.IsCommandOnly)
            {
                var cmd = it.ExtraCmd;
                var r = await Cmd.Shell(cmd, 300000);
                if (r.Ok) Log.Ok($"  {it.Name} → 执行完成");
                else { failed++; Log.Warn($"  {it.Name} → 返回码 {r.ExitCode}：{Trim(r.All)}"); }
                continue;
            }

            string p = it.Expanded;
            bool wild = p.Contains('*') || p.Contains('?');
            int okFiles = 0, badFiles = 0;
            bool skippedSelf = false;

            try
            {
                if (!wild && Directory.Exists(p))
                {
                    // 整个目录 —— 清空内容而不是删目录本身（除 NativeImages）
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        TryDeleteFile(f, ref okFiles, ref badFiles, ref skippedSelf);
                    foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories))
                        try { Directory.Delete(d, false); } catch { }
                }
                else
                {
                    int i = p.LastIndexOf('\\');
                    string dir = i > 0 ? p.Substring(0, i) : p;
                    string pat = i > 0 ? p.Substring(i + 1) : "*";
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, pat, SearchOption.TopDirectoryOnly))
                            TryDeleteFile(f, ref okFiles, ref badFiles, ref skippedSelf);
                        if (pat == "*")
                            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                            {
                                long sz = DirSize(d);
                                try { Directory.Delete(d, true); freed += sz; okFiles++; }
                                catch { badFiles++; }
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"  {it.Name} 遍历异常：{ex.Message}");
            }

            long after = 0;
            try { after = MeasureSize(it); } catch { }
            if (after < 0) after = 0;
            long got = Math.Max(0, before - after);
            freed += got;

            if (skippedSelf) notes.Add($"{it.Name}：跳过了本工具自身日志目录");

            if (badFiles > 0 && okFiles == 0)
            {
                failed++;
                Log.Warn($"  {it.Name} → {badFiles} 个文件被占用或无权限；释放 {Fmt(got)}");
            }
            else
            {
                Log.Ok($"  {it.Name} → 删除 {okFiles} 个" +
                       (badFiles > 0 ? $"，{badFiles} 个占用跳过" : "") +
                       $"，释放 {Fmt(got)}");
            }
        }

        return (freed, failed, notes);
    }

    private static void TryDeleteFile(string f, ref int ok, ref int bad, ref bool skippedSelf)
    {
        // 不要把自己的日志删掉（否则用户看不到记录）
        try
        {
            var logDir = Path.GetDirectoryName(Log.FilePath);
            if (!string.IsNullOrEmpty(logDir) &&
                f.StartsWith(logDir, StringComparison.OrdinalIgnoreCase))
            {
                skippedSelf = true;
                return;
            }
        }
        catch { }

        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { File.Delete(f); ok++; }
        catch { bad++; }
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", "").Replace("\n", " | ").Trim();
        return s.Length <= 160 ? s : s.Substring(0, 160) + "…";
    }

    public static string Fmt(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
