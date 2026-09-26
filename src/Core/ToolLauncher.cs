using System.Diagnostics;
using System.Text;

namespace NetDoctor.Core;

internal sealed class ToolEntry
{
    public string Category = "";
    public string Name = "";
    public string Exe = "";
    public string WorkDir = "";
    public bool IsLink;
    public string Note = "";
    public override string ToString() => Name;
}

/// <summary>工具启动器：扫描图吧工具箱的 tools 目录，按分类列出并启动</summary>
internal static class ToolLauncher
{
    private static readonly string[] Excludes =
    {
        "unins", "uninst", "setup_", "vcredist", "dotnet", "readme", "license",
        "更新", "检查更新", "广告", "流量卡", "加速器", "changelog", "说明"
    };

    private static readonly string[] KnownRoots =
    {
        @"D:\github\图吧工具箱202608\tools",
        @"C:\图吧工具箱\tools",
        @"D:\图吧工具箱\tools",
        @"E:\图吧工具箱\tools",
    };

    public static string Root;
    private static List<ToolEntry> _cache;

    public static string FindRoot()
    {
        if (!string.IsNullOrEmpty(Root) && Directory.Exists(Root)) return Root;
        foreach (var r in KnownRoots)
            if (Directory.Exists(r)) { Root = r; return r; }
        // 在常见盘符上找一层
        foreach (var drive in new[] { "C:", "D:", "E:", "F:" })
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(drive + "\\"))
                {
                    var nm = Path.GetFileName(d);
                    if (!nm.Contains("图吧")) continue;
                    var t = Path.Combine(d, "tools");
                    if (Directory.Exists(t)) { Root = t; return t; }
                }
            }
            catch { }
        }
        return null;
    }

    public static List<ToolEntry> Scan(bool force = false)
    {
        if (!force && _cache != null) return _cache;
        var list = new List<ToolEntry>();
        var root = FindRoot();
        if (root == null) { _cache = list; return list; }

        foreach (var catDir in Directory.EnumerateDirectories(root))
        {
            string cat = Path.GetFileName(catDir);
            if (cat.Equals("常用工具", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var toolDir in Directory.EnumerateDirectories(catDir))
            {
                string name = Path.GetFileName(toolDir);
                var files = Directory.EnumerateFiles(toolDir)
                                     .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                                     .ToList();

                // 优先选与目录同名、或名字最像主程序的那个
                var pick = files
                    .Where(f => !Excludes.Any(x => Path.GetFileName(f).Contains(x, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f)
                        .Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => Path.GetFileName(f).Length)
                    .FirstOrDefault();

                if (pick == null)
                {
                    if (files.Count == 0)
                        list.Add(new ToolEntry { Category = cat, Name = name, Note = "空目录（本版本未附带）" });
                    continue;
                }

                list.Add(new ToolEntry
                {
                    Category = cat,
                    Name = name,
                    Exe = pick,
                    WorkDir = toolDir,
                    IsLink = pick.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                          || pick.EndsWith(".url", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        _cache = list;
        return list;
    }

    public static List<string> Categories()
        => Scan().Select(t => t.Category).Distinct().ToList();

    public static void Launch(ToolEntry t)
    {
        if (t == null || string.IsNullOrEmpty(t.Exe))
        {
            Log.Warn($"「{t?.Name}」没有可执行文件（本版本未附带该工具）");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(t.WorkDir) && Directory.Exists(t.WorkDir))
                psi.WorkingDirectory = t.WorkDir;
            Process.Start(psi);
            Log.Ok($"已启动 [ {t.Category} ] {t.Name}   →  {t.Exe}");
        }
        catch (Exception ex)
        {
            Log.Err($"启动「{t.Name}」失败：{ex.Message}");
            try
            {
                // 退一步：用资源管理器定位
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t.Exe}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    public static void OpenLocation(ToolEntry t)
    {
        try
        {
            if (!string.IsNullOrEmpty(t?.Exe) && File.Exists(t.Exe))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t.Exe}\"") { UseShellExecute = true });
            else if (!string.IsNullOrEmpty(t?.WorkDir) && Directory.Exists(t.WorkDir))
                Process.Start(new ProcessStartInfo(t.WorkDir) { UseShellExecute = true });
        }
        catch { }
    }
}

// =====================================================================
internal sealed class CpuBenchResult
{
    public double SingleMBps;
    public double MultiMBps;
    public int Threads;
    public double SingleSec, MultiSec;
    public double PrimeOps;
    public double PrimeSec;
}

internal sealed class DiskBenchResult
{
    public double WriteMBps;
    public double ReadMBps;
    public long SizeBytes;
    public string Path = "";
    public string Error = "";
}

internal static class Benchmarks
{
    /// <summary>CPU 跑分：SHA256 吞吐（单线程/多线程）+ 浮点运算</summary>
    public static async Task<CpuBenchResult> CpuAsync(IProgress<string> progress = null)
    {
        var res = new CpuBenchResult { Threads = Environment.ProcessorCount };
        var data = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        progress?.Report("CPU 单线程测试…");
        await Task.Run(() =>
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            sha.ComputeHash(data);                                  // 预热
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 16; i++) sha.ComputeHash(data);
            sw.Stop();
            res.SingleSec = sw.Elapsed.TotalSeconds;
            res.SingleMBps = 16.0 * data.Length / 1024 / 1024 / Math.Max(0.001, res.SingleSec);
        });

        progress?.Report($"CPU 多线程测试（{res.Threads} 线程）…");
        await Task.Run(() =>
        {
            int per = Math.Max(2, 16 / Math.Max(1, res.Threads / 4));
            var sw = Stopwatch.StartNew();
            Parallel.For(0, res.Threads, _ =>
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                for (int i = 0; i < per; i++) sha.ComputeHash(data);
            });
            sw.Stop();
            res.MultiSec = sw.Elapsed.TotalSeconds;
            res.MultiMBps = (double)res.Threads * per * data.Length / 1024 / 1024 / Math.Max(0.001, res.MultiSec);
        });

        progress?.Report("CPU 浮点运算测试…");
        await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            double x = 1.0, acc = 0;
            long ops = 0;
            for (int i = 0; i < 40_000_000; i++)
            {
                x = x * 1.0000001 + 0.0000001;
                if (x > 2.0) x -= 1.0;
                acc += x;
                ops++;
            }
            sw.Stop();
            if (acc < 0) Console.WriteLine(acc);   // 防止被优化掉
            res.PrimeOps = ops / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1_000_000;
            res.PrimeSec = sw.Elapsed.TotalSeconds;
        });

        return res;
    }

    /// <summary>磁盘测速：顺序写 + 顺序读</summary>
    public static async Task<DiskBenchResult> DiskAsync(string dir, int sizeMB = 256,
        IProgress<string> progress = null)
    {
        var res = new DiskBenchResult { SizeBytes = (long)sizeMB * 1024 * 1024 };
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            res.Error = "目录不存在：" + dir;
            return res;
        }
        res.Path = dir;
        string file = Path.Combine(dir, $"nd_bench_{Guid.NewGuid():N}.tmp");
        var buf = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(buf);

        try
        {
            progress?.Report("磁盘顺序写入测试…");
            await Task.Run(() =>
            {
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None,
                    buf.Length, FileOptions.WriteThrough);
                var sw = Stopwatch.StartNew();
                long written = 0;
                while (written < res.SizeBytes)
                {
                    fs.Write(buf, 0, buf.Length);
                    written += buf.Length;
                }
                fs.Flush(true);
                sw.Stop();
                res.WriteMBps = written / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            });

            // 让写缓存落盘一点
            await Task.Delay(500);

            progress?.Report("磁盘顺序读取测试…");
            await Task.Run(() =>
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None,
                    buf.Length, FileOptions.None);
                var sw = Stopwatch.StartNew();
                long read = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) read += n;
                sw.Stop();
                res.ReadMBps = read / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            });
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
        return res;
    }
}
