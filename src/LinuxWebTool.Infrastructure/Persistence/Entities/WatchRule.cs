
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>鏂囦欢澶圭洃鍚鍒欙細鍙戠幇鏂板 / 鍙樻洿鐨勫尮閰嶆枃浠跺悗鑷姩鍏ラ槦杞爜銆?/summary>
public class WatchRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>鐩戝惉鐩綍锛堝彲涓?SMB 鎸傝浇璺緞锛夈€?/summary>
    public string WatchPath { get; set; } = string.Empty;

    /// <summary>鎵╁睍鍚嶈繃婊わ紙閫楀彿鍒嗛殧鍚偣锛夛紱绌?= 鍐呯疆濯掍綋鎵╁睍鍚嶅叏闆嗐€?/summary>
    public string? FilePatterns { get; set; }

    public Guid PresetId { get; set; }

    /// <summary>杈撳嚭妯″紡锛?=鏇挎崲 1=骞跺瓨銆?/summary>
    public int OutputMode { get; set; }

    public bool Recursive { get; set; }

    /// <summary>鎵弿鏂瑰紡锛?=杞 1=鏂囦欢绯荤粺浜嬩欢銆?/summary>
    public int Mode { get; set; }

    /// <summary>杞闂撮殧绉掓暟锛堟渶灏?30锛夈€?/summary>
    public int PollSeconds { get; set; } = 300;

    public bool Enabled { get; set; } = true;

    /// <summary>鏄惁浣跨敤纭欢鍔犻€燂紙榛樿寮€鍚紱杩愯鏃舵帰娴嬩笉鏀寔鍒欏洖閫€杞欢缂栫爜锛夈€?/summary>
    public bool UseHardwareAccel { get; set; } = true;

    /// <summary>鐢ㄦ埛鎸囧畾鐨勭‖浠跺悗绔細auto / nvenc / qsv / vaapi / v4l2m2m锛沘uto = 鎸夌‖浠朵俊鍙疯嚜鍔ㄦ帓浼樸€?/summary>
    public string? HardwareBackend { get; set; } = "auto";

    public DateTime? LastScanTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

