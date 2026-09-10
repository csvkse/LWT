import os
with open("src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs", 'r', encoding='utf-8') as f: c = f.read()
if "using LinuxWebTool.Contracts.Models;" not in c:
    c = c.replace("using LinuxWebTool.Infrastructure.Persistence.Entities;", "using LinuxWebTool.Infrastructure.Persistence.Entities;\nusing LinuxWebTool.Contracts.Models;")
with open("src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs", 'w', encoding='utf-8') as f: f.write(c)
