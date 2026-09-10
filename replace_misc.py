import os, re

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

r("src/LinuxWebTool.WebHost/Routes/AuthController.cs", 
  "new { userName = User.Identity?.Name ?? string.Empty }", 
  "new UserInfoResponse(User.Identity?.Name ?? string.Empty)")

r("src/LinuxWebTool.WebHost/Routes/FilesController.cs", 
  "new { path = normalized, name = info.Name, size = info.Length, content }", 
  "new FileContentResponse(normalized, info.Name, info.Length, content)")

r("src/LinuxWebTool.WebHost/Routes/LogsController.cs", 
  "new { name, tail, content }", 
  "new LogContentResponse(name, tail, content)")

r("src/LinuxWebTool.WebHost/Routes/SchedulesController.cs", 
  "new { task.Enabled, task.NextRunTime }", 
  "new ScheduleStatusResponse(task.Enabled, task.NextRunTime)")

r("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs", 
  "new { items = mapped, total }", 
  "new PagedResponse<TranscodeJobBrief>(mapped, total)")

r("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs", 
  "new { imported, skipped, messages }", 
  "new ImportResponse(imported, skipped, messages)")

r("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs", 
  "new { rule.Enabled, rule.Id }", 
  "new RuleStatusResponse(rule.Enabled, rule.Id)")
