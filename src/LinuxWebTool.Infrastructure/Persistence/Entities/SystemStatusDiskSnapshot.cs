
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 绯荤粺鐘舵€佸巻鍙茬鐩樻寕杞界偣蹇収锛堟瘡娆￠噰鏍疯褰曟瘡涓寕杞界偣鐨勪娇鐢ㄦ儏鍐碉級銆?/// 涓?SystemStatusProcessSnapshot / SystemStatusNetSnapshot 鍏辩敤鍚屼竴閲囨牱鏃堕棿鎴筹紙Time锛夈€?/// </summary>
public class SystemStatusDiskSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>鍏辩敤閲囨牱鏃堕棿锛堜笌鏁存満 / 杩涚▼ / 缃戠粶蹇収鍚屽埢锛夈€?/summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>鎸傝浇鐐广€?/summary>
    public string Mount { get; set; } = string.Empty;

    public string FileSystem { get; set; } = string.Empty;

    /// <summary>浣跨敤鐜囷紙0~100锛夈€?/summary>
    public double UsagePercent { get; set; }

    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
}

