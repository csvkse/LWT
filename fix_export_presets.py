import os

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

path = "src/LinuxWebTool.WebHost/Routes/TranscodeController.cs"
old = """        var items = list.Select(p => new
        {
            name = p.Name,
            container = p.Container,
            videoCodec = p.VideoCodec,
            videoQuality = p.VideoQuality,
            audioCodec = p.AudioCodec,
            audioBitrate = p.AudioBitrate,
            extraArgs = p.ExtraArgs,
            description = p.Description,
            isBuiltin = p.IsBuiltin,
        });
        var json = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });"""
new = """        var items = list.Select(p => new PresetImportItem
        {
            Name = p.Name,
            Container = p.Container,
            VideoCodec = p.VideoCodec,
            VideoQuality = p.VideoQuality,
            AudioCodec = p.AudioCodec,
            AudioBitrate = p.AudioBitrate,
            ExtraArgs = p.ExtraArgs,
            Description = p.Description,
            IsBuiltin = p.IsBuiltin,
        });
        var json = System.Text.Json.JsonSerializer.Serialize(items, LinuxWebTool.WebHost.Composition.AppJsonSerializerContext.Default.IEnumerablePresetImportItem);"""
r(path, old, new)
