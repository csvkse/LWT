using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    private void EnsureDefaultWorkersCreated()
    {
        try
        {
            Directory.CreateDirectory(_workersRoot);
            EnsureDemoJsCreated();
            EnsureDemoPyCreated();
            EnsureDemoDotNetCreated();
            EnsureDemoShCreated();
            EnsureDemoPsCreated();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化官方示例 Worker 发生异常");
        }
    }

    private void EnsureDemoJsCreated()
    {
        var id = "demo-js-webhook";
        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new WorkerManifest
        {
            Id = id,
            Name = "Node.js Webhook 服务",
            Version = "1.0.0",
            Description = "基于原生 Node.js 的轻量 Webhook 接口服务，毫秒级响应并返回 JSON 数据。",
            Author = "Antigravity",
            Mode = WorkerServiceMode.Service,
            Runtime = WorkerRuntimeType.Node,
            Entrypoint = "worker.js",
            Network = new WorkerNetworkConfig { RoutingMode = "path", PathPrefix = $"/w/{id}" },
            Storage = new WorkerStorageConfig { EnableSqlite = true, EnableKv = true },
            Supervision = new WorkerSupervisionConfig { AutoRestart = true }
        };
        File.WriteAllText(Path.Combine(dir, "worker.json"), JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest));

        var script = @"const http = require('http');
const port = process.env.PORT || 8080;
const workerId = process.env.LWT_WORKER_ID || 'demo-js';

const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify({
    code: 0,
    message: 'Hello from Node.js Worker!',
    workerId: workerId,
    path: req.url,
    timestamp: new Date().toISOString()
  }));
});

server.listen(port, '0.0.0.0', () => {
  console.log(`[Node-Worker] Ready and listening on port ${port}`);
});
";
        File.WriteAllText(Path.Combine(dir, "worker.js"), script);
    }

    private void EnsureDemoPyCreated()
    {
        var id = "demo-py-api";
        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new WorkerManifest
        {
            Id = id,
            Name = "Python 数据分析 API",
            Version = "1.0.0",
            Description = "基于 Python 3 原生 http.server 的微服务，自动接入独立 SQLite 记录访问日志。",
            Author = "Antigravity",
            Mode = WorkerServiceMode.Service,
            Runtime = WorkerRuntimeType.Python,
            Entrypoint = "worker.py",
            Network = new WorkerNetworkConfig { RoutingMode = "path", PathPrefix = $"/w/{id}" },
            Storage = new WorkerStorageConfig { EnableSqlite = true, EnableKv = true },
            Supervision = new WorkerSupervisionConfig { AutoRestart = true }
        };
        File.WriteAllText(Path.Combine(dir, "worker.json"), JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest));

        var script = @"import os, sqlite3, json
from http.server import HTTPServer, BaseHTTPRequestHandler

PORT = int(os.environ.get('PORT', 8080))
DB_PATH = os.environ.get('LWT_SQLITE_PATH', 'data.db')

try:
    with sqlite3.connect(DB_PATH) as conn:
        conn.execute('CREATE TABLE IF NOT EXISTS access_logs (id INTEGER PRIMARY KEY AUTOINCREMENT, ts DATETIME DEFAULT CURRENT_TIMESTAMP)')
        conn.commit()
except Exception as e:
    print(f'[WARN] Init DB error: {e}', flush=True)

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        count = 0
        try:
            with sqlite3.connect(DB_PATH) as conn:
                conn.execute('INSERT INTO access_logs DEFAULT VALUES')
                count = conn.execute('SELECT count(*) FROM access_logs').fetchone()[0]
        except Exception:
            pass

        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.end_headers()
        self.wfile.write(json.dumps({
            'message': 'Hello from Python Worker!',
            'total_visits': count,
            'db': DB_PATH
        }).encode('utf-8'))

if __name__ == '__main__':
    server = HTTPServer(('0.0.0.0', PORT), Handler)
    print(f'[*] Python Worker started on port {PORT}, DB: {DB_PATH}', flush=True)
    server.serve_forever()
";
        File.WriteAllText(Path.Combine(dir, "worker.py"), script);
    }

    private void EnsureDemoDotNetCreated()
    {
        var id = "demo-dotnet-service";
        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new WorkerManifest
        {
            Id = id,
            Name = ".NET Core C# 极简服务",
            Version = "1.0.0",
            Description = "基于 .NET 顶层语句单文件极速启动的微服务，支持超高吞吐接口响应。",
            Author = "Antigravity",
            Mode = WorkerServiceMode.Service,
            Runtime = WorkerRuntimeType.DotNet,
            Entrypoint = "Program.cs",
            Network = new WorkerNetworkConfig { RoutingMode = "path", PathPrefix = $"/w/{id}" },
            Storage = new WorkerStorageConfig { EnableSqlite = true, EnableKv = true },
            Supervision = new WorkerSupervisionConfig { AutoRestart = true }
        };
        File.WriteAllText(Path.Combine(dir, "worker.json"), JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest));

        var script = @"using System;
using System.IO;
using System.Net;
using System.Text;

var port = Environment.GetEnvironmentVariable(""PORT"") ?? ""8080"";
var listener = new HttpListener();
listener.Prefixes.Add($""http://*:{port}/"");
listener.Start();
Console.WriteLine($""[.NET-Worker] Listening on http://0.0.0.0:{port}/"");

while (true)
{
    var context = await listener.GetContextAsync();
    var response = context.Response;
    var json = ""{\""message\"":\""Hello from .NET Core C# Top-Level Worker!\"",\""timestamp\"":\"""" + DateTime.UtcNow.ToString(""o"") + ""\""}"";
    var buffer = Encoding.UTF8.GetBytes(json);
    response.ContentType = ""application/json; charset=utf-8"";
    response.ContentLength64 = buffer.Length;
    await response.OutputStream.WriteAsync(buffer);
    response.OutputStream.Close();
}
";
        File.WriteAllText(Path.Combine(dir, "Program.cs"), script);
    }

    private void EnsureDemoShCreated()
    {
        var id = "demo-sh-daemon";
        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new WorkerManifest
        {
            Id = id,
            Name = "Linux 巡检长时守护",
            Version = "1.0.0",
            Description = "无请求后台常驻守护进程，每 30 秒巡检一次磁盘与内存并在控制台输出指标。",
            Author = "Antigravity",
            Mode = WorkerServiceMode.Daemon,
            Runtime = WorkerRuntimeType.Bash,
            Entrypoint = "worker.sh",
            Network = new WorkerNetworkConfig { RoutingMode = "path" },
            Storage = new WorkerStorageConfig { EnableSqlite = true, EnableKv = false },
            Supervision = new WorkerSupervisionConfig { AutoRestart = true }
        };
        File.WriteAllText(Path.Combine(dir, "worker.json"), JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest));

        var script = @"#!/usr/bin/env bash
echo ""[Shell-Daemon] Worker started. Data dir: ${LWT_DATA_DIR}""
trap 'echo ""[Shell-Daemon] Received SIGTERM, exiting gracefully...""; exit 0' TERM INT

while true; do
    DATE=$(date '+%Y-%m-%d %H:%M:%S')
    echo ""[${DATE}] Shell Daemon Heartbeat: Worker ${LWT_WORKER_ID} is alive.""
    sleep 30
done
";
        File.WriteAllText(Path.Combine(dir, "worker.sh"), script);
    }

    private void EnsureDemoPsCreated()
    {
        var id = "demo-ps-daemon";
        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new WorkerManifest
        {
            Id = id,
            Name = "PowerShell 任务调度守护",
            Version = "1.0.0",
            Description = "跨平台 PowerShell 后台守护示例，定时执行心跳与本地队列消费。",
            Author = "Antigravity",
            Mode = WorkerServiceMode.Daemon,
            Runtime = WorkerRuntimeType.PowerShell,
            Entrypoint = "worker.ps1",
            Network = new WorkerNetworkConfig { RoutingMode = "path" },
            Storage = new WorkerStorageConfig { EnableSqlite = false, EnableKv = true },
            Supervision = new WorkerSupervisionConfig { AutoRestart = true }
        };
        File.WriteAllText(Path.Combine(dir, "worker.json"), JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest));

        var script = @"Write-Output ""[PowerShell-Daemon] Service initialized on PID $PID""
$counter = 0

while ($true) {
    $counter++
    $time = Get-Date -Format ""yyyy-MM-dd HH:mm:ss""
    Write-Output ""[$time] Heartbeat tick #$counter - Worker: $env:LWT_WORKER_ID""
    Start-Sleep -Seconds 25
}
";
        File.WriteAllText(Path.Combine(dir, "worker.ps1"), script);
    }
}
