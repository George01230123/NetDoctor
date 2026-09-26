using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace NetDoctor.Core;

/// <summary>硬件信息：全部来自 WMI/CIM + 注册表，自己实现，不依赖外部工具</summary>
internal static class HardwareInfo
{
    private static JsonElement _cache;
    private static DateTime _cacheAt = DateTime.MinValue;

    public static async Task<JsonElement> GatherAsync(bool force = false)
    {
        if (!force && _cache.ValueKind == JsonValueKind.Object
            && (DateTime.Now - _cacheAt).TotalMinutes < 10)
            return _cache;

        var script = EmbeddedScripts.PathOf("sysitems.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("内嵌脚本释放失败", script);

        var r = await Cmd.RunAsync("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Mode hw", 180000);
        var txt = r.All.Trim();
        int i = txt.IndexOf('{');
        if (i < 0) throw new InvalidOperationException("硬件检测无输出：" + txt);
        var json = txt.Substring(i);
        var doc = JsonDocument.Parse(json);
        _cache = doc.RootElement.Clone();
        _cacheAt = DateTime.Now;
        return _cache;
    }

    public static string Str(JsonElement e, string k)
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

    public static long Num(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        if (!e.TryGetProperty(k, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
        long.TryParse(v.ToString(), out long p);
        return p;
    }

    /// <summary>
    /// 显卡显存：WMI 的 AdapterRAM 是 32 位，超过 4GB 会被截断（RTX 5060 Ti 8G 读出来只有 4GB），
    /// 所以改从显卡类注册表读 HardwareInformation.qwMemorySize。
    /// 注意 MatchingDeviceId 形如 pci\ven_10de&amp;dev_2d04，而 PNPDeviceID 后面还有 SUBSYS 等，
    /// 因此必须按 VEN_/DEV_ 前缀匹配，不能用 EndsWith。
    /// </summary>
    public static long GpuVramFromRegistry(string pnpId)
    {
        try
        {
            if (string.IsNullOrEmpty(pnpId)) return 0;

            // 从 PNPDeviceID 里抽出 VEN_xxxx&DEV_xxxx
            var m = System.Text.RegularExpressions.Regex.Match(pnpId,
                @"VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string venDev = m.Success ? m.Value.ToLowerInvariant() : null;

            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (k == null) return 0;

            long best = 0;
            foreach (var sub in k.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var s = k.OpenSubKey(sub);
                if (s == null) continue;

                string mid = s.GetValue("MatchingDeviceId")?.ToString() ?? "";
                bool hit = venDev != null
                    ? mid.ToLowerInvariant().Contains(venDev)
                    : (!string.IsNullOrEmpty(mid) && pnpId.EndsWith(mid, StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;

                long v = 0;
                if (s.GetValue("HardwareInformation.qwMemorySize") is long q) v = q;
                else if (s.GetValue("HardwareInformation.qwMemorySize") is int qi) v = qi;
                if (v <= 0 && s.GetValue("HardwareInformation.MemorySize") is byte[] b && b.Length >= 4)
                    v = BitConverter.ToUInt32(b, 0);
                if (v > best) best = v;
            }
            return best;
        }
        catch { }
        return 0;
    }

    // ---------------------------------------------------------------
    /// <summary>生成可复制/可导出的完整报告</summary>
    public static string BuildReport(JsonElement hw)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==================== 图吧风格硬件检测报告 ====================");
        sb.AppendLine($"检测时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"工具：夕颜若雪网络工具 硬件检测模块");
        sb.AppendLine();

        if (hw.TryGetProperty("system", out var sys))
        {
            sb.AppendLine("【系统 / 整机】");
            Row(sb, "操作系统", Str(sys, "Caption") + "  " + Str(sys, "Version") + " (Build " + Str(sys, "Build") + ")");
            Row(sb, "系统架构", Str(sys, "Arch"));
            Row(sb, "计算机名", Str(sys, "ComputerName") + "   用户：" + Str(sys, "UserName"));
            Row(sb, "整机品牌", Str(sys, "Manufacturer"));
            Row(sb, "整机型号", Str(sys, "Model"));
            Row(sb, "主板厂商", Str(sys, "BoardVendor"));
            Row(sb, "主板型号", Str(sys, "BoardProduct"));
            Row(sb, "BIOS", Str(sys, "BiosVendor") + "  " + Str(sys, "BiosVersion") + "   " + FmtBiosDate(Str(sys, "BiosDate")));
            Row(sb, "主板序列号", Str(sys, "BoardSerial"));
            Row(sb, "SMBIOS UUID", Str(sys, "UUID"));
            Row(sb, "物理内存总量", JunkCleaner.Fmt(Num(sys, "TotalRamBytes")));
            Row(sb, "安全启动", Str(sys, "SecureBoot"));
            Row(sb, "系统安装时间", Str(sys, "InstallDate"));
            Row(sb, "运行时长", Str(sys, "Uptime"));
            sb.AppendLine();
        }

        if (hw.TryGetProperty("cpu", out var cpus) && cpus.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【处理器】");
            int n = 0;
            foreach (var c in cpus.EnumerateArray())
            {
                if (cpus.GetArrayLength() > 1) sb.AppendLine($"  物理处理器 #{++n}");
                Row(sb, "型号", Str(c, "Name").Trim());
                Row(sb, "厂商", Str(c, "Manufacturer"));
                Row(sb, "核心 / 线程", $"{Num(c, "Cores")} 核 / {Num(c, "Threads")} 线程");
                Row(sb, "主频", $"最大 {Num(c, "MaxClockMHz")} MHz    当前 {Num(c, "CurrentMHz")} MHz    外频 {Num(c, "ExtClockMHz")} MHz");
                Row(sb, "缓存", $"L2 {Num(c, "L2KB") / 1024.0:0.#} MB    L3 {Num(c, "L3KB") / 1024.0:0.#} MB");
                Row(sb, "封装 / 架构", $"{Str(c, "Socket")}    {Str(c, "Architecture")} 位");
                Row(sb, "虚拟化(VT)", Str(c, "Virtualization"));
                Row(sb, "当前负载", Str(c, "LoadPercent") + " %");
            }
            sb.AppendLine();
        }

        if (hw.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【内存】");
            foreach (var m in mem.EnumerateArray())
            {
                var cap = Num(m, "CapacityBytes");
                string speed = $"{Num(m, "SpeedMHz")} MHz";
                var cfg = Num(m, "ConfiguredMHz");
                if (cfg > 0 && cfg != Num(m, "SpeedMHz")) speed += $"（实际运行 {cfg} MHz）";
                Row(sb, Str(m, "DeviceLocator") + " / " + Str(m, "Bank"),
                    $"{JunkCleaner.Fmt(cap)}  {Str(m, "Type")}  {speed}  {Str(m, "FormFactor")}");
                Row(sb, "  品牌 / 型号", $"{Str(m, "Manufacturer")}   {Str(m, "PartNumber")}");
                var v = Num(m, "VoltageMV");
                if (v > 0) Row(sb, "  电压", $"{v / 1000.0:0.00} V");
            }
            int cnt = mem.GetArrayLength();
            long total = 0;
            foreach (var m in mem.EnumerateArray()) total += Num(m, "CapacityBytes");
            sb.AppendLine($"  → 共 {cnt} 条，合计 {JunkCleaner.Fmt(total)}" +
                          (cnt == 1 ? "  ⚠ 单通道（只插了一条内存，性能受限）" : "  双通道/多通道"));
            sb.AppendLine();
        }

        if (hw.TryGetProperty("gpu", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【显卡】");
            foreach (var g in gpus.EnumerateArray())
            {
                Row(sb, "型号", Str(g, "Name"));
                long vram = GpuVramFromRegistry(Str(g, "PNPDeviceID"));
                if (vram <= 0) vram = Num(g, "AdapterRam");
                Row(sb, "显存", vram > 0 ? JunkCleaner.Fmt(vram) : "读取失败");
                Row(sb, "驱动版本", Str(g, "DriverVersion"));
                Row(sb, "当前分辨率", Str(g, "Resolution") + " @ " + Str(g, "RefreshRate") + " Hz   " + Str(g, "BitsPerPixel") + " bit");
            }
            sb.AppendLine();
        }

        if (hw.TryGetProperty("disk", out var disks) && disks.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【硬盘 / 存储】");
            foreach (var d in disks.EnumerateArray())
            {
                Row(sb, $"磁盘 {Str(d, "Index")}", Str(d, "Model"));
                Row(sb, "  容量 / 接口", $"{JunkCleaner.Fmt(Num(d, "SizeBytes"))}   {Str(d, "Interface")}   {Str(d, "MediaType")}");
                Row(sb, "  健康状态", Str(d, "Health") + (Str(d, "Status") == "OK" ? "" : "  (" + Str(d, "Status") + ")"));
                var t = Num(d, "Temperature");
                if (t > 0) Row(sb, "  温度", t + " °C" + (t >= 60 ? "   ⚠ 偏高" : ""));
                var h = Num(d, "PowerOnHours");
                if (h > 0) Row(sb, "  通电时间", h + " 小时（约 " + (h / 24.0 / 365.0).ToString("0.0") + " 年）");
                var w = Num(d, "Wear");
                if (w > 0) Row(sb, "  磨损度", w + " %");
                if (!string.IsNullOrWhiteSpace(Str(d, "Firmware"))) Row(sb, "  固件", Str(d, "Firmware"));
            }
            sb.AppendLine();
        }

        if (hw.TryGetProperty("volume", out var vols) && vols.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【分区】");
            foreach (var v in vols.EnumerateArray())
            {
                long size = Num(v, "SizeBytes"), free = Num(v, "FreeBytes");
                double used = size > 0 ? 100.0 * (size - free) / size : 0;
                Row(sb, Str(v, "Drive") + " " + Str(v, "Label"),
                    $"{Str(v, "FileSystem")}  {Str(v, "DriveType")}  总 {JunkCleaner.Fmt(size)}  " +
                    $"可用 {JunkCleaner.Fmt(free)}  已用 {used:0.#}%" +
                    (size > 0 && used > 92 ? "   ⚠ 空间紧张" : ""));
            }
            sb.AppendLine();
        }

        if (hw.TryGetProperty("monitor", out var mons) && mons.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("【显示器】");
            var sizes = hw.TryGetProperty("monitorSize", out var ms) && ms.ValueKind == JsonValueKind.Array
                ? ms.EnumerateArray().ToList() : new List<JsonElement>();
            int idx = 0;
            foreach (var m in mons.EnumerateArray())
            {
                Row(sb, "显示器 " + (idx + 1), Str(m, "FriendlyName"));
                Row(sb, "  厂商代码 / 面板", Str(m, "Manufacturer") + "   " + Str(m, "ProductCode"));
                Row(sb, "  生产日期", Str(m, "YearOfMfg") + " 年 第 " + Str(m, "WeekOfMfg") + " 周");
                Row(sb, "  序列号", Str(m, "SerialNumber"));
                if (idx < sizes.Count)
                {
                    var s = sizes[idx];
                    Row(sb, "  物理尺寸", $"{Str(s, "WidthCm")} × {Str(s, "HeightCm")} cm  ≈ {Str(s, "DiagonalInch")} 英寸");
                }
                idx++;
            }
            sb.AppendLine();
        }

        foreach (var (key, title) in new[] { ("net", "【网络适配器】"), ("sound", "【声卡】") })
        {
            if (!hw.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            sb.AppendLine(title);
            foreach (var x in arr.EnumerateArray())
                Row(sb, Str(x, "Name"), key == "net"
                    ? $"{Str(x, "Status")}   {Str(x, "LinkSpeed")}   {Str(x, "Mac")}   {Str(x, "Desc")}"
                    : Str(x, "Manufacturer") + "  " + Str(x, "Status"));
            sb.AppendLine();
        }

        if (hw.TryGetProperty("battery", out var bats) && bats.ValueKind == JsonValueKind.Array && bats.GetArrayLength() > 0)
        {
            sb.AppendLine("【电池】");
            foreach (var b in bats.EnumerateArray())
            {
                Row(sb, Str(b, "Name"), $"电量 {Str(b, "ChargePercent")}%");
                long full = Num(b, "FullCapacityMWh"), design = Num(b, "DesignCapacityMWh");
                if (full > 0 && design > 0)
                    Row(sb, "  健康度", $"{100.0 * full / design:0.#}%   （设计 {design} mWh / 当前满充 {full} mWh）");
                var c = Num(b, "CycleCount");
                if (c > 0) Row(sb, "  循环次数", c.ToString());
            }
            sb.AppendLine();
        }

        if (hw.TryGetProperty("temp", out var temps) && temps.ValueKind == JsonValueKind.Array && temps.GetArrayLength() > 0)
        {
            sb.AppendLine("【ACPI 温度区】");
            foreach (var t in temps.EnumerateArray())
                Row(sb, Str(t, "Zone"), Str(t, "Celsius") + " °C");
            sb.AppendLine();
        }

        sb.AppendLine("==============================================================");
        sb.AppendLine("说明：数据来自 WMI/CIM 与注册表；笔记本的显存、部分传感器可能读不到。");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string v)
    {
        if (string.IsNullOrWhiteSpace(v)) v = "—";
        sb.AppendLine($"  {k,-16}: {v.Trim()}");
    }

    private static string FmtBiosDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // 形如 /Date(1234567890000)/ 或 20240101000000.000000+000
        if (raw.StartsWith("/Date(") && raw.Contains(")/"))
        {
            var s = raw.Substring(6, raw.IndexOf(")/", StringComparison.Ordinal) - 6);
            if (long.TryParse(s, out long ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd");
        }
        if (raw.Length >= 8 && raw.All(char.IsDigit)) return raw.Substring(0, 4) + "-" + raw.Substring(4, 2) + "-" + raw.Substring(6, 2);
        return raw;
    }

    /// <summary>取一行摘要，用于概览卡</summary>
    public static string SummaryLine(JsonElement hw)
    {
        var parts = new List<string>();
        try
        {
            if (hw.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
                parts.Add(Str(c[0], "Name").Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Trim());
            if (hw.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0)
                parts.Add(Str(g[0], "Name"));
            if (hw.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                long tot = 0; foreach (var x in m.EnumerateArray()) tot += Num(x, "CapacityBytes");
                parts.Add(JunkCleaner.Fmt(tot) + " " + Str(m[0], "Type"));
            }
        }
        catch { }
        return string.Join("  |  ", parts);
    }
}
