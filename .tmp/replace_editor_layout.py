import re

path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Pattern for the editor border
p_editor = r'<Border BorderBrush="\{DynamicResource CardStrokeColorDefaultBrush\}" BorderThickness="1" CornerRadius="8" Padding="12" Margin="0,2,0,0">\s*<StackPanel Spacing="8">\s*<TextBlock Text="手动编辑选中日期值日安排" FontWeight="SemiBold" />.*?<StackPanel Orientation="Horizontal" Spacing="8">.*?<Button x:Name="CreateScheduleEditBtn".*?/>\s*</StackPanel>\s*</StackPanel>\s*</Border>'

replacement = r'''<!-- 极简排班编辑器 -->
                    <Border Background="#08000000" CornerRadius="4" Padding="12,8" Margin="0,2,0,0">
                        <StackPanel Spacing="8">
                            <!-- 标题与状态行 -->
                            <Grid ColumnDefinitions="Auto,*">
                                <TextBlock Grid.Column="0" Text="编辑排班详情" FontWeight="SemiBold" VerticalAlignment="Center" />
                                <TextBlock x:Name="SelectedScheduleMetaText" Grid.Column="1" Text="请先在上方列表选择记录" Opacity="0.6" FontSize="12" Margin="12,0,0,0" VerticalAlignment="Center" />
                            </Grid>

                            <!-- 日期与星期 -->
                            <Grid ColumnDefinitions="Auto,150,Auto,100" ColumnSpacing="8">
                                <TextBlock Grid.Column="0" Text="日期" VerticalAlignment="Center" Opacity="0.8" />
                                <TextBox x:Name="ScheduleDateEditorBox" Grid.Column="1" TextChanged="OnScheduleEditorTextChanged" Watermark="yyyy-MM-dd" />
                                <TextBlock Grid.Column="2" Text="星期" VerticalAlignment="Center" Opacity="0.8" />
                                <ComboBox x:Name="ScheduleDayEditorComboBox" Grid.Column="3" SelectionChanged="OnScheduleEditorSelectionChanged" HorizontalAlignment="Stretch" />
                            </Grid>

                            <!-- 安排与备注 -->
                            <Grid ColumnDefinitions="Auto,*" ColumnSpacing="8">
                                <StackPanel Grid.Column="0" Spacing="4" Margin="0,6,0,0">
                                    <TextBlock Text="安排" Opacity="0.8" />
                                    <TextBlock Text="区域: 姓名" FontSize="12" Opacity="0.5" />
                                </StackPanel>
                                <TextBox x:Name="ScheduleAssignmentsEditorBox" Grid.Column="1" TextChanged="OnScheduleEditorTextChanged" Height="64" AcceptsReturn="True" TextWrapping="Wrap" />
                            </Grid>

                            <Grid ColumnDefinitions="Auto,*" ColumnSpacing="8">
                                <TextBlock Grid.Column="0" Text="备注" VerticalAlignment="Center" Opacity="0.8" Margin="0,0,24,0" />
                                <TextBox x:Name="ScheduleNoteEditorBox" Grid.Column="1" TextChanged="OnScheduleEditorTextChanged" TextWrapping="Wrap" Watermark="附加说明..." />
                            </Grid>

                            <!-- 操作按钮区 -->
                            <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right" Margin="0,4,0,0">
                                <Button x:Name="CreateScheduleEditBtn" Content="新建记录" Click="OnCreateScheduleEditClick" />
                                <Button x:Name="ReloadScheduleEditBtn" Content="放弃更改" Click="OnReloadScheduleEditClick" />
                                <Button x:Name="SaveScheduleEditBtn" Content="保存编辑" Classes="accent" IsEnabled="False" Click="OnSaveScheduleEditClick" />
                            </StackPanel>
                        </StackPanel>
                    </Border>'''

if re.search(p_editor, content, flags=re.DOTALL):
    content = re.sub(p_editor, replacement, content, flags=re.DOTALL)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(content)
    print('Editor Layout optimized successfully!')
else:
    print('Pattern not found in XAML.')
