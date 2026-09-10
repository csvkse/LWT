
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>涓€娆¤浆鐮佷换鍔★紙鏂囦欢绮掑害锛夈€傛墽琛岀敱 TranscodeQueueService 鍚庡彴椹卞姩锛岃繘搴﹁妭娴佸洖鍐欍€?/summary>
public class TranscodeJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourcePath { get; set; } = string.Empty;

    /// <summary>杈撳嚭鐩爣璺緞锛堣繍琛屾椂瑙勫垝鍚庡洖鍐欙紱鏇挎崲妯″紡涓烘渶缁堣矾寰勶紝瀹為檯鍏堝啓 .lwt-tmp 涓存椂鏂囦欢锛夈€?/summary>
    public string? OutputPath { get; set; }

    public Guid? PresetId { get; set; }

    public string? PresetName { get; set; }

    /// <summary>鑷畾涔?ffmpeg 鍙傛暟锛堜笌棰勮浜掓枼浼樺厛锛夈€?/summary>
    public string? CustomArgs { get; set; }

    /// <summary>鏄惁涓哄畬鏁村懡浠ゆā寮忥細true=CustomArgs 鏄畬鏁?ffmpeg 鍛戒护锛堝惈 -i/杈撳嚭璺緞锛夛紝绯荤粺涓嶆敞鍏?-progress/杈撳嚭瑙勫垝/纭欢涓婁笅鏂囥€?/summary>
    public bool IsFullCommand { get; set; }

    /// <summary>鏄惁璇锋眰浣跨敤纭欢鍔犻€燂紙鎻愪氦鏃舵爣璁帮紝榛樿寮€鍚級銆?/summary>
    public bool UseHardwareAccel { get; set; } = true;

    /// <summary>鐢ㄦ埛鎸囧畾鐨勭‖浠跺悗绔細auto / nvenc / qsv / vaapi / v4l2m2m锛沘uto = 鎸夌‖浠朵俊鍙疯嚜鍔ㄦ帓浼樸€?/summary>
    public string? HardwareBackend { get; set; } = "auto";

    /// <summary>瀹為檯鏄惁浣跨敤浜嗙‖浠剁紪鐮佸櫒锛堣繍琛屾椂鍒ゅ畾鍥炲啓锛沠alse=鍥為€€杞欢缂栫爜锛夈€?/summary>
    public bool UsedHardwareAccel { get; set; }

    /// <summary>瀹為檯鎵ц鐨勫畬鏁?ffmpeg 鍛戒护琛岋紙杩愯鏃惰褰曪紝渚涗换鍔￠槦鍒楀洖鏄撅級銆?/summary>
    public string? CommandLine { get; set; }

    /// <summary>鍥為€€鍘熷洜锛堣姹傜‖浠跺姞閫熶絾瀹為檯鐢ㄨ蒋浠剁紪鐮佹椂鍐欐瀛楁锛屽"棰勮涓鸿蒋浠剁紪鐮佸櫒"/"纭欢缂栫爜鍣ㄤ笉鍙敤锛屽凡鍥為€€杞欢缂栫爜"锛夈€?/summary>
    public string? FallbackReason { get; set; }

    /// <summary>鍥為€€鍓嶆湰搴旀墽琛岀殑纭欢鍔犻€熷懡浠わ紙褰撻璁炬寚瀹氱‖浠剁紪鐮佸櫒浣嗗洜鐜涓嶆敮鎸佸洖閫€鏃惰褰曪紝渚涗换鍔￠槦鍒楀姣旓級銆?/summary>
    public string? FallbackFromCommand { get; set; }

    /// <summary>杈撳嚭鐩綍锛涗负绌?= 杈撳嚭鍒版簮鏂囦欢鎵€鍦ㄧ洰褰曘€?/summary>
    public string? OutputDir { get; set; }

    /// <summary>杈撳嚭瀹瑰櫒 / 鎵╁睍鍚嶏紙鑷畾涔夊弬鏁版ā寮忓湪鎻愪氦鏃舵寚瀹氾紱棰勮妯″紡杩愯鏃朵粠棰勮璇诲彇锛夈€?/summary>
    public string? OutputContainer { get; set; }

    /// <summary>杈撳嚭妯″紡锛?=鏇挎崲 1=骞跺瓨銆?/summary>
    public int OutputMode { get; set; }

    /// <summary>瑙﹀彂鏉ユ簮锛?=鎵嬪姩 1=鐩戝惉瑙勫垯銆?/summary>
    public int Trigger { get; set; }

    public Guid? WatchRuleId { get; set; }

    /// <summary>0=鎺掗槦 1=杞爜涓?2=鎴愬姛 3=澶辫触 4=鍙栨秷 5=涓柇銆?/summary>
    public int Status { get; set; }

    /// <summary>杩涘害 0~100锛坒fprobe 鏃堕暱宸茬煡鏃舵寜杈撳嚭鏃堕棿鎺ㄨ繘锛夈€?/summary>
    public double Progress { get; set; }

    /// <summary>瀹炴椂閫熷害鏂囨湰锛屽 "x1.53"銆?/summary>
    public string? SpeedText { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>澶辫触 / 涓柇鍘熷洜鎽樿锛坒fmpeg 鏃ュ織灏鹃儴锛夈€?/summary>
    public string? ErrorOutput { get; set; }

    /// <summary>ffmpeg 瀹屾暣鏃ュ織鏂囦欢璺緞锛坉ata/logs/transcode/&lt;jobId&gt;.log锛夈€?/summary>
    public string? LogFile { get; set; }

    public long? SourceSizeBytes { get; set; }

    public long? OutputSizeBytes { get; set; }

    public DateTime QueueTime { get; set; } = DateTime.Now;

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

