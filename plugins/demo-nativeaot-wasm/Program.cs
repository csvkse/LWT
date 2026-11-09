using System;

namespace DemoNativeAotWasm;

/// <summary>
/// C# NativeAOT-LLVM 极速微函数示例。
/// 彻底去除 Mono 解释器与 IL 程序集依赖，经由 LLVM 后端优化直接生成纯静态单一 .wasm 模块。
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var action = Environment.GetEnvironmentVariable("LWT_ACTION") ?? (args.Length > 0 ? args[0] : "run");
        var param = Environment.GetEnvironmentVariable("LWT_PARAM") ?? (args.Length > 1 ? args[1] : "");

        var resultJson = action.ToLowerInvariant() switch
        {
            "fib" => ExecuteFibonacci(param),
            "calc" => ExecuteCalc(param),
            "echo" => $"{{\"action\":\"echo\",\"param\":\"{Escape(param)}\"}}",
            _ => $"{{\"action\":\"info\",\"engine\":\"C# NativeAOT-LLVM\",\"status\":\"ready\"}}"
        };

        var output = $"{{\"status\":\"ok\",\"engine\":\"NativeAOT-LLVM\",\"action\":\"{action}\",\"result\":{resultJson},\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
        Console.WriteLine(output);
    }

    private static string ExecuteFibonacci(string param)
    {
        var n = int.TryParse(param, out var parsed) && parsed > 0 ? Math.Min(parsed, 45) : 10;
        long a = 0, b = 1;
        for (var i = 0; i < n; i++)
        {
            var temp = a + b;
            a = b;
            b = temp;
        }
        return $"{{\"n\":{n},\"fib\":{a}}}";
    }

    private static string ExecuteCalc(string param)
    {
        var val = int.TryParse(param, out var n) ? n : 42;
        var square = (long)val * val;
        return $"{{\"input\":{val},\"square\":{square}}}";
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
