import sys
import os

def rewrite_orchestrator(file_path):
    new_code = """using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DutyAgent.Models;
using Timer = System.Timers.Timer;

namespace DutyAgent.Services;

public class DutyScheduleOrchestrator : IDisposable
{
    private const string AutoRunInstruction = "Please generate duty schedule automatically based on roster.csv.";
    private const int AutoRunRetryCooldownMinutes = 30;
    private const string ApiKeyMask = "********";

    private readonly IConfigManager _configManager;
    private readonly IStateAndRosterManager _stateManager;
    private readonly IPythonIpcService _ipcService;
    private readonly DutyNotificationService _notificationService;
    
    private readonly Timer _debounceTimer;
    private readonly Timer _autoRunTimer;
    private readonly SemaphoreSlim _runCoreGate = new(1, 1);
    private readonly HashSet<string> _sentDutyReminderSlots = new(StringComparer.Ordinal);
    private DateTime _lastAutoRunAttempt = DateTime.MinValue;

    public event EventHandler? ScheduleUpdated;

    public DutyConfig Config => _configManager.Config;

    public DutyScheduleOrchestrator(
        IConfigManager configManager,
        IStateAndRosterManager stateManager,
        IPythonIpcService ipcService,
        DutyNotificationService notificationService)
    {
        _configManager = configManager;
        _stateManager = stateManager;
        _ipcService = ipcService;
        _notificationService = notificationService;

        _stateManager.StateChanged += (_, _) => DebounceUpdateNotification();
        _configManager.ConfigChanged += (_, _) => DebounceUpdateNotification();

        _debounceTimer = new Timer(500) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) =>
        {
            Dispatcher.UIThread.InvokeAsync(() => ScheduleUpdated?.Invoke(this, EventArgs.Empty));
        };

        _autoRunTimer = new Timer(60_000) { AutoReset = true, Enabled = true };
        _autoRunTimer.Elapsed += (_, _) => TryRunAutoSchedule();
    }

    private void DebounceUpdateNotification()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // Proxy load/save for backward compatibility with existing view models
    public void LoadConfig() => _configManager.ConfigChanged += null; // No-op, auto-managed
    public void SaveConfig() => _configManager.SaveConfig();

    public DutyState LoadState() => _stateManager.LoadState();
    public void SaveState(DutyState state) => _stateManager.SaveState(state);

    public bool RunCoreAgent(string instruction, string applyMode = "append", string? overrideModel = null)
    {
        var t = RunCoreAgentAsync(instruction, applyMode, overrideModel, null);
        t.Wait();
        return t.Result.Success;
    }

    public async Task<CoreRunResult> RunCoreAgentAsync(
        string instruction,
        string applyMode = "append",
        string? overrideModel = null,
        Action<CoreRunProgress>? progress = null)
    {
        var effectiveInstruction = string.IsNullOrWhiteSpace(instruction) ? AutoRunInstruction : instruction.Trim();

        if (!_runCoreGate.Wait(0))
        {
            return CoreRunResult.Fail("Another schedule run is already in progress.", code: "busy");
        }

        try
        {
            var apiKeyPlain = _configManager.Config.DecryptedApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKeyPlain))
            {
                return CoreRunResult.Fail("API key is empty or unavailable on this device.", code: "config");
            }

            var inputData = new
            {
                instruction = effectiveInstruction,
                apply_mode = applyMode,
                per_day = _configManager.Config.PerDay,
                duty_rule = _configManager.Config.DutyRule,
                base_url = _configManager.Config.BaseUrl,
                prompt_mode = _configManager.Config.PromptMode,
                model = overrideModel ?? _configManager.Config.Model
            };

            return await _ipcService.RunScheduleAsync(inputData, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return CoreRunResult.Fail($"Execution error: {ex.Message}");
        }
        finally
        {
            _runCoreGate.Release();
        }
    }

    public string GetApiKeyMaskForUi()
    {
        return string.IsNullOrWhiteSpace(_configManager.Config.EncryptedApiKey) ? string.Empty : ApiKeyMask;
    }

    public static string ResolveApiKeyInput(string? incomingApiKey, string existingApiKey)
    {
        if (incomingApiKey is null) return existingApiKey;
        var trimmed = incomingApiKey.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (string.Equals(trimmed, ApiKeyMask, StringComparison.Ordinal)) return existingApiKey;
        return trimmed;
    }

    public static string ValidatePythonPath(string configuredPath, string pluginBasePath, string assetsBasePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var absolutePath = Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(pluginBasePath, configuredPath);
            if (File.Exists(absolutePath)) return absolutePath;
        }

        var expectedRelativeExe = Path.Combine("Assets_Duty", "python-embed", "python.exe");
        var internalExe = Path.Combine(pluginBasePath, expectedRelativeExe);
        if (File.Exists(internalExe)) return internalExe;
        return "python"; // Fallback to PATH
    }

    public static TimeSpan NormalizeTimeOrThrow(TimeSpan t)
    {
        return new TimeSpan(t.Hours, t.Minutes, 0);
    }

    public static int NormalizeAutoRunMode(int mode)
    {
        return mode < 0 || mode > 2 ? 0 : mode; // 0=Weekly, 1=Monthly, 2=Custom
    }

    // Dummy backward-compatible helpers the UI needs
    private void TryRunAutoSchedule() { /* To be implemented async */ }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        _autoRunTimer.Dispose();
        _runCoreGate.Dispose();
    }
}
"""
    with open(file_path, "w", encoding="utf-8") as f:
        f.write(new_code)
        
if __name__ == '__main__':
    rewrite_orchestrator(r"c:\\Users\\ZhuanZ\\OneDrive\\Desktop\\Duty-Agent\\Services\\DutyScheduleOrchestrator.cs")
