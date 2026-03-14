using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsBackendModule
{
    private const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string DefaultModel = "moonshotai/kimi-k2-thinking";
    private static readonly string[] DefaultPlanOrder =
    [
        DutyBackendModeIds.Standard,
        DutyBackendModeIds.Campus6Agent,
        DutyBackendModeIds.IncrementalSmall
    ];

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
            ModelPresets = ClonePresets(config.ModelPresets),
            ModeProfiles = CloneModeProfiles(config.ModeProfiles),
            PerDay = config.PerDay,
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

        if (values.PerDay.HasValue && values.PerDay.Value != currentBackend.PerDay)
        {
            patch.PerDay = Math.Clamp(values.PerDay.Value, 1, 30);
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
            perDay = patch.PerDay?.ToString() ?? "<unchanged>",
            dutyRule = patch.DutyRule is null ? "<unchanged>" : TruncateForLog(patch.DutyRule, 160)
        };
    }

    public List<DutyPlanPreset> NormalizePlanPresets(IEnumerable<DutyPlanPreset>? planPresets, DutyBackendConfig? currentBackend = null)
    {
        var seed = ClonePlanPresets(planPresets);
        if (seed.Count == 0 && currentBackend != null)
        {
            seed = currentBackend.PlanPresets.Count > 0
                ? ClonePlanPresets(currentBackend.PlanPresets)
                : BuildLegacyPlanPresets(currentBackend);
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

    private List<DutyPlanPreset> CreateDefaultPlanPresets()
    {
        return
        [
            CreateDefaultPlanPreset(DutyBackendModeIds.Standard),
            CreateDefaultPlanPreset(DutyBackendModeIds.Campus6Agent),
            CreateDefaultPlanPreset(DutyBackendModeIds.IncrementalSmall)
        ];
    }

    private DutyPlanPreset CreateDefaultPlanPreset(string modeId)
    {
        return new DutyPlanPreset
        {
            Id = NormalizePlanId(modeId),
            Name = GetModeDisplayName(modeId),
            ModeId = NormalizePlanModeId(modeId),
            BaseUrl = DefaultBaseUrl,
            Model = DefaultModel,
            ModelProfile = "auto",
            MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                ? "auto"
                : "auto"
        };
    }

    private List<DutyPlanPreset> BuildLegacyPlanPresets(DutyBackendConfig? currentBackend)
    {
        if (currentBackend == null)
        {
            return CreateDefaultPlanPresets();
        }

        var normalizedPresets = NormalizeLegacyModelPresets(currentBackend.ModelPresets, currentBackend);
        var selectedModeId = InferLegacySelectedModeId(currentBackend);
        var normalizedProfiles = NormalizeLegacyModeProfiles(currentBackend.ModeProfiles, normalizedPresets, currentBackend, selectedModeId);

        var plans = new List<DutyPlanPreset>();
        foreach (var modeId in DefaultPlanOrder)
        {
            var profile = normalizedProfiles.FirstOrDefault(item => string.Equals(item.ModeId, modeId, StringComparison.Ordinal))
                          ?? CreateLegacyModeProfile(modeId, normalizedPresets[0].Id, modeId == DutyBackendModeIds.Campus6Agent ? "multi_agent" : "single_pass", "auto", modeId == DutyBackendModeIds.IncrementalSmall ? "incremental_thinking" : "auto");
            var preset = normalizedPresets.FirstOrDefault(item => string.Equals(item.Id, profile.PresetId, StringComparison.Ordinal))
                         ?? normalizedPresets[0];

            plans.Add(new DutyPlanPreset
            {
                Id = NormalizePlanId(modeId),
                Name = GetModeDisplayName(modeId),
                ModeId = modeId,
                ApiKey = preset.ApiKey,
                BaseUrl = NormalizeBaseUrl(preset.BaseUrl),
                Model = string.IsNullOrWhiteSpace(preset.Model) ? DefaultModel : preset.Model.Trim(),
                ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(preset.ModelProfile),
                ProviderHint = (preset.ProviderHint ?? string.Empty).Trim(),
                MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                    ? DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(profile.MultiAgentExecutionMode)
                    : "auto"
            });
        }

        return plans;
    }

    private List<DutyModelPreset> ClonePresets(IEnumerable<DutyModelPreset>? presets)
    {
        return (presets ?? [])
            .Select(preset => new DutyModelPreset
            {
                Id = preset.Id,
                Name = preset.Name,
                ApiKey = preset.ApiKey,
                BaseUrl = preset.BaseUrl,
                Model = preset.Model,
                ModelProfile = preset.ModelProfile,
                ProviderHint = preset.ProviderHint
            })
            .ToList();
    }

    private List<DutyModeProfile> CloneModeProfiles(IEnumerable<DutyModeProfile>? modeProfiles)
    {
        return (modeProfiles ?? [])
            .Select(profile => new DutyModeProfile
            {
                ModeId = profile.ModeId,
                PresetId = profile.PresetId,
                OrchestrationMode = profile.OrchestrationMode,
                MultiAgentExecutionMode = profile.MultiAgentExecutionMode,
                SinglePassStrategy = profile.SinglePassStrategy
            })
            .ToList();
    }

    private List<DutyModelPreset> NormalizeLegacyModelPresets(IEnumerable<DutyModelPreset>? presets, DutyBackendConfig? currentBackend = null)
    {
        var seed = ClonePresets(presets);
        if (seed.Count == 0 && currentBackend != null)
        {
            seed = currentBackend.ModelPresets.Count > 0
                ? ClonePresets(currentBackend.ModelPresets)
                : [CreateLegacyPreset(currentBackend)];
        }

        if (seed.Count == 0)
        {
            seed =
            [
                new DutyModelPreset
                {
                    Id = "default",
                    Name = "默认模型"
                }
            ];
        }

        var normalized = new List<DutyModelPreset>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;

        foreach (var preset in seed)
        {
            var baseId = NormalizePlanId(preset.Id);
            if (baseId.Length == 0)
            {
                baseId = $"preset-{index}";
            }

            var resolvedId = EnsureUniquePlanId(baseId, usedIds);
            var resolvedModel = (preset.Model ?? string.Empty).Trim();
            normalized.Add(new DutyModelPreset
            {
                Id = resolvedId,
                Name = NormalizeLegacyPresetName(preset.Name, resolvedModel, normalized.Count + 1),
                ApiKey = (preset.ApiKey ?? string.Empty).Trim(),
                BaseUrl = NormalizeBaseUrl(preset.BaseUrl),
                Model = resolvedModel.Length == 0 ? DefaultModel : resolvedModel,
                ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(preset.ModelProfile),
                ProviderHint = (preset.ProviderHint ?? string.Empty).Trim()
            });
            index++;
        }

        return normalized;
    }

    private List<DutyModeProfile> NormalizeLegacyModeProfiles(
        IEnumerable<DutyModeProfile>? modeProfiles,
        IReadOnlyList<DutyModelPreset> presets,
        DutyBackendConfig? currentBackend = null,
        string? selectedModeId = null)
    {
        var presetIds = presets.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var fallbackPresetId = presets.FirstOrDefault()?.Id ?? "default";
        var inputMap = CloneModeProfiles(modeProfiles)
            .GroupBy(profile => NormalizePlanModeId(profile.ModeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var effectiveSelectedModeId = NormalizePlanModeId(selectedModeId ?? currentBackend?.SelectedPlanId);
        return
        [
            CreateLegacyModeProfile(
                DutyBackendModeIds.Standard,
                ResolveLegacyPresetId(inputMap, DutyBackendModeIds.Standard, currentBackend, fallbackPresetId, presetIds, effectiveSelectedModeId),
                "single_pass",
                "auto",
                "auto"),
            CreateLegacyModeProfile(
                DutyBackendModeIds.Campus6Agent,
                ResolveLegacyPresetId(inputMap, DutyBackendModeIds.Campus6Agent, currentBackend, fallbackPresetId, presetIds, effectiveSelectedModeId),
                "multi_agent",
                ResolveLegacyCampusExecutionMode(inputMap, currentBackend, effectiveSelectedModeId),
                "auto"),
            CreateLegacyModeProfile(
                DutyBackendModeIds.IncrementalSmall,
                ResolveLegacyPresetId(inputMap, DutyBackendModeIds.IncrementalSmall, currentBackend, fallbackPresetId, presetIds, effectiveSelectedModeId),
                "single_pass",
                "auto",
                "incremental_thinking")
        ];
    }

    private static DutyModelPreset CreateLegacyPreset(DutyBackendConfig currentBackend)
    {
        return new DutyModelPreset
        {
            Id = "default",
            Name = "默认模型",
            ApiKey = currentBackend.ApiKey,
            BaseUrl = currentBackend.BaseUrl,
            Model = currentBackend.Model,
            ModelProfile = currentBackend.ModelProfile,
            ProviderHint = currentBackend.ProviderHint
        };
    }

    private static DutyModeProfile CreateLegacyModeProfile(
        string modeId,
        string presetId,
        string orchestrationMode,
        string multiAgentExecutionMode,
        string singlePassStrategy)
    {
        return new DutyModeProfile
        {
            ModeId = modeId,
            PresetId = presetId,
            OrchestrationMode = orchestrationMode,
            MultiAgentExecutionMode = multiAgentExecutionMode,
            SinglePassStrategy = singlePassStrategy
        };
    }

    private string InferLegacySelectedModeId(DutyBackendConfig currentBackend)
    {
        var explicitModeId = NormalizePlanModeId(currentBackend.SelectedPlanId);
        if (currentBackend.ModelPresets.Count > 0 || currentBackend.ModeProfiles.Count > 0)
        {
            return explicitModeId;
        }

        if (string.Equals(DutyScheduleOrchestrator.NormalizeOrchestrationMode(currentBackend.OrchestrationMode), "multi_agent", StringComparison.Ordinal))
        {
            return DutyBackendModeIds.Campus6Agent;
        }

        if (string.Equals((currentBackend.SinglePassStrategy ?? string.Empty).Trim(), "incremental_thinking", StringComparison.OrdinalIgnoreCase))
        {
            return DutyBackendModeIds.IncrementalSmall;
        }

        if (string.Equals(DutyScheduleOrchestrator.NormalizeModelProfile(currentBackend.ModelProfile), "campus_small", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DutyScheduleOrchestrator.NormalizeOrchestrationMode(currentBackend.OrchestrationMode), "auto", StringComparison.Ordinal))
        {
            return DutyBackendModeIds.Campus6Agent;
        }

        return explicitModeId;
    }

    private static string ResolveLegacyPresetId(
        IReadOnlyDictionary<string, DutyModeProfile> inputMap,
        string modeId,
        DutyBackendConfig? currentBackend,
        string fallbackPresetId,
        IReadOnlySet<string> presetIds,
        string selectedModeId)
    {
        var candidate = inputMap.TryGetValue(modeId, out var inputProfile)
            ? inputProfile.PresetId
            : string.Equals(selectedModeId, modeId, StringComparison.Ordinal) && currentBackend != null
                ? fallbackPresetId
                : fallbackPresetId;
        var normalized = (candidate ?? string.Empty).Trim();
        return presetIds.Contains(normalized) ? normalized : fallbackPresetId;
    }

    private static string ResolveLegacyCampusExecutionMode(
        IReadOnlyDictionary<string, DutyModeProfile> inputMap,
        DutyBackendConfig? currentBackend,
        string selectedModeId)
    {
        var candidate = inputMap.TryGetValue(DutyBackendModeIds.Campus6Agent, out var inputProfile)
            ? inputProfile.MultiAgentExecutionMode
            : string.Equals(selectedModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                ? currentBackend?.MultiAgentExecutionMode
                : "auto";
        return DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(candidate);
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

        return index <= DefaultPlanOrder.Length ? GetModeDisplayName(modeId) : (model.Length > 0 ? model : $"方案预设 {index}");
    }

    private static string NormalizeLegacyPresetName(string? name, string model, int index)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        return model.Length > 0 ? model : $"模型预设 {index}";
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
            DutyBackendModeIds.IncrementalSmall => "增量小模型",
            _ => "标准"
        };
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
