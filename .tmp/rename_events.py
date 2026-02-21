path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('TextChanged="OnScheduleEditorChanged"', 'TextChanged="OnScheduleEditorTextChanged"')
content = content.replace('SelectionChanged="OnScheduleEditorChanged"', 'SelectionChanged="OnScheduleEditorSelectionChanged"')

with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write(content)
print("XAML events renamed")
