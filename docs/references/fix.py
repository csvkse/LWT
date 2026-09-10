with open("src/LinuxWebTool.WebHost/Composition/ServiceCollectionExtensions.cs", "r", encoding="utf-8") as f:
    lines = f.readlines()

new_lines = []
skip = False
for line in lines:
    if skip:
        if "});" in line:
            skip = False
        continue

    if "builder.Services.AddAutoControllers();" in line or "builder.services.AddAutoControllers();" in line:
        new_lines.append("        builder.Services.AddAutoControllers();\n")
        skip = True
        continue
    
    if "`n" in line:
        continue
        
    new_lines.append(line)

with open("src/LinuxWebTool.WebHost/Composition/ServiceCollectionExtensions.cs", "w", encoding="utf-8") as f:
    f.writelines(new_lines)
