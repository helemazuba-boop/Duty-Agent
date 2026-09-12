using ClassIsland.Core.Abstractions.Controls;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Controls;

/// <summary>
/// 值日人员组件的设置控件。继承 ComponentBase&lt;DutyComponentSettings&gt;，
/// 与组件实例共享同一个 Settings 对象（ClassIsland 组件系统负责接线），
/// 绑定改动即写回设置并触发组件实时重渲染。
/// </summary>
public partial class DutyComponentSettingsControl : ComponentBase<DutyComponentSettings>
{
    public DutyComponentSettingsControl()
    {
        InitializeComponent();
        // 绑定基线：Settings 由基类在初始化后加载；DataContext 指向自身，
        // 让 XAML 里的 {Binding Settings.X} 可解析（双保险，XAML 里也有
        // RelativeSource 写法，覆盖 Flyout 等特殊宿主）。
        DataContext = this;
    }
}
