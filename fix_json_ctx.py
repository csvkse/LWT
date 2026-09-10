import os
with open("src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs", 'r', encoding='utf-8') as f: c = f.read()
c = c.replace('[JsonSerializable(typeof(PagedResponse))]\n', '')
c = c.replace('public partial class AppJsonSerializerContext', """
[JsonSerializable(typeof(PagedResponse<LinuxCommand>))]
[JsonSerializable(typeof(PagedResponse<OperationLog>))]
[JsonSerializable(typeof(PagedResponse<TranscodeJobBrief>))]
public partial class AppJsonSerializerContext
""")
with open("src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs", 'w', encoding='utf-8') as f: f.write(c)
