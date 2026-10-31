namespace LinuxWebTool.Infrastructure.Features.Workers.Supervisor;

/// <summary>
/// 固定容量线程安全环形日志缓冲区（内存恒定受控，防日志打爆）。
/// </summary>
public sealed class WorkerProcessRingBuffer
{
    private readonly string[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public WorkerProcessRingBuffer(int capacity = 1000)
    {
        _capacity = Math.Max(10, capacity);
        _buffer = new string[_capacity];
    }

    /// <summary>
    /// 追加一行日志（包含时间戳）。
    /// </summary>
    public void AppendLine(string line)
    {
        lock (_lock)
        {
            var formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
            _buffer[_head] = formatted;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// 获取最新的 N 行日志。
    /// </summary>
    public IReadOnlyList<string> GetRecentLines(int limit = 200)
    {
        lock (_lock)
        {
            var take = Math.Min(_count, Math.Max(1, limit));
            var result = new List<string>(take);
            var startIndex = (_head - take + _capacity) % _capacity;

            for (var i = 0; i < take; i++)
            {
                var index = (startIndex + i) % _capacity;
                result.Add(_buffer[index]);
            }

            return result;
        }
    }

    /// <summary>
    /// 清空缓冲区。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
            Array.Clear(_buffer);
        }
    }
}
