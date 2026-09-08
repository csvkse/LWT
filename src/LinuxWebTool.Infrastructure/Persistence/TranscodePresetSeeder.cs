using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>内置转码预设播种：首次启动（预设表为空）时写入常用组合。</summary>
public static class TranscodePresetSeeder
{
    public static void Seed(ISqlSugarClient db)
    {
        if (db.Queryable<TranscodePreset>().Any())
        {
            return;
        }

        var presets = new List<TranscodePreset>
        {
            new()
            {
                Name = "MP4 H.264 通用",
                Container = "mp4",
                VideoCodec = "libx264",
                VideoQuality = 23,
                AudioCodec = "aac",
                AudioBitrate = "128k",
                ExtraArgs = "-preset medium",
                Description = "兼容性最好的通用转码预设（faststart 由系统自动附加）",
                IsBuiltin = true,
            },
            new()
            {
                Name = "MP4 H.265 高压缩",
                Container = "mp4",
                VideoCodec = "libx265",
                VideoQuality = 26,
                AudioCodec = "aac",
                AudioBitrate = "96k",
                ExtraArgs = "-tag:v hvc1 -preset medium",
                Description = "体积约省 40%，编码较慢；tag:hvc1 便于苹果设备播放",
                IsBuiltin = true,
            },
            new()
            {
                Name = "MKV 无损重封装",
                Container = "mkv",
                VideoCodec = "copy",
                AudioCodec = "copy",
                Description = "不重编码直接换容器，秒级完成（mp4→mkv 等）",
                IsBuiltin = true,
            },
            new()
            {
                Name = "MP3 音频提取",
                Container = "mp3",
                VideoCodec = "",
                AudioCodec = "libmp3lame",
                AudioBitrate = "192k",
                Description = "从视频中提取音频轨为 MP3",
                IsBuiltin = true,
            },
        };

        foreach (var preset in presets)
        {
            preset.CreateTime = DateTime.Now;
            preset.UpdateTime = DateTime.Now;
            db.Insertable(preset).ExecuteCommandAsync();
        }
    }
}
