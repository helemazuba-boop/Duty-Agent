using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            var assembly = Assembly.LoadFrom(@"D:\ClassIsland_app_windows_x64_full_folder\app-2.0.0.2-0\ClassIsland.Core.dll");
            var sharedAssembly = Assembly.LoadFrom(@"D:\ClassIsland_app_windows_x64_full_folder\app-2.0.0.2-0\ClassIsland.Shared.dll");

            Console.WriteLine("--- NotificationRequest Properties ---");
            var reqType = assembly.GetType("ClassIsland.Core.Models.Notification.NotificationRequest");
            if (reqType != null) {
                foreach (var prop in reqType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Console.WriteLine(prop.PropertyType.Name + " " + prop.Name);
                }
            }
            
            Console.WriteLine("\n--- NotificationContent Properties ---");
            var contType = assembly.GetType("ClassIsland.Core.Models.Notification.NotificationContent");
            if (contType != null) {
                foreach (var prop in contType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Console.WriteLine(prop.PropertyType.Name + " " + prop.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
}
