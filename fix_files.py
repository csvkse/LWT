import os

def fix_file(path):
    with open(path, 'r', encoding='utf-8') as f:
        c = f.read()

    # FilesController
    c = c.replace('new { message = $"目录不存在：{normalized}" }', 'new MessageResponse($"目录不存在：{normalized}")')
    c = c.replace('new { message = "未选择文件" }', 'new MessageResponse("未选择文件")')
    c = c.replace('new { message = "目标文件已存在" }', 'new MessageResponse("目标文件已存在")')
    c = c.replace('new { path = Path.GetDirectoryName(targetPath) ?? "/", name = fileName }', 'new FileContentResponse(Path.GetDirectoryName(targetPath) ?? "/", fileName, 0, "")')
    
    # TranscodeController
    c = c.replace('new { items = mapped, total }', 'new PagedResponse<TranscodeJobBrief>(mapped, total)')
    c = c.replace('new { message = "请填写源文件 / 源文件夹路径" }', 'new MessageResponse("请填写源文件 / 源文件夹路径")')
    c = c.replace('new { message = "请选择预设或填写自定义 ffmpeg 参数" }', 'new MessageResponse("请选择预设或填写自定义 ffmpeg 参数")')
    c = c.replace('new { message = "已加入转码队列", count = 1, jobId = job.Id }', 'new MessageResponse("已加入转码队列")')
    c = c.replace('new { message = count > 0 ? $"已加入转码队列 {count} 个文件" : "未发现匹配的媒体文件", count }', 'new MessageResponse(count > 0 ? $"已加入转码队列 {count} 个文件" : "未发现匹配的媒体文件")')
    c = c.replace('new { message = "源路径不存在（文件或文件夹均未找到），请检查路径是否为服务器本地可访问路径" }', 'new MessageResponse("源路径不存在（文件或文件夹均未找到），请检查路径是否为服务器本地可访问路径")')
    c = c.replace('new { message = "任务不存在" }', 'new MessageResponse("任务不存在")')
    c = c.replace('new { message = "任务已结束，无需取消" }', 'new MessageResponse("任务已结束，无需取消")')
    c = c.replace('new { message = "任务状态已变化，请刷新后重试" }', 'new MessageResponse("任务状态已变化，请刷新后重试")')
    c = c.replace('new { message = job.Status == (int)TranscodeJobStatus.Running ? "已发出取消指令，等待进程终止" : "已取消" }', 'new MessageResponse(job.Status == (int)TranscodeJobStatus.Running ? "已发出取消指令，等待进程终止" : "已取消")')
    c = c.replace('new { message = "仅失败的 / 已取消 / 已中断的任务可重试" }', 'new MessageResponse("仅失败的 / 已取消 / 已中断的任务可重试")')
    c = c.replace('new { message = "源文件不存在，无法重试" }', 'new MessageResponse("源文件不存在，无法重试")')
    c = c.replace('new { message = "已重新加入转码队列" }', 'new MessageResponse("已重新加入转码队列")')
    c = c.replace('new { message = $"已清理 {deleted} 条已结束记录" }', 'new MessageResponse($"已清理 {deleted} 条已结束记录")')
    c = c.replace('new { preset.Id }', 'new IdResponse(preset.Id)')
    c = c.replace('new { message = "转码预设不存在" }', 'new MessageResponse("转码预设不存在")')
    c = c.replace('new { message = "该预设正被监听规则使用，无法删除（请先修改或删除对应规则）" }', 'new MessageResponse("该预设正被监听规则使用，无法删除（请先修改或删除对应规则）")')
    c = c.replace('new { message = "该预设存在排队 / 运行中的任务，无法删除" }', 'new MessageResponse("该预设存在排队 / 运行中的任务，无法删除")')
    c = c.replace('new { message = "已删除" }', 'new MessageResponse("已删除")')
    c = c.replace('new { message = "导入内容为空" }', 'new MessageResponse("导入内容为空")')
    c = c.replace('new { message = "单次最多导入 500 个预设" }', 'new MessageResponse("单次最多导入 500 个预设")')
    c = c.replace('new { imported, skipped, messages }', 'new ImportResponse(imported, skipped, messages)')
    c = c.replace('new { rule.Id }', 'new IdResponse(rule.Id)')
    c = c.replace('new { message = "监听规则不存在" }', 'new MessageResponse("监听规则不存在")')
    c = c.replace('new { rule.Enabled, rule.Id }', 'new RuleStatusResponse(rule.Enabled, rule.Id)')
    
    # Generic new { message }
    c = c.replace('new { message }', 'new MessageResponse(message)')
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(c)

fix_file('src/LinuxWebTool.WebHost/Routes/FilesController.cs')
fix_file('src/LinuxWebTool.WebHost/Routes/TranscodeController.cs')
