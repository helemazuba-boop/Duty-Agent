using System.Windows.Forms;

namespace DutyAgent.Client;

internal static class Program
{
    // 单实例互斥与唤醒事件：开机自启驻留托盘后，用户再次双击快捷方式
    // 只应唤醒既有窗口，而不是再拉起一套后端。
    // 名称经 ScopeName 加包前缀：商店版（MSIX）与 zip 版同机并存时不会把对方
    // 当成"已有实例"。
    private static readonly string InstanceMutexName =
        PackageIdentityHelper.ScopeName(@"Local\DutyAgent.Client.SingleInstance");
    private static readonly string WakeEventName =
        PackageIdentityHelper.ScopeName(@"Local\DutyAgent.Client.Wake");

    // 数据目录级 owner：zip 版与商店版共享 %LOCALAPPDATA%\DutyAgent\data
    // （config/roster/state/host-config 与桥接 meta）。包身份进程写 HKCU 与
    // Win32 LocalAppData 均不重定向，两个客户端同时运行会各自拉起后端写同一份
    // 状态，因此这里刻意使用**不带包前缀**的互斥名：跨形态也只有一个 owner，
    // 后启动的一方唤醒既有实例后退出。
    private static readonly string DataDirectoryOwnerMutexName =
        @"Local\DutyAgent.DataDirectory.Owner";
    private static readonly string DataDirectoryWakeEventName =
        @"Local\DutyAgent.DataDirectory.Wake";

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

        // 先创建/打开唤醒事件再抢 owner：抢失败的一方要能通知当前 owner 把窗口拉到前台。
        using var dataWakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DataDirectoryWakeEventName);
        using var dataOwnerMutex = new Mutex(initiallyOwned: true, DataDirectoryOwnerMutexName, out var ownsDataDirectory);
        if (!ownsDataDirectory)
        {
            dataWakeEvent.Set();
            ClientLog.Warn(
                "另一个形态的 Duty-Agent（zip 版或商店版）正在使用同一数据目录，" +
                "已发送唤醒信号，本次退出以避免双后端写坏 state/roster。");
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
        Application.Run(new MainForm(startSilent, wakeEvent, dataWakeEvent));
        ClientLog.Info("主窗口已关闭，客户端退出。");
    }
}
