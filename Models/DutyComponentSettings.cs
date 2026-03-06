using ClassIsland.Core.Attributes;

namespace DutyAgent.Models;

public class DutyComponentSettings
{
    [SettingsInfo("\u6BCF\u4E2A\u533A\u57DF\u4E00\u884C\u663E\u793A\uFF08\u5173\u95ED\u4E3A\u5355\u884C\uFF09")]
    public bool UsePerAreaMultiLine { get; set; } = false;

    [SettingsInfo("\u503C\u65E5\u540D\u5355\u53CC\u884C\u663E\u793A\uFF08\u622A\u53D6\u4E2D\u95F4\u5206\u884C\uFF09")]
    public bool UseDualRowDisplay { get; set; } = false;
}
