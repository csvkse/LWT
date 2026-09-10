import os
import re

def fix_file(path):
    if not os.path.exists(path): return
    with open(path, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    for i in range(len(lines)):
        c = lines[i]
        
        # simple new { message = "..." } or new { message = $"..." }
        match = re.search(r'new\s*\{\s*message\s*=\s*([^,}]+)\s*\}', c)
        if match and not 'tooLarge' in c and not 'binary' in c and not 'needRecursive' in c:
            c = c.replace(match.group(0), f'new MessageResponse({match.group(1).strip()})')

        # specific ones
        c = c.replace('new { message = $"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", tooLarge = true }', 'new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false)')
        c = c.replace('new { message = "二进制文件，无法以文本查看", binary = true }', 'new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true)')
        c = c.replace('new { path = normalized, name = info.Name, size = info.Length, content }', 'new FileContentResponse(normalized, info.Name, info.Length, content)')
        c = c.replace('new { message = "已创建", path = normalized }', 'new CreateFileResponse("已创建", normalized)')
        c = c.replace('new { message = "已重命名", path = to }', 'new RenameFileResponse("已重命名", to)')
        c = c.replace('new { message = "目录非空，确认后使用递归删除", needRecursive = true }', 'new DeleteFileErrorResponse("目录非空，确认后使用递归删除", true)')
        c = c.replace('new { message = "已上传", path = target }', 'new UploadFileResponse("已上传", target)')
        
        lines[i] = c

    with open(path, 'w', encoding='utf-8') as f:
        f.writelines(lines)

fix_file('src/LinuxWebTool.WebHost/Routes/FilesController.cs')
