import clr
import sys
import os

assembly_path = r"D:\ClassIsland_app_windows_x64_full_folder\app-2.0.0.2-0\ClassIsland.Core.dll"
sys.path.append(os.path.dirname(assembly_path))

try:
    clr.AddReference("ClassIsland.Core")
    import ClassIsland.Core.Models.Notification as Notification
    
    print("NotificationRequest Properties:")
    req_type = Notification.NotificationRequest().__class__
    for prop in dir(req_type):
        if not prop.startswith("_"):
            print(f" - {prop}")
            
    print("\nNotificationContent Properties:")
    content_type = Notification.NotificationContent().__class__
    for prop in dir(content_type):
        if not prop.startswith("_"):
            print(f" - {prop}")
            
    print("\nNotificationSettings Properties:")
    settings_type = clr.AddReference("ClassIsland.Shared")
    import ClassIsland.Shared.Models.Notification as SharedNotification
    set_type = SharedNotification.NotificationSettings().__class__
    for prop in dir(set_type):
        if not prop.startswith("_"):
            print(f" - {prop}")
            
except Exception as e:
    print(f"Error: {e}")
