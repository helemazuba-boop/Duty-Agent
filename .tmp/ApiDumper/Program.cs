using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ApiDumper
{
    class Program
    {
        static void Main(string[] args)
        {
            var sb = new StringBuilder();
            try
            {
                var corePath = @"d:\ClassIsland_app_windows_x64_full_folder\app-2.0.0.2-0\ClassIsland.Core.dll";
                var sharedPath = @"d:\ClassIsland_app_windows_x64_full_folder\app-2.0.0.2-0\ClassIsland.Shared.dll";
                var assembly = Assembly.LoadFrom(corePath);
                Assembly.LoadFrom(sharedPath);

                var types = assembly.GetTypes()
                    .Where(t => t.Name == "NotificationRequest" || t.Name == "NotificationContent" || t.Name == "RollingTextTemplate")
                    .ToList();

                sb.AppendLine($"Found {types.Count} matching types.");

                foreach (var t in types)
                {
                    sb.AppendLine($"Type: {t.FullName} (IsInterface: {t.IsInterface})");
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        sb.AppendLine($"  Property: {p.PropertyType.Name} {p.Name}");
                    }
                    foreach (var m in t.GetConstructors())
                    {
                        sb.AppendLine($"  Constructor: {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Error: " + ex);
            }
            File.WriteAllText(@"c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\.tmp\ApiDumper\api_clean.txt", sb.ToString(), Encoding.UTF8);
        }
    }
}
