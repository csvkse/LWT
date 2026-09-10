
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>绯荤粺鐘舵€佸巻鍙插揩鐓э紙鍚庡彴 60s 閲囨牱涓€鏉★紝淇濈暀 7 澶╋紝渚涘墠绔敾鏇茬嚎锛夈€?/summary>
public class SystemStatusSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>CPU 浣跨敤鐜囷紙0~100锛夈€?/summary>
    public double CpuUsage { get; set; }

    public double Load1 { get; set; }

    /// <summary>鍐呭瓨浣跨敤鐜囷紙0~100锛夈€?/summary>
    public double MemUsage { get; set; }

    /// <summary>鏍瑰垎鍖猴紙Windows 绯荤粺鐩橈級浣跨敤鐜囷紙0~100锛夈€?/summary>
    public double DiskRootUsage { get; set; }

    /// <summary>鍏ㄧ綉鍗″彂閫侀€熺巼锛堝瓧鑺?绉掞級銆?/summary>
    public long NetSentBps { get; set; }

    /// <summary>鍏ㄧ綉鍗℃帴鏀堕€熺巼锛堝瓧鑺?绉掞級銆?/summary>
    public long NetRecvBps { get; set; }
}

