namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件控制台日志无锁环形缓冲区。
/// </summary>
public sealed class PluginProcessRingBuffer
{
    private readonly string[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public PluginProcessRingBuffer(int capacity = 1000)
    {
        _capacity = capacity > 0 ? capacity : 1000;
        _buffer = new string[_capacity];
    }

    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_lock)
        {
            var index = (_head + _count) % _capacity;
            if (_count == _capacity)
            {
                _head = (_head + 1) % _capacity;
            }
            else
            {
                _count++;
            }
            _buffer[index] = line;
        }
    }

    public IReadOnlyList<string> GetRecentLines(int limit = 200)
    {
        lock (_lock)
        {
            var take = Math.Min(limit, _count);
            var result = new List<string>(take);
            var start = (_head + _count - take) % _capacity;
            for (var i = 0; i < take; i++)
            {
                var idx = (start + i) % _capacity;
                result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }
}
