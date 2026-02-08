using ClassIsland.Core.Attributes;

namespace DutyIsland.Models;

public class DutyComponentSettings
{
    [SettingsInfo("\u53CC\u884C\u663E\u793A")]
    public bool UseTwoLineDisplay { get; set; } = true;
}
