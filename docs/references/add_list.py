import os
import re

context_file = "src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs"
with open(context_file, "r", encoding="utf-8") as f:
    content = f.read()

types_to_add = set(["System.Collections.Generic.List<LinuxWebTool.Contracts.Models.PresetImportItem>"])

new_lines = []
for t in types_to_add:
    if f"typeof({t})" not in content and f"typeof({t.split('.')[-1]})" not in content:
        new_lines.append(f"[JsonSerializable(typeof({t}))]")

if new_lines:
    insert_pos = content.find("public partial class AppJsonSerializerContext")
    new_content = content[:insert_pos] + "\n".join(new_lines) + "\n" + content[insert_pos:]
    with open(context_file, "w", encoding="utf-8") as f:
        f.write(new_content)
    print(f"Added {len(new_lines)} types to AppJsonSerializerContext.")
