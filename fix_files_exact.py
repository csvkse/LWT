import os

def replace_exact(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = $"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", tooLarge = true }', 'new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = "二进制文件，无法以文本查看", binary = true }', 'new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { path = normalized, name = info.Name, size = info.Length, content }', 'new FileContentResponse(normalized, info.Name, info.Length, content)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = "已创建", path = normalized }', 'new CreateFileResponse("已创建", normalized)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = "已重命名", path = to }', 'new RenameFileResponse("已重命名", to)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = "目录非空，确认后使用递归删除", needRecursive = true }', 'new DeleteFileErrorResponse("目录非空，确认后使用递归删除", true)')
replace_exact('src/LinuxWebTool.WebHost/Routes/FilesController.cs', 'new { message = "已上传", path = target }', 'new UploadFileResponse("已上传", target)')

replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { items = mapped, total }', 'new PagedResponse<TranscodeJobBrief>(mapped, total)')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { message = count > 0 ? $"已加入转码队列 {count} 个文件" : "未发现匹配的媒体文件", count }', 'new MessageResponse(count > 0 ? $"已加入转码队列 {count} 个文件" : "未发现匹配的媒体文件")')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { message = "已加入转码队列", count = 1, jobId = job.Id }', 'new MessageResponse("已加入转码队列")')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { preset.Id }', 'new IdResponse(preset.Id)')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { imported, skipped, messages }', 'new ImportResponse(imported, skipped, messages)')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { rule.Id }', 'new IdResponse(rule.Id)')
replace_exact('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs', 'new { rule.Enabled, rule.Id }', 'new RuleStatusResponse(rule.Enabled, rule.Id)')

replace_exact('src/LinuxWebTool.WebHost/Routes/SmbMountsController.cs', 'new { mount.Id }', 'new IdResponse(mount.Id)')
replace_exact('src/LinuxWebTool.WebHost/Routes/CommandsController.cs', 'new { command.Id }', 'new IdResponse(command.Id)')
replace_exact('src/LinuxWebTool.WebHost/Routes/LogsController.cs', 'new { name, tail, content }', 'new LogContentResponse(name, tail, content)')
replace_exact('src/LinuxWebTool.WebHost/Routes/SchedulesController.cs', 'new { task.Enabled, task.NextRunTime }', 'new ScheduleStatusResponse(task.Enabled, task.NextRunTime)')

