using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            var faAssembly = Assembly.LoadFrom(@"C:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\.dotnet-home\.nuget\packages\fluentavaloniaui\2.0.5\lib\net6.0\FluentAvalonia.dll");
            Console.WriteLine("Contains SettingsCard: " + (faAssembly.GetType("FluentAvalonia.UI.Controls.SettingsCard") != null));
            var type = faAssembly.GetType("FluentAvalonia.UI.Controls.SettingsExpanderItem");
            if (type == null)
            {
                Console.WriteLine("Type SettingsExpanderItem not found");
                return;
            }
            Console.WriteLine("Properties of SettingsExpanderItem:");
            foreach (var prop in type.GetProperties())
            {
                Console.WriteLine($" - {prop.PropertyType.Name} {prop.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
}
