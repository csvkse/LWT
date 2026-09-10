import os

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

path = "src/LinuxWebTool.WebHost/Routes/GroupsController.cs"
r(path, 'new { message }', 'new MessageResponse(message)')
r(path, 'new { message = "分组不存在" }', 'new MessageResponse("分组不存在")')
r(path, 'new { message = $"分组下仍有 {usage} 个条目，请先移出后再删除" }', 'new MessageResponse($"分组下仍有 {usage} 个条目，请先移出后再删除")')
r(path, 'new { message = "已删除" }', 'new MessageResponse("已删除")')
