import os
import re

def process_xaml():
    path = r'c:\Users\ZhuanZ\OneDrive\Desktop\Duty-Agent\Views\SettingPages\DutyMainSettingsPage.axaml'
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    # The content is wrapped in:
    # <ScrollViewer>
    #     <StackPanel Margin="24,18,24,26" Spacing="14">
    #         [BLOCKS]
    #     </StackPanel>
    # </ScrollViewer>
    
    start_str = '<StackPanel Margin="24,18,24,26" Spacing="14">'
    end_str = '</StackPanel>\n    </ScrollViewer>'
    
    start_idx = content.find(start_str) + len(start_str)
    end_idx = content.find(end_str)
    
    inner_content = content[start_idx:end_idx]
    
    # We can split the inner_content by looking for borders that define the main cards.
    # Note: there is a nested Border inside Schedule Preview, but we can look for:
    # '            <Border Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"'
    
    card_split_token = '            <Border Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"'
    parts = inner_content.split(card_split_token)
    
    # parts[0] is just whitespace
    cards = []
    for part in parts[1:]:
        card = card_split_token + part
        cards.append(card)
        
    # We should have exactly 8 cards based on previous analysis.
    # 0: Status
    # 1: Execute
    # 2: API & Model
    # 3: AI Rule
    # 4: Params
    # 5: Roster
    # 6: Debug
    # 7: Schedule Preview
    
    assert len(cards) >= 8, f"Expected at least 8 cards, found {len(cards)}"
    
    # Let's map them by looking at their content
    def get_card(keyword):
        for c in cards:
            if keyword in c:
                return c
        return None
        
    c_status = cards[0]
    c_execute = cards[1]
    c_api = cards[2]
    c_rule = cards[3]
    c_params = cards[4]
    c_roster = cards[5]
    c_debug = cards[6]
    c_preview = cards[7]
    
    # Modifications to c_execute: 
    # Add Reasoning Board before the closing </StackPanel> inside the Card.
    # The Execute card ends with:
    #                             <TextBlock Text="开始排班（覆盖）" VerticalAlignment="Center" />
    #                         </StackPanel>
    #                     </Button>
    #                 </StackPanel>
    #             </Border>
    
    reasoning_board_xaml = """
                    <!-- [新] AI 思考黑板 -->
                    <Border x:Name="ReasoningBoardContainer" 
                            IsVisible="False"
                            Background="#08000000" 
                            BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}"
                            BorderThickness="1"
                            CornerRadius="6"
                            Margin="0,4,0,0">
                        <ScrollViewer x:Name="ReasoningBoardScrollViewer" MaxHeight="250" Padding="12">
                            <TextBlock x:Name="ReasoningBoardText"
                                       TextWrapping="Wrap"
                                       FontFamily="Consolas, Courier New, monospace"
                                       FontSize="13"
                                       Opacity="0.85" />
                        </ScrollViewer>
                    </Border>
"""
    
    # Insert before the last `                </StackPanel>\n            </Border>`
    insert_idx = c_execute.rfind('                </StackPanel>')
    c_execute = c_execute[:insert_idx] + reasoning_board_xaml + c_execute[insert_idx:]
    
    # Helper to wrap card in Expander
    def wrap_in_expander(header_icon, header_text, desc_text, card_xaml):
        # We replace the outer Border with an Expander.
        # But to be safe and keep same inner look, we can wrap the whole Border in Expander, 
        # or change the top level Border to Expander.
        # Actually, using Avalonia Expander:
        
        # We can extract the inner StackPanel of the card.
        # card_xaml starts with <Border...> and ends with </Border>
        
        # Let's just wrap the entire Border into an Expander? No, Border looks like a card.
        # If we just put the StackPanel inside Expander, it's cleaner.
        
        # Find the first `<StackPanel Spacing="10">` that wraps the content.
        content_start = card_xaml.find('<StackPanel Spacing="10">')
        content_end = card_xaml.rfind('</StackPanel>')
        inner_stack = card_xaml[content_start:content_end] + '</StackPanel>'
        
        # Remove the original Header from the inner_stack (the StackPanel with Icon + Text)
        header_end = inner_stack.find('</StackPanel>') + len('</StackPanel>')
        # Note: the description TextBlock comes right after header. We can keep it or remove it.
        # Let's just keep everything inside, but wrap XAML inside Expander.
        
        # To make it simple and safe:
        expander_xaml = f"""            <Expander HorizontalAlignment="Stretch" Padding="16">
                <Expander.Header>
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock FontFamily="Segoe Fluent Icons" Text="{header_icon}" FontSize="16" VerticalAlignment="Center" />
                        <TextBlock Text="{header_text}" FontSize="16" FontWeight="SemiBold" VerticalAlignment="Center" />
                    </StackPanel>
                </Expander.Header>
                <Expander.Content>
                    <StackPanel Spacing="10" Margin="0,8,0,0">
{card_xaml}
                    </StackPanel>
                </Expander.Content>
            </Expander>
"""
        return expander_xaml

    # Wrap the secondary sections
    exp_roster = wrap_in_expander("&#xE125;", "花名册管理", "", c_roster)
    exp_api = wrap_in_expander("&#xE8D2;", "连接与模型", "", c_api)
    exp_rule = wrap_in_expander("&#xE945;", "AI 长期规则", "", c_rule)
    exp_params = wrap_in_expander("&#xE823;", "排班处理参数", "", c_params)
    exp_debug = wrap_in_expander("&#xE943;", "调试与集成", "", c_debug)

    # Reassemble
    new_inner = "\n".join([
        c_status,
        c_execute,
        c_preview,   # Pushed up!
        exp_roster,
        exp_api,
        exp_rule,
        exp_params,
        exp_debug
    ])
    
    final_content = content[:start_idx] + "\n" + new_inner + "\n" + content[end_idx:]
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(final_content)
        
    print("XAML rearranged completely!")

if __name__ == '__main__':
    process_xaml()
