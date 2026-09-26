using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;

namespace NetDoctor.Core;

internal sealed class AppxItem
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool NonRemovable { get; set; }
    public string SizeText => JunkCleaner.Fmt(SizeBytes);
}

internal sealed class SvcItem
{
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartType { get; set; } = "";
    public string StartNum { get; set; } = "";     // 2/3/4/5
    public bool Running { get; set; }
    public string Path { get; set; } = "";
    public bool Microsoft { get; set; }
}

internal sealed class StartupItem
{
    public string Source { get; set; } = "";       // 注册表位置 / 启动文件夹 / 计划任务
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string State { get; set; } = "";        // 启用/禁用
    public string RegKey { get; set; } = "";
    public string RegValue { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CanToggle { get; set; } = true;
    public string Kind { get; set; } = "";         // Run / Startup / Task
    public string FilePath { get; set; } = "";
}

internal static class SystemItems
{
    private static string Script => EmbeddedScripts.PathOf("sysitems.ps1");

    private static async Task<JsonElement> RunJson(string mode)
    {
        if (!File.Exists(Script))
            throw new FileNotFoundException("内嵌脚本释放失败", Script);
        var r = await Cmd.RunAsync("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Script}\" -Mode {mode}", 180000);
        var txt = r.All.Trim();
        if (txt.Length == 0) return default;
        int i = txt.IndexOf('[');
        int j = txt.IndexOf('{');
        int start = i < 0 ? j : (j < 0 ? i : Math.Min(i, j));
        if (start < 0) return default;
        var json = txt.Substring(start);
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return default; }
    }

    // ---------------- Appx ----------------
    public static async Task<List<AppxItem>> AppxAsync()
    {
        var list = new List<AppxItem>();
        var root = await RunJson("appx");
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var e in root.EnumerateArray())
            {
                list.Add(new AppxItem
                {
                    Name = Get(e, "Name"),
                    FullName = Get(e, "FullName"),
                    Version = Get(e, "Version"),
                    Publisher = Short(Get(e, "Publisher")),
                    SizeBytes = GetLong(e, "SizeBytes"),
                    NonRemovable = GetBool(e, "NonRemovable"),
                });
            }
        return list.OrderByDescending(a => a.SizeBytes).ToList();
    }

    public static async Task<(int ok, int fail, List<string> failedNames)> UninstallAppxAsync(
        IEnumerable<AppxItem> items, IProgress<string> progress = null)
    {
        int ok = 0, fail = 0;
        var failed = new List<string>();
        foreach (var it in items)
        {
            progress?.Report("卸载：" + it.Name);
            Log.Step($"卸载应用 {it.Name} ({it.SizeText})");
            var ps =
                $"Get-AppxPackage -AllUsers -Name '{it.Name}' | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; " +
                $"Get-AppxProvisionedPackage -Online | Where-Object {{$_.DisplayName -eq '{it.Name}'}} | " +
                "Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null; " +
                $"Write-Output 'DONE:{it.Name}'";
            var r = await Cmd.RunAsync("powershell", "-NoProfile -Command \"" + ps + "\"", 180000);
            bool done = r.All.Contains("DONE:") && !r.All.Contains("Exception");
            // 复核：是否已不存在
            var check = await Cmd.RunAsync("powershell",
                $"-NoProfile -Command \"if (Get-AppxPackage -Name '{it.Name}' -ErrorAction SilentlyContinue) {{ 'STILL' }} else {{ 'GONE' }}\"",
                60000);
            if (check.All.Contains("GONE")) { ok++; Log.Ok($"  {it.Name} 已卸载"); }
            else
            {
                fail++; failed.Add(it.Name);
                Log.Warn($"  {it.Name} 未能卸载（可能是系统应用或需重启）");
            }
        }
        return (ok, fail, failed);
    }

    // ---------------- 服务 ----------------
    public static List<SvcItem> Services(bool includeMicrosoft = true)
    {
        var list = new List<SvcItem>();
        try
        {
            foreach (var sc in ServiceController.GetServices())
            {
                string startNum = "", path = "", display = "";
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}");
                    if (k != null)
                    {
                        startNum = (k.GetValue("Start") ?? "").ToString();
                        path = (k.GetValue("ImagePath") ?? "").ToString();
                    }
                }
                catch { }
                try { display = sc.DisplayName; } catch { }

                bool ms = path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
                       || path.StartsWith("\\SystemRoot", StringComparison.OrdinalIgnoreCase);

                var item = new SvcItem
                {
                    Name = sc.ServiceName,
                    Display = string.IsNullOrWhiteSpace(display) ? sc.ServiceName : display,
                    Running = sc.Status == ServiceControllerStatus.Running,
                    Status = sc.Status switch
                    {
                        ServiceControllerStatus.Running => "运行中",
                        ServiceControllerStatus.Stopped => "已停止",
                        ServiceControllerStatus.StartPending => "启动中",
                        ServiceControllerStatus.StopPending => "停止中",
                        ServiceControllerStatus.Paused => "已暂停",
                        _ => sc.Status.ToString(),
                    },
                    StartNum = startNum,
                    StartType = startNum switch
                    {
                        "0" => "引导启动", "1" => "系统启动", "2" => "自动",
                        "3" => "手动", "4" => "已禁用", "5" => "延迟自动", _ => "?"
                    },
                    Path = path,
                    Microsoft = ms,
                };
                if (!includeMicrosoft && ms) continue;
                list.Add(item);
            }
        }
        catch (Exception ex) { Log.Warn("枚举服务失败：" + ex.Message); }
        return list.OrderByDescending(s => s.Running).ThenBy(s => s.Display).ToList();
    }

    public static async Task<bool> SetServiceAsync(SvcItem s, string startNum)
    {
        bool ok = await Log.RunLogged($"服务 {s.Name} 启动类型 → {startNum}",
            "sc", $"config {s.Name} start= {startNum}", 30000);
        if (startNum == "4" && s.Running)
            await Cmd.RunAsync("sc", $"stop {s.Name}", 40000);
        return ok;
    }

    public static async Task<bool> ControlServiceAsync(SvcItem s, bool start)
    {
        var r = await Cmd.RunAsync("sc", $"{(start ? "start" : "stop")} {s.Name}", 60000);
        if (r.Ok) return true;
        // 已处于目标状态也算成功
        var txt = r.All;
        return txt.Contains("已经启动") || txt.Contains("already been started")
            || txt.Contains("没有启动") || txt.Contains("not started")
            || txt.Contains("1056") /* 已在运行 */ || txt.Contains("1062");
    }

    // ---------------- 启动项 ----------------
    private static readonly (string Hive, string Path, string Label)[] RunKeys =
    {
        ( "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run",        "当前用户 · Run" ),
        ( "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce",    "当前用户 · RunOnce" ),
        ( "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run",        "所有用户 · Run" ),
        ( "HKLM", @"Software\Microsoft\Windows\CurrentVersion\RunOnce",    "所有用户 · RunOnce" ),
        ( "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",     "所有用户 · Run (32位)" ),
        ( "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "所有用户 · RunOnce (32位)" ),
    };

    public static List<StartupItem> Startup()
    {
        var list = new List<StartupItem>();

        // 注册表 Run 项
        foreach (var (hive, path, label) in RunKeys)
        {
            try
            {
                var root = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using var k = root.OpenSubKey(path, false);
                if (k == null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var val = k.GetValue(name)?.ToString() ?? "";
                    list.Add(new StartupItem
                    {
                        Kind = "Run",
                        Source = label,
                        Name = name,
                        Command = val,
                        State = "启用",
                        Enabled = true,
                        CanToggle = true,
                        RegKey = (hive == "HKCU" ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\") + path,
                        RegValue = name,
                    });
                }
            }
            catch { }
        }

        // 启动文件夹
        foreach (var (folder, label) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "启动文件夹 · 当前用户"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "启动文件夹 · 所有用户"),
        })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var fn = Path.GetFileName(f);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new StartupItem
                    {
                        Kind = "Startup",
                        Source = label,
                        Name = Path.GetFileNameWithoutExtension(fn),
                        Command = f,
                        State = "启用",
                        Enabled = true,
                        CanToggle = true,
                        FilePath = f,
                    });
                }
            }
            catch { }
        }

        return list;
    }

    /// <summary>禁用/启用注册表启动项（用 Windows 官方的 StartupApproved 机制，不改原键）</summary>
    public static async Task<bool> ToggleStartupAsync(StartupItem it, bool enable)
    {
        if (it.Kind == "Startup")
        {
            try
            {
                string dir = Path.GetDirectoryName(it.FilePath) ?? "";
                string name = Path.GetFileName(it.FilePath);
                string dis = Path.Combine(dir, name + ".disabled");
                if (enable)
                {
                    if (File.Exists(dis)) File.Move(dis, it.FilePath, true);
                }
                else
                {
                    if (File.Exists(it.FilePath)) File.Move(it.FilePath, dis, true);
                }
                Log.Ok($"启动文件夹项「{it.Name}」→ {(enable ? "已启用" : "已禁用")}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Err($"切换「{it.Name}」失败：{ex.Message}");
                return false;
            }
        }

        if (it.Kind != "Run")
        {
            await Task.CompletedTask;
            return false;
        }

        // StartupApproved：3 = 启用，2 = 禁用（未设置视为启用）
        try
        {
            bool hkcu = it.RegKey.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);
            string approvedLeaf = it.RegKey
                .Replace("HKEY_CURRENT_USER\\", "").Replace("HKEY_LOCAL_MACHINE\\", "");
            string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" +
                              (approvedLeaf.Contains("WOW6432Node") ? "Run32" : "Run");

            var root = hkcu ? Registry.CurrentUser : Registry.LocalMachine;
            using var k = root.CreateSubKey(approved, true);
            if (k == null) return false;

            var data = new byte[12];
            data[0] = (byte)(enable ? 2 : 3);   // 2=启用 3=禁用
            k.SetValue(it.RegValue, data, RegistryValueKind.Binary);
            Log.Ok($"启动项「{it.Name}」→ {(enable ? "已启用" : "已禁用")}（StartupApproved）");
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            Log.Err($"切换「{it.Name}」失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>读取 StartupApproved 状态，判断启动项当前是否启用</summary>
    public static void ApplyApprovedState(List<StartupItem> items)
    {
        foreach (var it in items.Where(i => i.Kind == "Run"))
        {
            try
            {
                bool hkcu = it.RegKey.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);
                string approvedLeaf = it.RegKey
                    .Replace("HKEY_CURRENT_USER\\", "").Replace("HKEY_LOCAL_MACHINE\\", "");
                string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" +
                                  (approvedLeaf.Contains("WOW6432Node") ? "Run32" : "Run");
                var root = hkcu ? Registry.CurrentUser : Registry.LocalMachine;
                using var k = root.OpenSubKey(approved, false);
                if (k == null) continue;
                if (k.GetValue(it.RegValue) is byte[] b && b.Length > 0)
                {
                    bool enabled = (b[0] & 1) == 0 && b[0] != 3;
                    it.Enabled = enabled;
                    it.State = enabled ? "启用" : "已禁用";
                }
            }
            catch { }
        }
    }

    // ---------------- 计划任务 ----------------
    public static async Task<List<StartupItem>> TasksAsync()
    {
        var list = new List<StartupItem>();
        var root = await RunJson("tasks");
        if (root.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in root.EnumerateArray())
        {
            string path = Get(e, "TaskPath");
            string name = Get(e, "TaskName");
            string state = Get(e, "State");
            string trig = Get(e, "Triggers");
            string action = Get(e, "Action");
            string author = Get(e, "Author");

            bool bootOrLogon = trig.Contains("Boot") || trig.Contains("Logon")
                            || trig.Contains("Startup") || trig.Contains("Registration");
            bool ms = author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase);

            list.Add(new StartupItem
            {
                Kind = "Task",
                Source = "计划任务" + (ms ? " · 系统" : ""),
                Name = (path == "\\" ? "" : path.TrimStart('\\')) + name,
                Command = string.IsNullOrWhiteSpace(action) ? "(见任务计划程序)" : action,
                State = state,
                Enabled = state.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                       || state.Equals("Running", StringComparison.OrdinalIgnoreCase),
                CanToggle = !ms,
                FilePath = path + "|" + name,
            });
        }
        return list;
    }

    public static async Task<bool> ToggleTaskAsync(StartupItem it, bool enable)
    {
        var parts = (it.FilePath ?? "").Split('|');
        if (parts.Length != 2) return false;
        string tn = parts[1].Replace("'", "''");
        var r = await Cmd.RunAsync("powershell",
            $"-NoProfile -Command \"{(enable ? "Enable" : "Disable")}-ScheduledTask -TaskName '{tn}' -ErrorAction Stop; 'OK'\"",
            60000);
        bool ok = r.All.Contains("OK");
        if (ok) Log.Ok($"计划任务「{it.Name}」→ {(enable ? "已启用" : "已禁用")}");
        else Log.Warn($"计划任务「{it.Name}」切换失败：{r.All.Trim()}");
        return ok;
    }

    // ---------------- 工具 ----------------
    private static string Get(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        if (!e.TryGetProperty(k, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "",
            _ => v.ToString(),
        };
    }

    private static long GetLong(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        if (!e.TryGetProperty(k, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
        long.TryParse(v.ToString(), out long p);
        return p;
    }

    private static bool GetBool(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty(k, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string pub)
    {
        if (string.IsNullOrEmpty(pub)) return "";
        int i = pub.IndexOf(", CN=");
        if (i > 0) return pub.Substring(i + 4);
        return pub.Length <= 40 ? pub : pub.Substring(0, 40) + "…";
    }
}
