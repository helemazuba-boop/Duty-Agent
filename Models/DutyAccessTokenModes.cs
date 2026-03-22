using System;

namespace DutyAgent.Models;

public static class DutyAccessTokenModes
{
    public const string Dynamic = "dynamic";
    public const string Static = "static";

    public static string Normalize(string? mode)
    {
        return string.Equals((mode ?? string.Empty).Trim(), Static, StringComparison.OrdinalIgnoreCase)
            ? Static
            : Dynamic;
    }
}
