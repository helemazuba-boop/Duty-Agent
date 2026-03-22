import re

path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# For Roster
p_roster = r'(<ListBox x:Name="RosterListBox"[\s\S]*?</ListBox>)'
replacement_roster = r'''\1
                            <StackPanel x:Name="RosterEmptyStatePanel" Grid.Row="1" VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="8" IsVisible="False">
                                <TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE716;" FontSize="48" Opacity="0.4" HorizontalAlignment="Center" />
                                <TextBlock Text="当前暂无学生名单数据" Opacity="0.6" FontSize="14" FontWeight="SemiBold" HorizontalAlignment="Center" />
                                <TextBlock Text="请在上方添加或导入名单" Opacity="0.5" FontSize="12" HorizontalAlignment="Center" />
                            </StackPanel>'''

# For Schedule
p_schedule = r'(<ListBox x:Name="ScheduleListBox"[\s\S]*?</ListBox>)'
replacement_schedule = r'''\1
                            <StackPanel x:Name="ScheduleEmptyStatePanel" Grid.Row="1" VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="8" IsVisible="False">
                                <TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE8A5;" FontSize="48" Opacity="0.4" HorizontalAlignment="Center" />
                                <TextBlock Text="暂无排班预期数据" Opacity="0.6" FontSize="14" FontWeight="SemiBold" HorizontalAlignment="Center" />
                                <TextBlock Text="点击上方【刷新预览】或【开始排班】生成" Opacity="0.5" FontSize="12" HorizontalAlignment="Center" />
                            </StackPanel>'''

content = re.sub(p_roster, replacement_roster, content)
content = re.sub(p_schedule, replacement_schedule, content)

with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write(content)
print('Empty states XAML replaced successfully!')
