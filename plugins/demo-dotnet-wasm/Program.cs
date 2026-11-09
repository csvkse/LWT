using System;
using System.Text.Json;

namespace DemoDotNetWasm;

/// <summary>
/// .NET 10 WASI (WebAssembly System Interface) 极速微函数示例。
/// 无需宿主提供完整重型运行时或常驻进程，以纯 Wasm 沙箱字节码瞬时装载执行。
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var action = Environment.GetEnvironmentVariable("LWT_ACTION") ?? (args.Length > 0 ? args[0] : "run");
        var param = Environment.GetEnvironmentVariable("LWT_PARAM") ?? (args.Length > 1 ? args[1] : "");

        object resultData = action.ToLowerInvariant() switch
        {
            "calc" => int.TryParse(param, out var n) ? n * 42 : 42,
            "echo" => $"Echo from C# WASI: {param}",
            "benchmark" => ExecuteBenchmark(),
            _ => "Hello from .NET 10 C# WebAssembly Micro-Function!"
        };

        var response = new
        {
            status = "ok",
            engine = ".NET 10 WASI (WebAssembly)",
            action = action,
            param = param,
            result = resultData,
            timestamp = DateTime.UtcNow.ToString("O")
        };

        Console.WriteLine(JsonSerializer.Serialize(response));
    }

    private static object ExecuteBenchmark()
    {
        long sum = 0;
        for (var i = 1; i <= 10000; i++)
        {
            sum += i;
        }
        return new { iterations = 10000, sum = sum };
    }
}
