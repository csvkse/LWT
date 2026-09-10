import os, re

types = set()

def scan_file(path):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    # Find all public class/record
    for m in re.finditer(r'public (?:class|record|struct) (\w+)', c):
        types.add(m.group(1))

for root, _, files in os.walk('src'):
    for file in files:
        if file.endswith('.cs') and "obj" not in root and "bin" not in root: 
            scan_file(os.path.join(root, file))

types.add('System.Collections.Generic.List<string>')
types.add('System.Collections.Generic.List<object>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.CommandGroup>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.LinuxCommand>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.OperationLog>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.ScheduleTask>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SmbMount>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusDiskSnapshot>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusNetSnapshot>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusProcessSnapshot>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusSnapshot>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.TranscodeJob>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.TranscodePreset>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.WatchRule>')
types.add('System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.ExecutionRecord>')

code = """using System.Text.Json.Serialization;
using LinuxWebTool.WebHost.Routes;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Persistence;

namespace LinuxWebTool.WebHost.Composition;

"""

for t in sorted(types):
    if t not in ["Program", "DbSetup", "ServiceCollectionExtensions", "PipelineExtensions", "AppJsonSerializerContext"]:
        code += f"[JsonSerializable(typeof({t}))]\n"

code += """public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
"""

with open("src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs", 'w', encoding='utf-8') as f:
    f.write(code)
