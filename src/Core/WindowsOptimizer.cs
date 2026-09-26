using System.Xml.Linq;
using Microsoft.Win32;

namespace NetDoctor.Core;

internal enum OpKind { RegWrite, RegDelete, SetServiceStart, ExplorerNotify }

internal sealed class OptOp
{
    public OpKind Kind;
    public string Key = "";
    public string Value = "";
    public string Type = "";
    public string Data = "";
    public bool Wow64;
    public bool SkipError;
    public string Name = "";     // SetServiceStart 的服务名
    public string SvcType = "";  // SetServiceStart 的目标启动类型
    public string Cmd = "";      // ExplorerNotify 的命令
    public string NType = "";    // ExplorerNotify 的类型
}

internal sealed class OptItem
{
    public string Category = "";
    public string Name = "";
    public string CheckKey = "";
    public string CheckValue = "";
    public string OptimizedValue = "";
    public readonly List<OptOp> Apply = new();
    public readonly List<OptOp> Restore = new();

    /// <summary>键或值名为空 —— 这类是「整键存在性」判定，无法自动识别状态</summary>
    public bool IsKeyExistenceCheck => string.IsNullOrEmpty(CheckValue);

    public bool HighRisk =>
        Name.Contains("防火墙") || Name.Contains("内存完整") || Name.Contains("虚拟化安全")
        || Name.Contains("关闭系统还原") || Name.Contains("UserInit")
        || Name.Contains("Windows 更新") && Name.Contains("从不");

    public override string ToString() => Name;
}

internal sealed class OptStatus
{
    public OptItem Item;
    public bool Applied;      // true=已优化  false=未优化
    public bool Unknown;      // 无法判定
    public string Current = "—";
}

internal static class WindowsOptimizer
{
    private static List<OptItem> _items;
    private static readonly object _lock = new();

    public static string[] CategoryOrder =
    {
        "xingneng", "explorer", "yinsi", "system", "edge", "update", "safe"
    };

    public static string CategoryTitle(string c) => c switch
    {
        "xingneng" => "性能优化",
        "explorer" => "外观 / 资源管理器",
        "yinsi"    => "隐私保护",
        "system"   => "系统设置",
        "edge"     => "Edge 浏览器",
        "update"   => "Windows 更新",
        "safe"     => "安全设置",
        _          => c,
    };

    public static string CategoryHint(string c) => c switch
    {
        "xingneng" => "提升响应速度与流畅度；含 HPET、处理器性能、预读等。个别项（幽灵熔断补丁、系统缓存）会影响稳定性或安全，已默认不勾。",
        "explorer" => "任务栏、右键菜单、资源管理器行为与桌面图标。纯界面偏好，随时可还原。",
        "yinsi"    => "关闭遥测、活动收集、广告 ID、诊断数据上报。",
        "system"   => "休眠、蓝屏行为、磁盘检查、还原点、字体等系统级设置。",
        "edge"     => "Edge 后台常驻、启动增强、必应广告、诊断数据等。",
        "update"   => "Windows 更新策略：驱动更新、大版本更新、恶意软件删除工具。",
        "safe"     => "⚠ UAC / SmartScreen / 防火墙 / 内存完整性 / 虚拟化安全。降低系统防护，请确认你清楚后果再勾选。",
        _          => "",
    };

    // ---------------------------------------------------------------
    public static List<OptItem> Load()
    {
        lock (_lock)
        {
            if (_items != null) return _items;

            var list = new List<OptItem>();
            var asm = typeof(WindowsOptimizer).Assembly;
            var resName = asm.GetManifestResourceNames()
                            .FirstOrDefault(n => n.EndsWith("Optimizations.xml", StringComparison.OrdinalIgnoreCase));
            if (resName == null)
                throw new InvalidOperationException("内置优化配置缺失（Optimizations.xml 未嵌入）");
            using var s = asm.GetManifestResourceStream(resName);
            if (s == null)
                throw new InvalidOperationException($"无法读取内置配置：{resName}");

            var doc = XDocument.Load(s);
            foreach (var cat in doc.Root.Elements("Category"))
            {
                string catId = (string)cat.Attribute("id") ?? "";
                foreach (var it in cat.Elements("Item"))
                {
                    var item = new OptItem
                    {
                        Category = catId,
                        Name = (string)it.Attribute("name") ?? "",
                        CheckKey = (string)it.Attribute("key") ?? "",
                        CheckValue = (string)it.Attribute("value") ?? "",
                        OptimizedValue = (string)it.Attribute("target") ?? "",
                    };
                    foreach (var ch in it.Elements("Apply"))
                        foreach (var op in ch.Elements())
                            item.Apply.Add(ReadOp(op));
                    foreach (var ch in it.Elements("Revert"))
                        foreach (var op in ch.Elements())
                            item.Restore.Add(ReadOp(op));
                    list.Add(item);
                }
            }
            _items = list;
            return _items;
        }
    }

    private static OptOp ReadOp(XElement e)
    {
        var op = new OptOp
        {
            Kind = e.Name.LocalName switch
            {
                "RegWrite" => OpKind.RegWrite,
                "RegDelete" => OpKind.RegDelete,
                "SetServiceStart" => OpKind.SetServiceStart,
                "ExplorerNotify" => OpKind.ExplorerNotify,
                _ => OpKind.RegWrite,
            },
            Key = (string)e.Attribute("key") ?? "",
            Value = (string)e.Attribute("value") ?? "",
            Type = (string)e.Attribute("type") ?? "",
            Data = (string)e.Attribute("data") ?? "",
            Wow64 = ParseBool((string)e.Attribute("wow64")),
            SkipError = ParseBool((string)e.Attribute("skipError")),
            Name = (string)e.Attribute("name") ?? "",
            SvcType = (string)e.Attribute("start") ?? "",
            Cmd = (string)e.Attribute("cmd") ?? "",
            NType = (string)e.Attribute("notify") ?? "",
        };
        return op;
    }

    private static bool ParseBool(string s)
        => !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

    public static List<OptItem> ByCategory(string cat)
        => Load().Where(i => i.Category == cat).ToList();

    public static int TotalCount => Load().Count;

    // ---------------------------------------------------------------
    // 状态检测
    // ---------------------------------------------------------------
    public static OptStatus Check(OptItem item)
    {
        var st = new OptStatus { Item = item };

        if (item.IsKeyExistenceCheck)
        {
            st.Unknown = true;
            bool exists = RegKeyExists(item.CheckKey, false);
            st.Current = exists ? "键存在" : "键不存在";
            // 该条目语义是「删除键」，键不存在即已完成
            st.Applied = !exists;
            return st;
        }

        object cur = RegRead(item.CheckKey, item.CheckValue);
        if (cur == null)
        {
            st.Current = "(未设置)";
            st.Applied = false;
            return st;
        }

        st.Current = ToText(cur);
        st.Applied = ValuesEqual(st.Current, item.OptimizedValue);
        return st;
    }

    /// <summary>
    /// 把注册表原始值转成可比较的文本。
    /// DWORD 一律按无符号十进制输出：注册表里 0xFFFFFFFF 用 int 读出来是 -1，
    /// 若不做转换就会和配置里的 4294967295 判为不等，导致「已优化」被误判成「未优化」。
    /// </summary>
    public static string ToText(object v) => v switch
    {
        null => "",
        int i => unchecked((uint)i).ToString(),
        long l => l.ToString(),
        uint u => u.ToString(),
        byte[] b => BitConverter.ToString(b).Replace("-", ""),
        string[] arr => string.Join(";", arr),
        _ => v.ToString() ?? "",
    };

    /// <summary>比较两个值文本是否等价（容忍 0x 前缀、大小写、二进制/字符串混写）</summary>
    public static bool ValuesEqual(string a, string b)
    {
        if (a == null) a = "";
        if (b == null) b = "";
        a = a.Trim(); b = b.Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;

        // 十六进制 / 十进制互认
        if (TryNum(a, out long na) && TryNum(b, out long nb)) return na == nb;

        // 二进制连续十六进制（去掉分隔符后比较）
        string ca = new string(a.Where(char.IsLetterOrDigit).ToArray());
        string cb = new string(b.Where(char.IsLetterOrDigit).ToArray());
        return ca.Equals(cb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNum(string s, out long v)
    {
        v = 0;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                { v = Convert.ToInt64(s.Substring(2), 16); return true; }
            if (s.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                { v = Convert.ToInt64(s.Substring(6).Trim(), 16); return true; }
            if (s.All(char.IsDigit) && s.Length <= 18) { v = long.Parse(s); return true; }
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------------
    // 执行
    // ---------------------------------------------------------------
    /// <summary>应用一条优化项，返回是否成功</summary>
    public static bool Apply(OptItem item, Action<string> log = null)
    {
        bool ok = true;
        foreach (var op in item.Apply)
            ok &= ExecOp(op, item, log, applying: true);
        return ok;
    }

    /// <summary>按 XML 声明的 Restore 段还原</summary>
    public static bool Restore(OptItem item, Action<string> log = null)
    {
        bool ok = true;
        foreach (var op in item.Restore)
            ok &= ExecOp(op, item, log, applying: false);
        return ok;
    }

    private static bool ExecOp(OptOp op, OptItem item, Action<string> log, bool applying)
    {
        try
        {
            switch (op.Kind)
            {
                case OpKind.RegWrite:
                    RegWrite(op.Key, op.Value, op.Type, op.Data, op.Wow64);
                    log?.Invoke($"      写入 {Short(op.Key)}\\{op.Value} = {Trunc(op.Data)} [{op.Type}]");
                    return true;

                case OpKind.RegDelete:
                    if (string.IsNullOrEmpty(op.Value))
                    {
                        RegDeleteKey(op.Key, op.Wow64);
                        log?.Invoke($"      删除键 {Short(op.Key)}");
                    }
                    else
                    {
                        RegDeleteValue(op.Key, op.Value, op.Wow64);
                        log?.Invoke($"      删除值 {Short(op.Key)}\\{op.Value}");
                    }
                    return true;

                case OpKind.SetServiceStart:
                    SetServiceStart(op.Name, op.SvcType, log);
                    return true;

                case OpKind.ExplorerNotify:
                    if (!string.IsNullOrEmpty(op.Cmd))
                    {
                        var r = Cmd.Run("cmd.exe", "/c " + op.Cmd, 60000);
                        log?.Invoke($"      执行 {op.Cmd} → exit={r.ExitCode}");
                        if (!r.Ok && !op.SkipError) return false;
                    }
                    if (op.NType == "AssocChanged") NotifyAssocChanged();
                    return true;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (op.SkipError)
            {
                log?.Invoke($"      跳过（可忽略）：{ex.Message}");
                return true;
            }
            log?.Invoke($"      ✗ 失败：{ex.Message}");
            return false;
        }
    }

    private static string Short(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var i = key.LastIndexOf('\\');
        return i > 0 ? "…\\" + key.Substring(i + 1) : key;
    }

    private static string Trunc(string s)
        => string.IsNullOrEmpty(s) ? "(空)" : (s.Length <= 40 ? s : s.Substring(0, 40) + "…");

    // ---------------- 注册表底层 ----------------
    private static RegistryKey Root(string fullKey, bool wow64, bool writable, out string sub)
    {
        sub = "";
        if (string.IsNullOrWhiteSpace(fullKey)) return null;
        int i = fullKey.IndexOf('\\');
        string hive = i < 0 ? fullKey : fullKey.Substring(0, i);
        sub = i < 0 ? "" : fullKey.Substring(i + 1);

        var hk = hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => null,
        };
        if (hk == null) return null;

        var view = wow64 ? RegistryView.Registry32 : RegistryView.Registry64;
        var baseKey = RegistryKey.OpenBaseKey(
            hk == Registry.CurrentUser ? RegistryHive.CurrentUser :
            hk == Registry.LocalMachine ? RegistryHive.LocalMachine :
            hk == Registry.ClassesRoot ? RegistryHive.ClassesRoot :
            hk == Registry.Users ? RegistryHive.Users : RegistryHive.CurrentConfig, view);

        return writable ? baseKey.CreateSubKey(sub, true) : baseKey.OpenSubKey(sub, false);
    }

    public static object RegRead(string key, string value)
    {
        try
        {
            using var k = Root(key, false, false, out _);
            if (k == null) return null;
            return k.GetValue(value, null);
        }
        catch { return null; }
    }

    public static string RegReadText(string key, string value)
    {
        var v = RegRead(key, value);
        return v == null ? "(不存在)" : ToText(v);
    }

    public static bool RegKeyExists(string key, bool wow64)
    {
        try
        {
            using var k = Root(key, wow64, false, out _);
            return k != null;
        }
        catch { return false; }
    }

    public static void RegWrite(string key, string value, string type, string data, bool wow64)
    {
        using var k = Root(key, wow64, true, out _)
            ?? throw new InvalidOperationException("无法打开注册表键：" + key);
        var t = (type ?? "").ToUpperInvariant();
        if (t.Contains("DWORD"))
        {
            long v;
            var d = (data ?? "").Trim();
            try
            {
                v = d.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(d.Substring(2), 16)
                    : (d.Length > 0 && d.All(Uri.IsHexDigit) && d.Length <= 8 && d.Any(char.IsLetter)
                        ? Convert.ToInt64(d, 16) : long.Parse(d.Length == 0 ? "0" : d));
            }
            catch { v = 0; }
            k.SetValue(value, unchecked((int)v), RegistryValueKind.DWord);
        }
        else if (t.Contains("BINARY"))
        {
            var hex = new string((data ?? "").Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1) hex = "0" + hex;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            k.SetValue(value, bytes, RegistryValueKind.Binary);
        }
        else if (t.Contains("EXPAND"))
        {
            k.SetValue(value, data ?? "", RegistryValueKind.ExpandString);
        }
        else if (t.Contains("MULTI"))
        {
            k.SetValue(value, (data ?? "").Split(';'), RegistryValueKind.MultiString);
        }
        else
        {
            k.SetValue(value, data ?? "", RegistryValueKind.String);
        }
    }

    public static void RegDeleteValue(string key, string value, bool wow64)
    {
        using var k = Root(key, wow64, true, out _);
        if (k == null) return;
        if (k.GetValue(value) != null) k.DeleteValue(value, false);
    }

    public static void RegDeleteKey(string key, bool wow64)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        int i = key.IndexOf('\\');
        if (i < 0) return;
        string hive = key.Substring(0, i);
        string sub = key.Substring(i + 1);

        var baseKey = hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            _ => null,
        };
        if (baseKey == null) return;

        // 该键可能是空键（只有一个默认值），直接删键
        try { baseKey.DeleteSubKeyTree(sub, false); }
        catch
        {
            // 有子键且不可递归时，退化为清空默认值
            try
            {
                using var k = baseKey.OpenSubKey(sub, true);
                k?.DeleteValue("", false);
            }
            catch { }
        }
    }

    public static void SetServiceStart(string name, string type, Action<string> log = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}";
        RegWrite(key, "Start", "REG_DWORD", type, false);
        string desc = type switch
        {
            "2" => "自动", "3" => "手动", "4" => "禁用", "5" => "延迟自动", _ => type
        };
        log?.Invoke($"      服务 {name} 启动类型 → {desc}({type})");

        // 设为禁用时，正在运行的服务需要停掉才真正生效
        if (type == "4")
        {
            var r = Cmd.Run("sc.exe", $"stop {name}", 30000);
            if (r.Ok) log?.Invoke($"      已停止服务 {name}");
            else log?.Invoke($"      服务 {name} 未运行或停止请求被忽略");
        }
    }

    private static void NotifyAssocChanged()
    {
        try { NativeMethods.SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    /// <summary>把 setting 变更广播给所有窗口（主题、桌面等相关优化生效需要）</summary>
    public static void BroadcastSettingChange()
    {
        try
        {
            NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, 0x001A /*WM_SETTINGCHANGE*/,
                IntPtr.Zero, "ImmersiveColorSet", 2 /*SMTO_ABORTIFHUNG*/, 1000, out _);
        }
        catch { }
    }
    // ---------------------------------------------------------------
    // 快照
    // ---------------------------------------------------------------
    /// <summary>记录所选优化项在优化前的注册表原值，便于人工核对 / 回滚</summary>
    public static string SnapshotValues(IEnumerable<OptItem> items, string reason)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("; ---------- Windows 优化：优化前原值 ----------");
        sb.AppendLine($"; 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  原因: {reason}");
        foreach (var it in items)
        {
            sb.AppendLine($"[{it.Category}] {it.Name}");
            if (it.IsKeyExistenceCheck)
            {
                sb.AppendLine($"    {it.CheckKey}");
                sb.AppendLine($"        → 键{(RegKeyExists(it.CheckKey, false) ? "存在" : "不存在")}");
                continue;
            }
            var cur = RegRead(it.CheckKey, it.CheckValue);
            sb.AppendLine($"    {it.CheckKey}");
            sb.AppendLine(cur == null
                ? $"        {it.CheckValue} = (不存在)"
                : $"        {it.CheckValue} = {ToText(cur)}");
        }
        return sb.ToString();
    }
}
