namespace LinuxWebTool.WebHost.Routes;

/// <summary>概览面板：数量统计、今日执行情况、最近执行/失败、即将执行的任务。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OverviewController(
    CommandStore commandStore,
    ScheduleStore scheduleStore,
    ExecutionStore executionStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var schedules = await scheduleStore.GetAllAsync();
        var recent = await executionStore.GetRecentAsync(8);
        var recentFailures = await executionStore.GetRecentFailuresAsync(5);

        return Ok(new OverviewResult
        {
            CommandCount = (await commandStore.GetAllAsync()).Count(),
            ScheduleCount = schedules.Count(),
            EnabledScheduleCount = schedules.Count(t => t.Enabled),
            TodayExecutions = await executionStore.CountTodayAsync(),
            TodayFailures = await executionStore.CountTodayFailuresAsync(),
            RecentExecutions = recent.Select(ToBrief).ToList(),
            RecentFailures = recentFailures.Select(ToBrief).ToList(),
            NextRuns = schedules
                .Where(t => t.Enabled && t.NextRunTime.HasValue)
                .OrderBy(t => t.NextRunTime)
                .Take(5)
                .Select(t => new ScheduleBrief
                {
                    Id = t.Id,
                    Name = t.Name,
                    CronExpression = t.CronExpression,
                    NextRunTime = t.NextRunTime,
                })
                .ToList(),
        });
    }

    private static ExecutionRecordBrief ToBrief(ExecutionRecord record) => new()
    {
        Id = record.Id,
        StartTime = record.StartTime,
        Source = (ExecutionSource)record.Source,
        CommandName = record.CommandName,
        CommandText = record.CommandText,
        Status = (ExecutionStatus)record.Status,
        ExitCode = record.ExitCode,
        DurationMs = record.DurationMs,
    };
}
