using System;
using System.Collections.Generic;
using System.Linq;
using DutyAgent.Models;

namespace DutyAgent.Services;

internal static class DutyBackendPlanPatchHelper
{
    private const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string DefaultModel = "moonshotai/kimi-k2-thinking";

    public static DutyBackendConfigPatch BuildSelectedPlanPatch(
        DutyBackendConfig backendConfig,
        string? apiKey,
        string? baseUrl,
        string? model,
        string? modelProfile,
        string? providerHint,
        string? orchestrationMode,
        string? multiAgentExecutionMode,
        string? dutyRule)
    {
        var planPresets = ClonePlanPresets(backendConfig.PlanPresets);
        if (planPresets.Count == 0)
        {
            planPresets = CreateDefaultPlanPresets();
        }

        var selectedPlanId = ResolveSelectedPlanId(backendConfig.SelectedPlanId, planPresets);
        var selectedPlan = planPresets.First(plan => string.Equals(plan.Id, selectedPlanId, StringComparison.Ordinal));
        selectedPlan.ModeId = ResolvePlanModeId(selectedPlan.ModeId, orchestrationMode);
        selectedPlan.ApiKey = DutyScheduleOrchestrator.ResolveApiKeyInput(apiKey, selectedPlan.ApiKey);
        selectedPlan.BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? selectedPlan.BaseUrl : baseUrl.Trim();
        selectedPlan.Model = string.IsNullOrWhiteSpace(model) ? selectedPlan.Model : model.Trim();
        if (modelProfile != null)
        {
            selectedPlan.ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(modelProfile);
        }

        if (providerHint != null)
        {
            selectedPlan.ProviderHint = providerHint.Trim();
        }

        selectedPlan.MultiAgentExecutionMode = string.Equals(selectedPlan.ModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
            ? (multiAgentExecutionMode is null
                ? DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(selectedPlan.MultiAgentExecutionMode)
                : DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(multiAgentExecutionMode))
            : "auto";

        return new DutyBackendConfigPatch
        {
            SelectedPlanId = selectedPlanId,
            PlanPresets = planPresets,
            DutyRule = dutyRule ?? backendConfig.DutyRule
        };
    }

    public static List<DutyPlanPreset> ClonePlanPresets(IEnumerable<DutyPlanPreset>? planPresets)
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

    private static List<DutyPlanPreset> CreateDefaultPlanPresets()
    {
        return
        [
            CreateDefaultPlanPreset(DutyBackendModeIds.Standard, "\u6807\u51c6"),
            CreateDefaultPlanPreset(DutyBackendModeIds.Campus6Agent, "6Agent"),
            CreateDefaultPlanPreset(DutyBackendModeIds.IncrementalSmall, "\u589e\u91cf\u5c0f\u6a21\u578b")
        ];
    }

    private static DutyPlanPreset CreateDefaultPlanPreset(string modeId, string name)
    {
        return new DutyPlanPreset
        {
            Id = modeId switch
            {
                DutyBackendModeIds.Campus6Agent => "campus-6agent",
                DutyBackendModeIds.IncrementalSmall => "incremental-small",
                _ => "standard"
            },
            Name = name,
            ModeId = modeId,
            BaseUrl = DefaultBaseUrl,
            Model = DefaultModel,
            ModelProfile = "auto",
            MultiAgentExecutionMode = "auto"
        };
    }

    private static string ResolveSelectedPlanId(string? selectedPlanId, IReadOnlyList<DutyPlanPreset> planPresets)
    {
        var normalizedSelectedPlanId = (selectedPlanId ?? string.Empty).Trim();
        if (normalizedSelectedPlanId.Length > 0 &&
            planPresets.Any(plan => string.Equals(plan.Id, normalizedSelectedPlanId, StringComparison.Ordinal)))
        {
            return normalizedSelectedPlanId;
        }

        return planPresets.Count > 0 ? planPresets[0].Id : DutyBackendModeIds.Standard;
    }

    private static string ResolvePlanModeId(string? currentModeId, string? orchestrationMode)
    {
        if (string.IsNullOrWhiteSpace(orchestrationMode))
        {
            return NormalizePlanModeId(currentModeId);
        }

        return DutyScheduleOrchestrator.NormalizeOrchestrationMode(orchestrationMode) switch
        {
            "multi_agent" => DutyBackendModeIds.Campus6Agent,
            "single_pass" when string.Equals(NormalizePlanModeId(currentModeId), DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                => DutyBackendModeIds.Standard,
            _ => NormalizePlanModeId(currentModeId)
        };
    }

    private static string NormalizePlanModeId(string? modeId)
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
}
