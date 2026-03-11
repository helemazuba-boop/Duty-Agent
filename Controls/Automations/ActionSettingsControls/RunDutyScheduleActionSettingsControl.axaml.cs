using ClassIsland.Core.Abstractions.Controls;
using DutyAgent.Models.Automations.Actions;

namespace DutyAgent.Controls.Automations.ActionSettingsControls;

public partial class RunDutyScheduleActionSettingsControl : ActionSettingsControlBase<RunDutyScheduleActionSettings>
{
    public IReadOnlyList<string> ApplyModeOptions { get; } =
    [
        "replace_all",
        "replace_future",
        "replace_overlap",
        "append"
    ];

    public RunDutyScheduleActionSettingsControl()
    {
        InitializeComponent();
    }
}
