using Microsoft.Extensions.DependencyInjection;

namespace DutyAgent.Services;

public sealed class DutyPluginLifecycle
{
    private readonly DutyScheduleOrchestrator _orchestrator;
    private readonly IPythonIpcService _pythonIpcService;
    private readonly DutyLocalPreviewHostedService? _previewHostedService;
    private readonly DutyPluginPaths _paths;
    private int _started;

    public DutyPluginLifecycle(
        DutyScheduleOrchestrator orchestrator,
        IPythonIpcService pythonIpcService,
        IServiceProvider serviceProvider,
        DutyPluginPaths paths)
    {
        _orchestrator = orchestrator;
        _pythonIpcService = pythonIpcService;
        _paths = paths;
        _previewHostedService = serviceProvider.GetService<DutyLocalPreviewHostedService>();
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

        if (_previewHostedService != null)
        {
            await _previewHostedService.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _orchestrator.StopRuntime();

        if (_previewHostedService != null)
        {
            await _previewHostedService.StopAsync(cancellationToken);
        }

        await _pythonIpcService.StopAsync();
        PythonProcessTracker.CleanupTrackedProcesses();
    }
}
