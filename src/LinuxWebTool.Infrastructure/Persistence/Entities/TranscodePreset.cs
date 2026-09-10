
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>杞爜棰勮锛歠fmpeg 鍙傛暟鐨勫０鏄庡紡缁勫悎锛涜繘闃跺弬鏁拌蛋 ExtraArgs銆?/summary>
public class TranscodePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>鐩爣瀹瑰櫒 / 杈撳嚭鎵╁睍鍚嶏細mp4銆乵kv銆乵p3鈥?/summary>
    public string Container { get; set; } = "mp4";

    /// <summary>瑙嗛缂栬В鐮侊細libx264 / libx265 / copy锛涚┖ = 鍘昏棰戙€?/summary>
    public string? VideoCodec { get; set; }

    /// <summary>瑙嗛璐ㄩ噺 CRF锛?~51锛夛紱copy / 鍘昏棰戞椂蹇界暐銆?/summary>
    public int? VideoQuality { get; set; }

    /// <summary>闊抽缂栬В鐮侊細aac / libmp3lame / copy锛涚┖ = 鍘婚煶棰戙€?/summary>
    public string? AudioCodec { get; set; }

    /// <summary>闊抽鐮佺巼锛屽 128k銆?/summary>
    public string? AudioBitrate { get; set; }

    /// <summary>棰濆 ffmpeg 鍙傛暟锛堝師鏍烽檮鍔犲湪杈撳嚭鏂囦欢涔嬪墠锛夈€?/summary>
    public string? ExtraArgs { get; set; }

    public string? Description { get; set; }

    /// <summary>鍐呯疆棰勮锛堥娆″惎鍔ㄦ挱绉嶏紱鍙紪杈戝彲鍒犻櫎锛夈€?/summary>
    public bool IsBuiltin { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

