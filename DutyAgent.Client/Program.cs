using System.Windows.Forms;

namespace DutyAgent.Client;

internal static class Program
{
    // 单实例互斥与唤醒事件：开机自启驻留托盘后，用户再次双击快捷方式
    // 只应唤醒既有窗口，而不是再拉起一套后端。
    // 名称经 ScopeName 加包前缀：商店版（MSIX）与 zip 版同机并存时不会把对方
    // 当成"已有实例"，也就不会出现双后端抢端口。
    private static readonly string InstanceMutexName =
        PackageIdentityHelper.ScopeName(@"Local\DutyAgent.Client.SingleInstance");
    private static readonly string WakeEventName =
        PackageIdentityHelper.ScopeName(@"Local\DutyAgent.Client.Wake");

    [STAThread]
    private static void Main(string[] args)
    {
        // 第一行日志必须早于一切分支：包身份是否生效决定了互斥名、自启方式、
        // AppData 语义，而"商店版不启动/启动即退"这类问题只有这行能说明白。
        ClientLog.Info($"客户端启动: {PackageIdentityHelper.Describe()}, 参数=[{string.Join(" ", args)}]");

        var startSilent = args.Any(arg =>
            string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));

        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

        if (!isFirstInstance)
        {
            // 已有实例（可能正驻留托盘）：发出唤醒信号后直接退出。
            ClientLog.Info("检测到同名单实例，已发送唤醒信号，本次退出。");
            wakeEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        // 未处理异常一律落盘：商店版静默退出（无控制台、无 Dr.Watson 弹窗）时，
        // 这数行是唯一能告诉用户"为什么没起来"的东西。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ClientLog.Error("进程级未处理异常（即将终止）",
                e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ClientLog.Error("UI 线程未处理异常", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ClientLog.Error("后台任务未观察异常", e.Exception);
            e.SetObserved();
        };

        ClientLog.Info("进入消息循环，创建主窗口。");
        Application.Run(new MainForm(startSilent, wakeEvent));
        ClientLog.Info("主窗口已关闭，客户端退出。");
    }
}
