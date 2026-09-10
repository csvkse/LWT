import os

with open("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs", 'r', encoding='utf-8') as f:
    c = f.read()

c = c.replace("""        var mapped = items.Select(j => new
        {
            j.Id,
            j.SourcePath,
            j.OutputPath,
            j.PresetName,
            j.OutputMode,
            j.Trigger,
            j.WatchRuleId,
            j.Status,
            j.Progress,
            j.SpeedText,
            j.DurationMs,
            j.ErrorOutput,
            j.SourceSizeBytes,
            j.OutputSizeBytes,
            j.QueueTime,
            j.StartTime,
            j.EndTime,
            j.UseHardwareAccel,
            j.HardwareBackend,
            j.UsedHardwareAccel,
            j.CommandLine,
            j.FallbackReason,
            j.FallbackFromCommand,
            j.IsFullCommand,
        });""", """        var mapped = items.Select(j => new TranscodeJobBrief(
            j.Id,
            j.SourcePath,
            j.OutputPath,
            j.PresetName,
            j.OutputMode,
            j.Trigger,
            j.WatchRuleId,
            j.Status,
            j.Progress,
            j.SpeedText,
            j.DurationMs,
            j.ErrorOutput,
            j.SourceSizeBytes,
            j.OutputSizeBytes,
            j.QueueTime,
            j.StartTime,
            j.EndTime,
            j.UseHardwareAccel,
            j.HardwareBackend,
            j.UsedHardwareAccel,
            j.CommandLine,
            j.FallbackReason,
            j.FallbackFromCommand,
            j.IsFullCommand
        ));""")

with open("src/LinuxWebTool.WebHost/Routes/TranscodeController.cs", 'w', encoding='utf-8') as f:
    f.write(c)
