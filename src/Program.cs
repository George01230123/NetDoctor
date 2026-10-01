using System.Diagnostics;
using NetDoctor.Core;
using NetDoctor.UI;

namespace NetDoctor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Err("未处理异常：" + e.ExceptionObject);

        Application.ThreadException += (_, e) =>
        {
            Log.Err("界面异常：" + e.Exception.Message);
            MessageBox.Show(e.Exception.Message, "夕颜若雪网络工具",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        bool admin = false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            admin = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }

        Log.Info("================ 夕颜若雪网络工具 启动 ================");
        Log.Info($"版本 {Application.ProductVersion} · {(admin ? "管理员权限" : "普通权限(部分功能不可用)")} · {Environment.OSVersion}");
        if (!admin)
            Log.Warn("未以管理员身份运行，断网修复/网络优化可能失败。请右键以管理员身份运行。");

        try
        {
            var form = new MainForm(admin);
            if (args.Any(a => a.Equals("--autotest", StringComparison.OrdinalIgnoreCase)))
            {
                // 开发/验证用：启动后自动进入指定页面并跑完整检测
                string view = args.FirstOrDefault(a => a.StartsWith("--view="))?.Substring(7) ?? "diag";
                Log.Info($"检测到 --autotest，将在 2 秒后自动进入 {view} 页");
                form.Shown += async (_, _) =>
                {
                    await Task.Delay(2000);
                    form.ShowView(view);
                    if (view == "diag") await form.ViewDiag.RunFullAsync(false);
                    if (view == "tools")
                    {
                        await form.ViewTools.AutoScanAsync();
                        // --tab=N 指定系统工具页内的标签（0=垃圾清理 … 5=安全中心）
                        var tb = args.FirstOrDefault(a => a.StartsWith("--tab="));
                        if (tb != null && int.TryParse(tb.Substring(6), out int ti))
                        {
                            form.ViewTools.SelectTab(ti);
                            if (ti == 5) await form.ViewTools.AutoScanSecurityAsync();
                        }
                    }
                    if (view == "hardware")
                    {
                        int tab = 0;
                        var ta = args.FirstOrDefault(a => a.StartsWith("--tab="));
                        if (ta != null) int.TryParse(ta.Substring(6), out tab);
                        await form.ViewHardware.AutoDetectAsync(tab);
                    }
                    Log.Ok("== autotest 执行完毕 ==");
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log.Err("致命错误：" + ex);
            MessageBox.Show(ex.ToString(), "启动失败");
        }
    }
}
