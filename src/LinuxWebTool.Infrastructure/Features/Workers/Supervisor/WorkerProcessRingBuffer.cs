namespace LinuxWebTool.Infrastructure.Features.Workers.Supervisor;

/// <summary>
/// 固定容量线程安全环形日志缓冲区（行数与内存双重受控，防日志打爆内存）。
/// 单行上限 4 KiB，实例总内存上限 1 MiB。
/// </summary>
public sealed class WorkerProcessRingBuffer
{
    private const int MaxLineLength = 4096;
    private const int MaxTotalBytes = 1024 * 1024; // 1 MiB

    private readonly string[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private int _currentTotalBytes;
    private readonly object _lock = new();

    public WorkerProcessRingBuffer(int capacity = 1000)
    {
        _capacity = Math.Max(10, capacity);
        _buffer = new string[_capacity];
    }

    /// <summary>
    /// 追加一行日志（包含时间戳）。单行与总内存均受界限保护。
    /// </summary>
    public void AppendLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (line.Length > MaxLineLength)
        {
            line = string.Concat(line.AsSpan(0, MaxLineLength), "... [TRUNCATED]");
        }

        var formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
        var lineBytes = formatted.Length * sizeof(char);

        lock (_lock)
        {
            // 若当前槽位已有旧行，先减去旧行字节
            if (_count == _capacity)
            {
                var oldLine = _buffer[_head];
                if (oldLine != null)
                {
                    _currentTotalBytes -= oldLine.Length * sizeof(char);
                }
            }

            _buffer[_head] = formatted;
            _currentTotalBytes += lineBytes;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }

            // 循环淘汰最旧行直至总内存回到 1 MiB 上限以内
            while (_currentTotalBytes > MaxTotalBytes && _count > 1)
            {
                var oldestIndex = (_head - _count + _capacity) % _capacity;
                var evicted = _buffer[oldestIndex];
                if (evicted != null)
                {
                    _currentTotalBytes -= evicted.Length * sizeof(char);
                    _buffer[oldestIndex] = string.Empty;
                }
                _count--;
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
                var item = _buffer[index];
                if (!string.IsNullOrEmpty(item))
                {
                    result.Add(item);
                }
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
            _currentTotalBytes = 0;
            Array.Clear(_buffer);
        }
    }
}
