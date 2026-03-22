using System;

namespace DutyAgent.Models;

public static class DutyServerPortModes
{
    public const string Random = "random";
    public const string Fixed = "fixed";

    public static string Normalize(string? mode)
    {
        return string.Equals((mode ?? string.Empty).Trim(), Fixed, StringComparison.OrdinalIgnoreCase)
            ? Fixed
            : Random;
    }
}
