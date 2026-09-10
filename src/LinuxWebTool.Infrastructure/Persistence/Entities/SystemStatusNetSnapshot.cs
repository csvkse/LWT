
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 绯荤粺鐘舵€佸巻鍙茬綉鍗″揩鐓э紙姣忔閲囨牱璁板綍姣忎釜缃戝崱鐨勬敹鍙戦€熺巼锛夈€?/// 涓?SystemStatusProcessSnapshot / SystemStatusDiskSnapshot 鍏辩敤鍚屼竴閲囨牱鏃堕棿鎴筹紙Time锛夈€?/// </summary>
public class SystemStatusNetSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>鍏辩敤閲囨牱鏃堕棿锛堜笌鏁存満 / 杩涚▼ / 纾佺洏蹇収鍚屽埢锛夈€?/summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>缃戝崱鍚嶃€?/summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>鍙戦€侀€熺巼锛堝瓧鑺?绉掞級銆?/summary>
    public long SentBytesPerSec { get; set; }

    /// <summary>鎺ユ敹閫熺巼锛堝瓧鑺?绉掞級銆?/summary>
    public long RecvBytesPerSec { get; set; }
}

