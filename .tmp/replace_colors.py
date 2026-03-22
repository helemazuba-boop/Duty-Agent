import os

path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'

with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('Background="#14FFFFFF"', 'Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"')
content = content.replace('BorderBrush="#22000000"', 'BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}"')
content = content.replace('CornerRadius="12"', 'CornerRadius="8"')

with open(path, 'w', encoding='utf-8', newline='') as f:
    f.write(content)

print("Replaced background colors successfully.")
