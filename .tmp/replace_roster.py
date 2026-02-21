import re

path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Pattern to find the Roster stack panel block
p_roster = r'<StackPanel Orientation="Horizontal" Spacing="10">\s*<TextBox x:Name="NewStudentNameBox".*?</StackPanel>'

replacement = r'''<Grid ColumnDefinitions="Auto,*,Auto">
                        <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="8">
                            <TextBox x:Name="NewStudentNameBox"
                                     Width="200"
                                     Watermark="输入学生姓名..." />
                            <Button x:Name="AddStudentBtn"
                                    Content="添加"
                                    Classes="accent"
                                    Click="OnAddStudentClick" />
                        </StackPanel>
                        <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8">
                            <Button x:Name="ToggleStudentActiveBtn"
                                    Content="切换状态"
                                    IsEnabled="False"
                                    Click="OnToggleStudentActiveClick" />
                            <Button x:Name="DeleteStudentBtn"
                                    ToolTip.Tip="删除选中"
                                    Click="OnDeleteStudentClick">
                                <TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE74D;" Foreground="#E81123" />
                            </Button>
                            <Button x:Name="ImportRosterFromFileBtn"
                                    Content="导入..."
                                    Click="OnImportRosterFromFileClick" />
                            <Button x:Name="OpenRosterBtn"
                                    ToolTip.Tip="打开源文件"
                                    Click="OnOpenRosterClick">
                                <TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE8E5;" />
                            </Button>
                        </StackPanel>
                    </Grid>'''

if re.search(p_roster, content, flags=re.DOTALL):
    content = re.sub(p_roster, replacement, content, flags=re.DOTALL)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(content)
    print('Roster Button Bar replaced successfully!')
else:
    print('Failed to find roster block')
