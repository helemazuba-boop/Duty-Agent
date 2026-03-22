import re
path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'

try:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    # Add TextChanged/SelectionChanged to the 4 editor boxes
    content = content.replace(
        '<TextBox x:Name="ScheduleDateEditorBox"',
        '<TextBox x:Name="ScheduleDateEditorBox"\n                                             TextChanged="OnScheduleEditorChanged"'
    )
    content = content.replace(
        '<ComboBox x:Name="ScheduleDayEditorComboBox"',
        '<ComboBox x:Name="ScheduleDayEditorComboBox"\n                                              SelectionChanged="OnScheduleEditorChanged"'
    )
    content = content.replace(
        '<TextBox x:Name="ScheduleAssignmentsEditorBox"',
        '<TextBox x:Name="ScheduleAssignmentsEditorBox"\n                                     TextChanged="OnScheduleEditorChanged"'
    )
    content = content.replace(
        '<TextBox x:Name="ScheduleNoteEditorBox"',
        '<TextBox x:Name="ScheduleNoteEditorBox"\n                                     TextChanged="OnScheduleEditorChanged"'
    )

    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(content)
    print('XAML events for anti-misclick protection added successfully!')
except Exception as e:
    print(f"Error: {e}")
