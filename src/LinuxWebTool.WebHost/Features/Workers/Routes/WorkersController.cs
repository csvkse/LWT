using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Workers.Routes;

/// <summary>
/// SaaS 函数微服务与长时守护平台 REST 控制器。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WorkersController(IWorkersManager workersManager) : MinimalApi.ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetAll(CancellationToken ct)
    {
        var list = await workersManager.GetAllAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id}")]
    public async Task<IResult> GetById(string id, CancellationToken ct)
    {
        var item = await workersManager.GetByIdAsync(id, ct);
        return item != null ? Ok(item) : NotFound(new WorkerOperationResponse(false, $"未找到 Worker: {id}"));
    }

    [HttpPost("{id}/Start")]
    public async Task<IResult> Start(string id, CancellationToken ct)
    {
        var success = await workersManager.StartAsync(id, ct);
        return success ? Ok(new WorkerOperationResponse(true)) : BadRequest(new WorkerOperationResponse(false, $"启动 Worker '{id}' 失败，请检查日志。"));
    }

    [HttpPost("{id}/Stop")]
    public async Task<IResult> Stop(string id, CancellationToken ct)
    {
        var success = await workersManager.StopAsync(id, ct);
        return Ok(new WorkerOperationResponse(success));
    }

    [HttpPost("{id}/Restart")]
    public async Task<IResult> Restart(string id, CancellationToken ct)
    {
        var success = await workersManager.RestartAsync(id, ct);
        return success ? Ok(new WorkerOperationResponse(true)) : BadRequest(new WorkerOperationResponse(false, $"重启 Worker '{id}' 失败。"));
    }

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct)
    {
        var success = await workersManager.DeleteAsync(id, ct);
        return Ok(new WorkerOperationResponse(success));
    }

    [HttpPost]
    public async Task<IResult> CreateOrUpdate([FromBody] WorkerCreateOrUpdateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new WorkerOperationResponse(false, "Worker ID 和名称不能为空。"));
        }
        var item = await workersManager.CreateOrUpdateAsync(request, ct);
        return Ok(item);
    }

    [HttpGet("{id}/Logs")]
    public async Task<IResult> GetLogs(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var lines = await workersManager.GetLogsAsync(id, limit, ct);
        return Ok(lines);
    }

    [HttpGet("{id}/Storage/Sqlite")]
    public async Task<IResult> GetSqliteInfo(string id, CancellationToken ct)
    {
        var info = await workersManager.GetSqliteInfoAsync(id, ct);
        return Ok(info);
    }

    [HttpGet("{id}/Storage/Sqlite/Download")]
    public async Task<IResult> DownloadSqlite(string id, CancellationToken ct)
    {
        var info = await workersManager.GetSqliteInfoAsync(id, ct);
        if (!info.Exists || !System.IO.File.Exists(info.AbsolutePath))
        {
            return NotFound(new WorkerOperationResponse(false, "该 Worker 尚未生成独立 SQLite 数据库文件。"));
        }
        var bytes = await System.IO.File.ReadAllBytesAsync(info.AbsolutePath, ct);
        return Results.File(bytes, "application/x-sqlite3", $"{id}-data.db");
    }

    [HttpGet("{id}/Storage/Kv")]
    public async Task<IResult> GetKvList(string id, CancellationToken ct)
    {
        var list = await workersManager.GetKvListAsync(id, ct);
        return Ok(list);
    }

    [HttpPost("{id}/Storage/Kv")]
    public async Task<IResult> SetKv(string id, [FromBody] WorkerKvItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.Key)) return BadRequest(new WorkerOperationResponse(false, "Key 不能为空。"));
        var success = await workersManager.SetKvAsync(id, item, ct);
        return Ok(new WorkerOperationResponse(success));
    }

    [HttpDelete("{id}/Storage/Kv/{key}")]
    public async Task<IResult> DeleteKv(string id, string key, CancellationToken ct)
    {
        var success = await workersManager.DeleteKvAsync(id, key, ct);
        return Ok(new WorkerOperationResponse(success));
    }

    [HttpPost("Import/Zip")]
    [Consumes("multipart/form-data")]
    public async Task<IResult> ImportZip(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest(new WorkerOperationResponse(false, "请选择有效的 Zip 文件上传。"));
        using var stream = file.OpenReadStream();
        var item = await workersManager.ImportZipAsync(stream, ct);
        return Ok(item);
    }

    [HttpPost("Import/Repo/Scan")]
    public async Task<IResult> ScanRepo([FromBody] WorkerRepoScanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RepoUrl)) return BadRequest(new WorkerOperationResponse(false, "Git 仓库地址不能为空。"));
        var result = await workersManager.ScanRepoAsync(request.RepoUrl, ct);
        return Ok(result);
    }

    [HttpPost("Import/Repo/Apply")]
    public async Task<IResult> ApplyRepoModules([FromBody] WorkerRepoApplyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TempDirectory) || request.ModuleIds.Count == 0)
        {
            return BadRequest(new WorkerOperationResponse(false, "模块选择与临时扫描目录不能为空。"));
        }
        var count = await workersManager.ApplyRepoModulesAsync(request, ct);
        return Ok(new WorkerOperationResponse(true, Count: count));
    }
}

public sealed record WorkerRepoScanRequest
{
    public required string RepoUrl { get; init; }
}
