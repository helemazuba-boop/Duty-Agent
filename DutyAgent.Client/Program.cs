using System.Windows.Forms;

namespace DutyAgent.Client;

internal static class Program
{
    // 单实例互斥与唤醒事件：开机自启驻留托盘后，用户再次双击快捷方式
    // 只应唤醒既有窗口，而不是再拉起一套后端。
    private const string InstanceMutexName = @"Local\DutyAgent.Client.SingleInstance";
    private const string WakeEventName = @"Local\DutyAgent.Client.Wake";

    [STAThread]
    private static void Main(string[] args)
    {
        var startSilent = args.Any(arg =>
            string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));

        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

        if (!isFirstInstance)
        {
            // 已有实例（可能正驻留托盘）：发出唤醒信号后直接退出。
            wakeEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startSilent, wakeEvent));
    }
}
