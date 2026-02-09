using ClassIsland.Core.Attributes;

namespace DutyIsland.Models;

public class DutyComponentSettings
{
    [SettingsInfo("\u6BCF\u4E2A\u533A\u57DF\u4E00\u884C\u663E\u793A\uFF08\u5173\u95ED\u4E3A\u5355\u884C\uFF09")]
    public bool UsePerAreaMultiLine { get; set; } = false;
}
