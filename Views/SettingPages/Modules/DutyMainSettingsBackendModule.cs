using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsBackendModule
{
    private const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string DefaultModel = "moonshotai/kimi-k2-thinking";

    private readonly DutyScheduleOrchestrator _service;

    public DutyMainSettingsBackendModule(DutyScheduleOrchestrator service)
    {
        _service = service;
    }

    public Task<DutyBackendConfig> LoadAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return _service.LoadBackendConfigAsync(requestSource, traceId, cancellationToken);
    }

    public Task<DutyBackendConfig> SaveAsync(
        DutyBackendConfigPatch patch,
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return _service.SaveBackendConfigAsync(patch, requestSource, traceId, cancellationToken);
    }

    public DutyBackendConfig CloneConfig(DutyBackendConfig config)
    {
        return new DutyBackendConfig
        {
            Version = config.Version,
            ApiKey = config.ApiKey,
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            ModelProfile = config.ModelProfile,
            OrchestrationMode = config.OrchestrationMode,
            MultiAgentExecutionMode = config.MultiAgentExecutionMode,
            SinglePassStrategy = config.SinglePassStrategy,
            ProviderHint = config.ProviderHint,
            SelectedPlanId = NormalizeSelectedPlanId(config.SelectedPlanId, config.PlanPresets, config),
            PlanPresets = ClonePlanPresets(config.PlanPresets),
            DutyRule = config.DutyRule
        };
    }

    public List<DutyPlanPreset> ClonePlanPresets(IEnumerable<DutyPlanPreset>? planPresets)
    {
        return (planPresets ?? [])
            .Select(plan => new DutyPlanPreset
            {
                Id = plan.Id,
                Name = plan.Name,
                ModeId = plan.ModeId,
                ApiKey = plan.ApiKey,
                BaseUrl = plan.BaseUrl,
                Model = plan.Model,
                ModelProfile = plan.ModelProfile,
                ProviderHint = plan.ProviderHint,
                MultiAgentExecutionMode = plan.MultiAgentExecutionMode
            })
            .ToList();
    }

    public DutyPlanPreset? GetSelectedPlan(IEnumerable<DutyPlanPreset>? planPresets, string? selectedPlanId)
    {
        var plans = ClonePlanPresets(planPresets);
        if (plans.Count == 0)
        {
            return null;
        }

        var resolvedId = NormalizeSelectedPlanId(selectedPlanId, plans);
        return plans.FirstOrDefault(plan => string.Equals(plan.Id, resolvedId, StringComparison.Ordinal))
               ?? plans[0];
    }

    public string NormalizePlanModeId(string? modeId)
    {
        return (modeId ?? DutyBackendModeIds.Standard).Trim().ToLowerInvariant() switch
        {
            DutyBackendModeIds.Campus6Agent => DutyBackendModeIds.Campus6Agent,
            "campus6agent" => DutyBackendModeIds.Campus6Agent,
            "6agent" => DutyBackendModeIds.Campus6Agent,
            "multi_agent" => DutyBackendModeIds.Campus6Agent,
            DutyBackendModeIds.IncrementalSmall => DutyBackendModeIds.IncrementalSmall,
            "incremental" => DutyBackendModeIds.IncrementalSmall,
            "small_incremental" => DutyBackendModeIds.IncrementalSmall,
            _ => DutyBackendModeIds.Standard
        };
    }

    public string NormalizeSelectedPlanId(
        string? selectedPlanId,
        IEnumerable<DutyPlanPreset>? planPresets = null,
        DutyBackendConfig? currentBackend = null)
    {
        var plans = ClonePlanPresets(planPresets);
        if (plans.Count == 0 && currentBackend != null)
        {
            plans = NormalizePlanPresets(currentBackend.PlanPresets, currentBackend);
        }

        if (plans.Count == 0)
        {
            plans = CreateDefaultPlanPresets();
        }

        var rawSelectedPlanId = NormalizePlanId(selectedPlanId);
        if (rawSelectedPlanId.Length > 0)
        {
            var exactMatch = plans.FirstOrDefault(plan => string.Equals(plan.Id, rawSelectedPlanId, StringComparison.Ordinal));
            if (exactMatch != null)
            {
                return exactMatch.Id;
            }
        }

        var normalizedModeId = NormalizePlanModeId(selectedPlanId);
        var modeMatch = plans.FirstOrDefault(plan => string.Equals(plan.ModeId, normalizedModeId, StringComparison.Ordinal));
        return modeMatch?.Id ?? plans[0].Id;
    }

    public DutyBackendConfigPatch? TryBuildPatch(DutyBackendSettingsValues values, DutyBackendConfig? currentBackend)
    {
        if (currentBackend == null)
        {
            return null;
        }

        var normalizedPlanPresets = NormalizePlanPresets(values.PlanPresets, currentBackend);
        var normalizedSelectedPlanId = NormalizeSelectedPlanId(values.SelectedPlanId, normalizedPlanPresets, currentBackend);
        var normalizedDutyRule = values.DutyRule ?? string.Empty;

        var patch = new DutyBackendConfigPatch();
        var hasChanges = false;

        if (!string.Equals(
                normalizedSelectedPlanId,
                NormalizeSelectedPlanId(currentBackend.SelectedPlanId, currentBackend.PlanPresets, currentBackend),
                StringComparison.Ordinal))
        {
            patch.SelectedPlanId = normalizedSelectedPlanId;
            hasChanges = true;
        }

        if (!ArePlanPresetsEqual(normalizedPlanPresets, NormalizePlanPresets(currentBackend.PlanPresets, currentBackend)))
        {
            patch.PlanPresets = ClonePlanPresets(normalizedPlanPresets);
            hasChanges = true;
        }

        if (!string.Equals(normalizedDutyRule, currentBackend.DutyRule, StringComparison.Ordinal))
        {
            patch.DutyRule = normalizedDutyRule;
            hasChanges = true;
        }

        return hasChanges ? patch : null;
    }

    public object? SummarizePatch(DutyBackendConfigPatch? patch)
    {
        if (patch == null)
        {
            return null;
        }

        return new
        {
            selectedPlanId = patch.SelectedPlanId ?? "<unchanged>",
            planPresetCount = patch.PlanPresets?.Count.ToString() ?? "<unchanged>",
            dutyRule = patch.DutyRule is null ? "<unchanged>" : TruncateForLog(patch.DutyRule, 160)
        };
    }

    public List<DutyPlanPreset> NormalizePlanPresets(
        IEnumerable<DutyPlanPreset>? planPresets,
        DutyBackendConfig? currentBackend = null)
    {
        var seed = ClonePlanPresets(planPresets);
        if (seed.Count == 0 && currentBackend != null)
        {
            seed = ClonePlanPresets(currentBackend.PlanPresets);
        }

        if (seed.Count == 0)
        {
            seed = CreateDefaultPlanPresets();
        }

        var normalized = new List<DutyPlanPreset>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;

        foreach (var plan in seed)
        {
            var modeId = NormalizePlanModeId(plan.ModeId);
            var baseId = NormalizePlanId(plan.Id);
            if (baseId.Length == 0)
            {
                baseId = NormalizePlanId(modeId);
            }

            if (baseId.Length == 0)
            {
                baseId = $"plan-{index}";
            }

            var resolvedId = EnsureUniquePlanId(baseId, usedIds);
            var resolvedModel = (plan.Model ?? string.Empty).Trim();

            normalized.Add(new DutyPlanPreset
            {
                Id = resolvedId,
                Name = NormalizePlanName(plan.Name, modeId, resolvedModel, index),
                ModeId = modeId,
                ApiKey = (plan.ApiKey ?? string.Empty).Trim(),
                BaseUrl = NormalizeBaseUrl(plan.BaseUrl),
                Model = resolvedModel.Length == 0 ? DefaultModel : resolvedModel,
                ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(plan.ModelProfile),
                ProviderHint = (plan.ProviderHint ?? string.Empty).Trim(),
                MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                    ? DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(plan.MultiAgentExecutionMode)
                    : "auto"
            });
            index++;
        }

        return normalized;
    }

    private static List<DutyPlanPreset> CreateDefaultPlanPresets()
    {
        return
        [
            CreateDefaultPlanPreset(DutyBackendModeIds.Standard),
            CreateDefaultPlanPreset(DutyBackendModeIds.Campus6Agent),
            CreateDefaultPlanPreset(DutyBackendModeIds.IncrementalSmall)
        ];
    }

    private static DutyPlanPreset CreateDefaultPlanPreset(string modeId)
    {
        return new DutyPlanPreset
        {
            Id = NormalizePlanId(modeId),
            Name = GetModeDisplayName(modeId),
            ModeId = modeId,
            BaseUrl = DefaultBaseUrl,
            Model = DefaultModel,
            ModelProfile = "auto",
            MultiAgentExecutionMode = "auto"
        };
    }

    private static bool ArePlanPresetsEqual(IReadOnlyList<DutyPlanPreset> left, IReadOnlyList<DutyPlanPreset> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.ModeId, b.ModeId, StringComparison.Ordinal) ||
                !string.Equals(a.ApiKey, b.ApiKey, StringComparison.Ordinal) ||
                !string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.Ordinal) ||
                !string.Equals(a.Model, b.Model, StringComparison.Ordinal) ||
                !string.Equals(a.ModelProfile, b.ModelProfile, StringComparison.Ordinal) ||
                !string.Equals(a.ProviderHint, b.ProviderHint, StringComparison.Ordinal) ||
                !string.Equals(a.MultiAgentExecutionMode, b.MultiAgentExecutionMode, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePlanId(string? planId)
    {
        var buffer = new List<char>();
        foreach (var ch in (planId ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Add(ch);
            }
            else if (buffer.Count > 0 && buffer[^1] != '-')
            {
                buffer.Add('-');
            }
        }

        return new string(buffer.ToArray()).Trim('-');
    }

    private static string EnsureUniquePlanId(string baseId, ISet<string> usedIds)
    {
        var candidate = baseId;
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string NormalizePlanName(string? name, string modeId, string model, int index)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        return index <= 3 ? GetModeDisplayName(modeId) : (model.Length > 0 ? model : $"Plan Preset {index}");
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim();
        return normalized.Length == 0 ? DefaultBaseUrl : normalized;
    }

    private static string GetModeDisplayName(string modeId)
    {
        return modeId switch
        {
            DutyBackendModeIds.Campus6Agent => "6Agent",
            DutyBackendModeIds.IncrementalSmall => "\u589e\u91cf\u5c0f\u6a21\u578b",
            _ => "\u6807\u51c6"
        };
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
