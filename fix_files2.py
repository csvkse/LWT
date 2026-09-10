import os

def fix_file(path):
    if not os.path.exists(path): return
    with open(path, 'r', encoding='utf-8') as f:
        c = f.read()

    # SystemStatusController
    # none?
    
    # LogsController
    c = c.replace('new { message = "日志文件不存在或文件名非法" }', 'new MessageResponse("日志文件不存在或文件名非法")')
    
    # HistoryController
    c = c.replace('new { message = "已清空所有历史记录" }', 'new MessageResponse("已清空所有历史记录")')

    # CommandsController
    c = c.replace('new { command.Id }', 'new IdResponse(command.Id)')
    c = c.replace('new { message = "指令不存在" }', 'new MessageResponse("指令不存在")')
    c = c.replace('new { message = "同名指令已存在" }', 'new MessageResponse("同名指令已存在")')
    c = c.replace('new { message = "已删除" }', 'new MessageResponse("已删除")')

    with open(path, 'w', encoding='utf-8') as f:
        f.write(c)

fix_file('src/LinuxWebTool.WebHost/Routes/LogsController.cs')
fix_file('src/LinuxWebTool.WebHost/Routes/HistoryController.cs')
fix_file('src/LinuxWebTool.WebHost/Routes/CommandsController.cs')
