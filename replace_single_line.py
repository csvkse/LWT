import os, re

def process(path):
    with open(path, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    for i in range(len(lines)):
        # new { message = "..." }
        lines[i] = re.sub(r'new\s*\{\s*message\s*=\s*(.+?)\s*\}', r'new MessageResponse(\1)', lines[i])
        # new { message }
        lines[i] = re.sub(r'new\s*\{\s*message\s*\}', r'new MessageResponse(message)', lines[i])
        # new { xxx.Id }
        lines[i] = re.sub(r'new\s*\{\s*([a-zA-Z0-9_]+\.Id)\s*\}', r'new IdResponse(\1)', lines[i])
        # new { items = mapped, total }
        lines[i] = re.sub(r'new\s*\{\s*items\s*=\s*([^,]+),\s*total\s*\}', r'new PagedResponse<TranscodeJobBrief>(\1, total)', lines[i])
        # new { task.Enabled, task.NextRunTime }
        lines[i] = re.sub(r'new\s*\{\s*([^,]+)\.Enabled,\s*([^,]+)\.NextRunTime\s*\}', r'new ScheduleStatusResponse(\1.Enabled, \2.NextRunTime)', lines[i])
        # new { imported, skipped, messages }
        lines[i] = re.sub(r'new\s*\{\s*imported,\s*skipped,\s*messages\s*\}', r'new ImportResponse(imported, skipped, messages)', lines[i])
        # new { rule.Enabled, rule.Id }
        lines[i] = re.sub(r'new\s*\{\s*([^,]+)\.Enabled,\s*([^,]+)\.Id\s*\}', r'new RuleStatusResponse(\1.Enabled, \2.Id)', lines[i])
        # new { path = normalized, name = info.Name, size = info.Length, content }
        lines[i] = re.sub(r'new\s*\{\s*path\s*=\s*([^,]+),\s*name\s*=\s*([^,]+),\s*size\s*=\s*([^,]+),\s*content\s*\}', r'new FileContentResponse(\1, \2, \3, content)', lines[i])
        # new { name, tail, content }
        lines[i] = re.sub(r'new\s*\{\s*name,\s*tail,\s*content\s*\}', r'new LogContentResponse(name, tail, content)', lines[i])
        
    with open(path, 'w', encoding='utf-8') as f:
        f.writelines(lines)

for root, _, files in os.walk('src/LinuxWebTool.WebHost/Routes'):
    for file in files:
        if file.endswith('.cs'):
            process(os.path.join(root, file))
