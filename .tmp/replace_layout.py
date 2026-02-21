import re
import os

path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'

try:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    # Refactor the 'API Key' and 'Model' Grid
    p_conn = r'(<TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE8D2;" FontSize="16" />\s*<TextBlock Text="连接与模型" FontSize="16" FontWeight="SemiBold" />\s*</StackPanel>).*?(</Border>)'

    replacement = r'''\1
                    <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto" RowSpacing="12" Margin="0,12,0,0">
                        <!-- API Key -->
                        <StackPanel Grid.Row="0" Grid.Column="0" VerticalAlignment="Center" Spacing="2" Margin="0,0,32,0">
                            <TextBlock Text="API Key" FontSize="14" VerticalAlignment="Center"/>
                            <TextBlock Text="用于访问大模型接口的鉴权密钥" FontSize="12" Opacity="0.55" VerticalAlignment="Center" />
                        </StackPanel>
                        <TextBox x:Name="ApiKeyBox" Grid.Row="0" Grid.Column="1" VerticalAlignment="Center" PasswordChar="*" LostFocus="OnConfigInputLostFocus" KeyDown="OnConfigInputKeyDown" />

                        <!-- Model -->
                        <StackPanel Grid.Row="1" Grid.Column="0" VerticalAlignment="Center" Spacing="2" Margin="0,0,32,0">
                            <TextBlock Text="模型名称" FontSize="14" VerticalAlignment="Center"/>
                            <TextBlock Text="例如: moonshotai/kimi-k2-thinking" FontSize="12" Opacity="0.55" VerticalAlignment="Center" />
                        </StackPanel>
                        <TextBox x:Name="ModelBox" Grid.Row="1" Grid.Column="1" VerticalAlignment="Center" Watermark="moonshotai/kimi-k2-thinking" LostFocus="OnConfigInputLostFocus" KeyDown="OnConfigInputKeyDown" />

                        <!-- Base URL -->
                        <StackPanel Grid.Row="2" Grid.Column="0" VerticalAlignment="Center" Spacing="2" Margin="0,0,32,0">
                            <TextBlock Text="Base URL" FontSize="14" VerticalAlignment="Center"/>
                            <TextBlock Text="提供与 OpenAI 兼容格式的转发层地址" FontSize="12" Opacity="0.55" VerticalAlignment="Center" />
                        </StackPanel>
                        <TextBox x:Name="BaseUrlBox" Grid.Row="2" Grid.Column="1" VerticalAlignment="Center" Watermark="https://integrate.api.nvidia.com/v1" LostFocus="OnConfigInputLostFocus" KeyDown="OnConfigInputKeyDown" />
                    </Grid>
                </StackPanel>
            \2'''
            
    if re.search(p_conn, content, flags=re.DOTALL):
        content = re.sub(p_conn, replacement, content, flags=re.DOTALL)
        with open(path, 'w', encoding='utf-8', newline='\r\n') as f:
            f.write(content)
        print('Connection Layout replaced successfully!')
    else:
        print('Pattern not found!')
except Exception as e:
    print(f"Error: {e}")
