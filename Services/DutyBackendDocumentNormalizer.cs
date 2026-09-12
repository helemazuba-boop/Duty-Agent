using DutyAgent.Models;

namespace DutyAgent.Services;

/// <summary>
/// 后端设置文档（selected_plan_id / plan_presets / duty_rule）的单一归一化器，
/// 由 <see cref="DutySettingsRepository"/> 与 <see cref="DutyBackendSettingsSyncService"/> 共用。
/// 规则对齐 Assets_Duty/state_ops.py 的 _normalize_plan_presets 一族：
/// - 默认 preset 四件套含 offline（离线算法），且对存量配置始终补齐 offline 预设；
/// - mode_id 别名合并两侧映射（standard/default、agents/multi_agent、
///   incremental(_small)/small_incremental、offline/algorithm/local/deterministic）；
/// - 计划 id 统一做 slug 化（小写、非字母数字折叠为 '-'）并去重 —— 与 Python 侧
///   _normalize_plan_id 对齐，否则后端把存量 "incremental_small" 改写为
///   "incremental-small" 后，两侧归一化永不收敛，BackendMatches 会陷入补丁循环。
/// </summary>
public static class DutyBackendDocumentNormalizer
{
    public const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string DefaultModel = "moonshotai/kimi-k2-thinking";
    public const string DefaultModelProfile = "auto";
    public const string DefaultMultiAgentExecutionMode = "auto";

    public static DutyEditableBackendSettingsDocument Normalize(DutyEditableBackendSettingsDocument? backend)
    {
        backend ??= new DutyEditableBackendSettingsDocument();
        var presets = ClonePlanPresets(backend.PlanPresets);
        if (presets.Count == 0)
        {
            presets = CreateDefaultPlanPresets();
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedPresets = new List<DutyPlanPreset>(presets.Count);
        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            var modeId = NormalizePlanModeId(preset.ModeId);
            var model = (preset.Model ?? string.Empty).Trim();
            if (model.Length == 0)
            {
                model = DefaultModel;
            }

            // Python: id 兜底 = mode_id（前 3 个预设），其后 plan-{index}（1 起始）。
            var fallbackId = i < 3 ? modeId : $"plan-{i + 1}";
            var id = EnsureUniquePlanId(NormalizePlanId(preset.Id, fallbackId), usedIds);

            normalizedPresets.Add(new DutyPlanPreset
            {
                Id = id,
                Name = NormalizePlanName(preset.Name, modeId, i + 1, model),
                ModeId = modeId,
                ApiKey = (preset.ApiKey ?? string.Empty).Trim(),
                BaseUrl = NormalizeNonEmpty(preset.BaseUrl, DefaultBaseUrl),
                Model = model,
                ModelProfile = NormalizeModelProfile(preset.ModelProfile),
                ProviderHint = (preset.ProviderHint ?? string.Empty).Trim(),
                MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                    ? NormalizeMultiAgentExecutionMode(preset.MultiAgentExecutionMode)
                    : DefaultMultiAgentExecutionMode
            });
        }

        // 镜像 Python 侧逻辑：离线（免模型）预设始终补齐，包含 offline 模式出现前
        // 保存的旧配置 —— 否则 BackendMatches 永远无法与后端注入的 offline 预设收敛。
        if (!normalizedPresets.Any(x => string.Equals(x.ModeId, DutyBackendModeIds.Offline, StringComparison.Ordinal)))
        {
            var offlineDefault = CreateDefaultPlanPresets()
                .First(x => string.Equals(x.ModeId, DutyBackendModeIds.Offline, StringComparison.Ordinal));
            offlineDefault.Id = EnsureUniquePlanId(offlineDefault.Id, usedIds);
            normalizedPresets.Add(offlineDefault);
        }

        if (normalizedPresets.Count == 0)
        {
            normalizedPresets = CreateDefaultPlanPresets();
        }

        // selected_plan_id 与 preset id 走同一 slug 规则后再匹配：存量
        // "incremental_small" 才能命中后端改写后的 "incremental-small"，不丢选中项。
        var selectedPlanId = NormalizePlanId(backend.SelectedPlanId, fallback: string.Empty);
        if (!normalizedPresets.Any(x => string.Equals(x.Id, selectedPlanId, StringComparison.Ordinal)))
        {
            selectedPlanId = normalizedPresets[0].Id;
        }

        return new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = selectedPlanId,
            PlanPresets = normalizedPresets,
            DutyRule = backend.DutyRule ?? string.Empty
        };
    }

    public static List<DutyPlanPreset> ClonePlanPresets(IEnumerable<DutyPlanPreset>? presets)
    {
        return (presets ?? [])
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

    public static string NormalizePlanModeId(string? modeId)
    {
        return (modeId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            DutyBackendModeIds.Agents => DutyBackendModeIds.Agents,
            "multi_agent" => DutyBackendModeIds.Agents,
            DutyBackendModeIds.IncrementalSmall => DutyBackendModeIds.IncrementalSmall,
            "incremental" => DutyBackendModeIds.IncrementalSmall,
            "small_incremental" => DutyBackendModeIds.IncrementalSmall,
            DutyBackendModeIds.Offline => DutyBackendModeIds.Offline,
            "algorithm" => DutyBackendModeIds.Offline,
            "local" => DutyBackendModeIds.Offline,
            "deterministic" => DutyBackendModeIds.Offline,
            _ => DutyBackendModeIds.Standard
        };
    }

    public static string NormalizeModelProfile(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cloud" or "cloud_general" => "cloud",
            "campus_small" or "campus" or "school_small" => "campus_small",
            "edge" or "edge_tuned" or "edge_finetuned" => "edge",
            "custom" => "custom",
            _ => DefaultModelProfile
        };
    }

    public static string NormalizeMultiAgentExecutionMode(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "parallel" or "concurrent" => "parallel",
            "serial" or "sequential" => "serial",
            _ => DefaultMultiAgentExecutionMode
        };
    }

    /// <summary>镜像 Python _normalize_plan_id：小写、仅保留字母数字、其余折叠为单个 '-'。</summary>
    private static string NormalizePlanId(string? rawId, string fallback)
    {
        var lowered = (rawId ?? string.Empty).Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        var previousDash = false;
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousDash = false;
            }
            else if (builder.Length > 0 && !previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var normalized = builder.ToString().Trim('-');
        return normalized.Length > 0 ? normalized : fallback;
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

    /// <summary>镜像 Python _normalize_plan_name：模式名兜底（mode_id 归一后必属四类）。</summary>
    private static string NormalizePlanName(string? name, string modeId, int index, string model)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        return modeId switch
        {
            DutyBackendModeIds.Agents => "Agents",
            DutyBackendModeIds.IncrementalSmall => "\u589e\u91cf\u5c0f\u6a21\u578b",
            DutyBackendModeIds.Offline => "\u79bb\u7ebf\u7b97\u6cd5",
            _ => "\u6807\u51c6"
        };
    }

    private static string NormalizeNonEmpty(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > 0 ? trimmed : fallback;
    }

    private static List<DutyPlanPreset> CreateDefaultPlanPresets()
    {
        return
        [
            CreateDefaultPlanPreset(DutyBackendModeIds.Standard),
            CreateDefaultPlanPreset(DutyBackendModeIds.Agents),
            CreateDefaultPlanPreset(DutyBackendModeIds.IncrementalSmall),
            CreateDefaultPlanPreset(DutyBackendModeIds.Offline)
        ];
    }

    private static DutyPlanPreset CreateDefaultPlanPreset(string modeId)
    {
        return new DutyPlanPreset
        {
            Id = modeId switch
            {
                DutyBackendModeIds.IncrementalSmall => "incremental-small",
                _ => modeId
            },
            Name = modeId switch
            {
                DutyBackendModeIds.Agents => "Agents",
                DutyBackendModeIds.IncrementalSmall => "\u589e\u91cf\u5c0f\u6a21\u578b",
                DutyBackendModeIds.Offline => "\u79bb\u7ebf\u7b97\u6cd5",
                _ => "\u6807\u51c6"
            },
            ModeId = modeId,
            BaseUrl = DefaultBaseUrl,
            Model = DefaultModel,
            ModelProfile = DefaultModelProfile,
            MultiAgentExecutionMode = DefaultMultiAgentExecutionMode
        };
    }
}
