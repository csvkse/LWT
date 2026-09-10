
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 绯荤粺鐘舵€佸巻鍙茶繘绋嬪簭鍒楀揩鐓э紙姣忔閲囨牱璁板綍褰撳墠 Top 杩涚▼锛夈€?/// 涓?SystemStatusDiskSnapshot / SystemStatusNetSnapshot 鍏辩敤鍚屼竴閲囨牱鏃堕棿鎴筹紙Time锛夈€?/// </summary>
public class SystemStatusProcessSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>鍏辩敤閲囨牱鏃堕棿锛堜笌鏁存満 / 纾佺洏 / 缃戠粶蹇収鍚屽埢锛夈€?/summary>
    public DateTime Time { get; set; } = DateTime.Now;

    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>CPU 鍗犵敤锛?~100锛屽崟杩涚▼鐩稿鏁存満锛夈€?/summary>
    public double CpuPercent { get; set; }

    /// <summary>鍐呭瓨鍗犵敤锛?~100锛夈€?/summary>
    public double MemPercent { get; set; }

    public long MemBytes { get; set; }

    /// <summary>纾佺洏璇诲彇閫熺巼锛堝瓧鑺?绉掞紝鍩轰簬 /proc/&lt;pid&gt;/io read_bytes 宸€硷紱鏃犳硶閲囬泦涓?0锛夈€?/summary>
    public long DiskReadBps { get; set; }

    /// <summary>纾佺洏鍐欏叆閫熺巼锛堝瓧鑺?绉掞紝鍩轰簬 /proc/&lt;pid&gt;/io write_bytes 宸€硷紱鏃犳硶閲囬泦涓?0锛夈€?/summary>
    public long DiskWriteBps { get; set; }

    /// <summary>缃戠粶鍙戦€侀€熺巼锛堝瓧鑺?绉掞紝nethogs tracemode 閲囬泦锛涙湭瀹夎/鏃犵壒鏉冧负 0锛夈€?/summary>
    public long NetSentBps { get; set; }

    /// <summary>缃戠粶鎺ユ敹閫熺巼锛堝瓧鑺?绉掞紝nethogs tracemode 閲囬泦锛涙湭瀹夎/鏃犵壒鏉冧负 0锛夈€?/summary>
    public long NetRecvBps { get; set; }
}

