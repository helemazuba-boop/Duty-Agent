import os
import glob

def rename_class(root_dir):
    cs_files = glob.glob(os.path.join(root_dir, '**', '*.cs'), recursive=True)
    for path in cs_files:
        with open(path, 'r', encoding='utf-8') as f:
            content = f.read()
        
        if 'DutyBackendService' in content:
            content = content.replace('DutyBackendService', 'DutyScheduleOrchestrator')
            with open(path, 'w', encoding='utf-8') as f:
                f.write(content)
            print("Updated:", path)
            
    # Rename the file itself
    old_file = os.path.join(root_dir, 'Services', 'DutyBackendService.cs')
    new_file = os.path.join(root_dir, 'Services', 'DutyScheduleOrchestrator.cs')
    if os.path.exists(old_file):
        os.rename(old_file, new_file)
        print("Renamed file to", new_file)

if __name__ == '__main__':
    rename_class(r"c:\\Users\\ZhuanZ\\OneDrive\\Desktop\\Duty-Agent")
