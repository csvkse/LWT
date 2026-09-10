import os
import re

context_file = "src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs"
with open(context_file, "r", encoding="utf-8") as f:
    content = f.read()
    
# Find types in Contracts/Models
models_dir = "src/LinuxWebTool.Contracts/Models"
types = set()
for fn in os.listdir(models_dir):
    if not fn.endswith(".cs"): continue
    with open(os.path.join(models_dir, fn), "r", encoding="utf-8") as m:
        m_content = m.read()
        # Find public (sealed)? (record|class|struct) Name
        matches = re.findall(r'public\s+(?:sealed\s+)?(?:record|class|struct)\s+(\w+)', m_content)
        for m in matches:
            types.add(m)
            
# Find types in WebHost/Routes
routes_dir = "src/LinuxWebTool.WebHost/Routes"
for fn in os.listdir(routes_dir):
    if not fn.endswith(".cs"): continue
    with open(os.path.join(routes_dir, fn), "r", encoding="utf-8") as m:
        m_content = m.read()
        matches = re.findall(r'public\s+(?:sealed\s+)?(?:record|class|struct)\s+(\w+)', m_content)
        for m in matches:
            if not m.endswith("Controller"):
                types.add(m)

# Add to AppJsonSerializerContext if not present
new_lines = []
for t in sorted(list(types)):
    if f"typeof({t})" not in content:
        new_lines.append(f"[JsonSerializable(typeof({t}))]")

if new_lines:
    # Insert before public partial class AppJsonSerializerContext
    insert_pos = content.find("public partial class AppJsonSerializerContext")
    new_content = content[:insert_pos] + "\n".join(new_lines) + "\n" + content[insert_pos:]
    with open(context_file, "w", encoding="utf-8") as f:
        f.write(new_content)
    print(f"Added {len(new_lines)} types to AppJsonSerializerContext.")
else:
    print("No types needed to be added.")
