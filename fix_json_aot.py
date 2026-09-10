import os

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

r("src/LinuxWebTool.WebHost/Middleware/ExceptionHandlingMiddleware.cs", 
  "context.Response.WriteAsJsonAsync(new MessageResponse(message));", 
  "context.Response.WriteAsJsonAsync(new MessageResponse(message), LinuxWebTool.WebHost.Composition.AppJsonSerializerContext.Default.MessageResponse);")

r("src/LinuxWebTool.WebHost/Composition/PipelineExtensions.cs",
  "context.Response.WriteAsJsonAsync(new MessageResponse(\"未授权\"));",
  "context.Response.WriteAsJsonAsync(new MessageResponse(\"未授权\"), AppJsonSerializerContext.Default.MessageResponse);")
  
r("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs",
  "var json = JsonSerializer.Serialize(list);",
  "var json = JsonSerializer.Serialize(list, LinuxWebTool.WebHost.Composition.AppJsonSerializerContext.Default.ListTranscodePreset);")
