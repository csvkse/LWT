
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>SMB 鎸傝浇閰嶇疆銆傛寕杞?/ 鍗歌浇 / 鐘舵€佹帰娴嬬敱 SmbMountService 鎵ц锛屽惎鍔ㄩ噸鎸傜敱 SmbMountStartupService 璐熻矗銆?/summary>
public class SmbMount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>鏈嶅姟鍣ㄥ叡浜湴鍧€锛屽綊涓€鍖栦负 //host/share銆?/summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>鏈湴鎸傝浇鐐癸紙Linux 缁濆璺緞锛夈€?/summary>
    public string LocalPath { get; set; } = string.Empty;

    public string? Username { get; set; }

    /// <summary>SMB 瀵嗙爜锛堟寕杞芥湰韬渶瑕佸師鏂囷紱鍚屾椂钀藉湴 data/mount-creds 鍑嵁鏂囦欢锛?00 鏉冮檺锛夈€?/summary>
    public string? Password { get; set; }

    public string? Domain { get; set; }

    /// <summary>闄勫姞鎸傝浇閫夐」锛堥€楀彿鍒嗛殧锛夈€?/summary>
    public string? Options { get; set; }

    public bool AutoMount { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

