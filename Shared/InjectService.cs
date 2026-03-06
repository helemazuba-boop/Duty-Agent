using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;

namespace DutyAgent.Shared;

public static class InjectService
{
    public static bool TryGetAddSettingsPageGroupMethod([MaybeNullWhen(false)] out MethodInfo method)
    {
        Type settingsWindowRegistryExtensionsType = typeof(SettingsWindowRegistryExtensions);
        method = settingsWindowRegistryExtensionsType
            .GetMethods()
            .FirstOrDefault(m => (m.ToString()?.Contains("AddSettingsPageGroup") ?? false) && m.GetParameters().Length == 4);
        return method != null;
    }

    public static FieldInfo GetSettingsPageInfoNameField()
    {
        Type settingsPageInfoType = typeof(SettingsPageInfo);
        FieldInfo? field = settingsPageInfoType
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.ToString()?.Contains("Name") ?? false);
        return field!;
    }

    public static PropertyInfo GetSettingsPageInfoGroupIdProperty()
    {
        Type settingsPageInfoType = typeof(SettingsPageInfo);
        PropertyInfo? property = settingsPageInfoType
            .GetProperties()
            .FirstOrDefault(m => m.ToString()?.Contains("GroupId") ?? false);
        return property!;
    }
}
