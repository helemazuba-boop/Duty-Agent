using ClassIsland.Core.Attributes;

namespace DutyAgent.Models;

public class DutyComponentSettings
{
    [SettingsInfo("\u503C\u65E5\u540D\u5355\u53CC\u884C\u663E\u793A\uFF08\u622A\u53D6\u4E2D\u95F4\u5206\u884C\uFF09")]
    public bool UseDualRowDisplay { get; set; } = false;

    [SettingsInfo("\u6BCF\u4E2A\u533A\u57DF\u4E00\u884C\u663E\u793A\uFF08\u5173\u95ED\u4E3A\u5355\u884C\uFF09")]
    public bool UsePerAreaMultiLine { get; set; } = false;

    [SettingsInfo("\u503C\u65E5\u7EC4\u4EF6\u5B57\u53F7\u5927\u5C0F\uFF08\u9ED8\u8BA4 14\uFF09")]
    public int FontSize { get; set; } = 14;

    [SettingsInfo("\u503C\u65E5\u7EC4\u4EF6\u5B57\u4F53\u989C\u8272\uFF08\u9ED8\u8BA4\u9ED1\u8272\uFF09")]
    public string FontColor { get; set; } = "#000000";

    [SettingsInfo("\u503C\u65E5\u7EC4\u4EF6\u5237\u65B0\u95F4\u9694\u79D2\u6570\uFF08\u9ED8\u8BA4 60\u79D2\uFF09")]
    public int RefreshIntervalSeconds { get; set; } = 60;
}
