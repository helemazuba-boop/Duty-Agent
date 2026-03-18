namespace DutyAgent.Services;

public sealed class DutyPluginLifecycle
{
    private readonly DutyScheduleOrchestrator _orchestrator;
    private readonly IPythonIpcService _pythonIpcService;
    private readonly DutyPluginPaths _paths;
    private int _started;

    public DutyPluginLifecycle(
        DutyScheduleOrchestrator orchestrator,
        IPythonIpcService pythonIpcService,
        DutyPluginPaths paths)
    {
        _orchestrator = orchestrator;
        _pythonIpcService = pythonIpcService;
        _paths = paths;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        PythonProcessTracker.CleanupPersistedProcess(_paths.LegacyProcessSnapshotPath);
        PythonProcessTracker.CleanupPersistedProcess(_paths.ProcessSnapshotPath);

        _orchestrator.StartRuntime();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _orchestrator.StopRuntime();

        await _pythonIpcService.StopAsync();
        PythonProcessTracker.CleanupTrackedProcesses();
    }
}
