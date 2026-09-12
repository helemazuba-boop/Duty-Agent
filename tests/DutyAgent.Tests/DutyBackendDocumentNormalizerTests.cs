using DutyAgent.Models;
using DutyAgent.Services;
using Xunit;

namespace DutyAgent.Tests;

/// <summary>
/// DutyBackendDocumentNormalizer 归一化回归（对齐 Assets_Duty/state_ops.py
/// _normalize_plan_presets 一族）：默认预设四件套含 offline、存量配置补齐
/// offline、mode_id 别名合并、计划 id slug 化去重、selected_plan_id 容错匹配。
/// </summary>
public class DutyBackendDocumentNormalizerTests
{
    [Fact]
    public void Normalize_null_document_yields_default_four_presets_with_offline()
    {
        var normalized = DutyBackendDocumentNormalizer.Normalize(null);

        Assert.Equal(4, normalized.PlanPresets.Count);
        Assert.Equal(
            new[] { "standard", "agents", "incremental-small", "offline" },
            normalized.PlanPresets.Select(p => p.Id));
        Assert.Contains(normalized.PlanPresets, p => p.ModeId == DutyBackendModeIds.Offline);
        Assert.Equal("standard", normalized.SelectedPlanId);
        Assert.Equal(string.Empty, normalized.DutyRule);
    }

    [Fact]
    public void Legacy_document_without_offline_preset_gets_offline_appended()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "standard", ModeId = "standard" },
                new DutyPlanPreset { Id = "agents", ModeId = "agents" },
                new DutyPlanPreset { Id = "incremental-small", ModeId = "incremental_small" },
            ],
            SelectedPlanId = "agents",
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        var offline = Assert.Single(normalized.PlanPresets, p => p.ModeId == DutyBackendModeIds.Offline);
        Assert.Equal("offline", offline.Id);
        Assert.Equal("离线算法", offline.Name);
        Assert.Equal(string.Empty, offline.ApiKey);
        // 选中项保持不变（存量 id 能命中）。
        Assert.Equal("agents", normalized.SelectedPlanId);
    }

    [Fact]
    public void Mode_id_aliases_are_merged()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "a", ModeId = "multi_agent" },
                new DutyPlanPreset { Id = "b", ModeId = "algorithm" },
                new DutyPlanPreset { Id = "c", ModeId = "incremental" },
            ],
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        Assert.Equal(DutyBackendModeIds.Agents, normalized.PlanPresets[0].ModeId);
        Assert.Equal(DutyBackendModeIds.Offline, normalized.PlanPresets[1].ModeId);
        Assert.Equal(DutyBackendModeIds.IncrementalSmall, normalized.PlanPresets[2].ModeId);
    }

    [Fact]
    public void Plan_ids_are_slugged_and_deduplicated_like_python()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "My Plan!!", ModeId = "standard" },
                new DutyPlanPreset { Id = "my plan??", ModeId = "standard" },
                new DutyPlanPreset { Id = "!!!", ModeId = "agents" }, // slug 为空 → 兜底 mode_id
            ],
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        Assert.Equal("my-plan", normalized.PlanPresets[0].Id);
        // 重名 slug 去重（-2 后缀），与 Python _ensure_unique_plan_id 对齐。
        Assert.Equal("my-plan-2", normalized.PlanPresets[1].Id);
        Assert.Equal("agents", normalized.PlanPresets[2].Id);
    }

    [Fact]
    public void Unknown_selected_plan_id_falls_back_to_first_preset()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "standard", ModeId = "standard" },
            ],
            SelectedPlanId = "does-not-exist",
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        Assert.Equal("standard", normalized.SelectedPlanId);
    }

    [Fact]
    public void Selected_plan_id_is_slugged_before_matching()
    {
        // 存量 "incremental_small" 应命中 slug 化后的 "incremental-small"。
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "standard", ModeId = "standard" },
                new DutyPlanPreset { Id = "incremental-small", ModeId = "incremental_small" },
            ],
            SelectedPlanId = "Incremental_Small",
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        Assert.Equal("incremental-small", normalized.SelectedPlanId);
    }

    [Fact]
    public void Duty_rule_is_preserved()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            DutyRule = "每天两名同学",
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        Assert.Equal("每天两名同学", normalized.DutyRule);
    }

    [Fact]
    public void Agents_preset_normalizes_multi_agent_execution_mode_but_others_default()
    {
        var document = new DutyEditableBackendSettingsDocument
        {
            PlanPresets =
            [
                new DutyPlanPreset { Id = "agents", ModeId = "agents", MultiAgentExecutionMode = "concurrent" },
                new DutyPlanPreset { Id = "standard", ModeId = "standard", MultiAgentExecutionMode = "serial" },
            ],
        };

        var normalized = DutyBackendDocumentNormalizer.Normalize(document);

        // 别名 concurrent → parallel（仅 agents 模式参与归一）。
        Assert.Equal("parallel", normalized.PlanPresets[0].MultiAgentExecutionMode);
        // 非 agents 预设强制回默认 auto（对齐 Python 侧）。
        Assert.Equal(DutyBackendDocumentNormalizer.DefaultMultiAgentExecutionMode, normalized.PlanPresets[1].MultiAgentExecutionMode);
    }
}
