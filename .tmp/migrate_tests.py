import re
import json

def replacer(match):
    json_str = match.group(1)
    
    try:
        # We might need to unescape strings first if they are python raw strings or something, 
        # but usually json.loads handles it.
        # test_core.py often has single quotes inside or other weird formatting, let's just attempt JSON load.
        # Sometimes test files have python dict strings, not valid json. We'll evaluate it with eval if json fails.
        try:
            data = json.loads(json_str)
        except:
            # Fallback to python eval because test files often use `{"date": "2023-10-10"}` which might be missing quotes around keys.
            data = eval(json_str)
            
        csv_lines = ['Date,Assigned_IDs,Note']
        for entry in data.get('schedule', []):
            date = entry.get('date', '')
            area_ids_dict = entry.get('area_ids', {})
            ids = ','.join(str(v) for v in area_ids_dict.values())
            note = entry.get('note', '')
            csv_lines.append(f'{date},{ids},{note}')
            
        csv_block = '\n'.join(csv_lines)
        
        xml_parts = [f'<csv>\n{csv_block}\n</csv>']
        
        if 'next_run_note' in data:
            xml_parts.append(f'<next_run_note>{data.get("next_run_note", "")}</next_run_note>')
        if 'new_debt_ids' in data:
            xml_parts.append(f'<new_debt_ids>{data.get("new_debt_ids", "")}</new_debt_ids>')
        if 'new_credit_ids' in data:
            xml_parts.append(f'<new_credit_ids>{data.get("new_credit_ids", "")}</new_credit_ids>')
            
        res = '\n'.join(xml_parts)
        return f'"""\n{res}\n"""'
    except Exception as e:
        print('Error parsing chunk:\n', json_str, '\nError:', e)
        return match.group(0)

def main():
    file_path = 'Assets_Duty/test_core.py'
    with open(file_path, 'r', encoding='utf-8') as f:
        code = f.read()

    # Find anything that looks like: """{ "schedule": [...] }"""
    # The regex looks for triple quotes, optional whitespace, a `{` containing `"schedule"`, and ending with triple quotes.
    pattern = re.compile(r'\"\"\"(?:\\n|\n|\s)*(\{.*?\"schedule\".*?\})(?:\\n|\n|\s)*\"\"\"', re.DOTALL | re.IGNORECASE)
    
    new_code = pattern.sub(replacer, code)

    # Let's also check if there are single-quoted versions: '''{ "schedule": [...] }'''
    pattern2 = re.compile(r"\'\'\'(?:\\n|\n|\s)*(\{.*?\"schedule\".*?\})(?:\\n|\n|\s)*\'\'\'", re.DOTALL | re.IGNORECASE)
    new_code = pattern2.sub(replacer, new_code)
    
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write(new_code)
    print("Migration complete.")

if __name__ == '__main__':
    main()
