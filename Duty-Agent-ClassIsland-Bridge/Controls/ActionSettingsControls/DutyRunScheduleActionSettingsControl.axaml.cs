using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Abstractions.Controls;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services.Automations.Actions;

/// <summary>
/// 执行排班动作的设置控件
/// </summary>
public partial class DutyRunScheduleActionSettingsControl : ActionSettingsControlBase
{
    public DutyRunScheduleActionSettingsControl()
    {
        InitializeComponent();
    }
}
