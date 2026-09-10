using System.Text.Json.Serialization;
using LinuxWebTool.WebHost.Routes;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;

namespace LinuxWebTool.WebHost.Composition;

[JsonSerializable(typeof(AuthController))]
[JsonSerializable(typeof(ChangeCredentialResponse))]
[JsonSerializable(typeof(CommandGroup))]
[JsonSerializable(typeof(CommandItemResponse))]
[JsonSerializable(typeof(CommandsController))]
[JsonSerializable(typeof(CreateFileResponse))]
[JsonSerializable(typeof(DbConnectionFactory))]
[JsonSerializable(typeof(DeleteFileErrorResponse))]
[JsonSerializable(typeof(ExecutionRecord))]
[JsonSerializable(typeof(FileContentResponse))]
[JsonSerializable(typeof(FilesController))]
[JsonSerializable(typeof(GroupItemResponse))]
[JsonSerializable(typeof(GroupsController))]
[JsonSerializable(typeof(HistoryController))]
[JsonSerializable(typeof(IdResponse))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(LinuxCommand))]
[JsonSerializable(typeof(LogContentResponse))]
[JsonSerializable(typeof(LogsController))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(OperationLog))]
[JsonSerializable(typeof(OperationLogger))]
[JsonSerializable(typeof(OverviewController))]
[JsonSerializable(typeof(ReadFileErrorResponse))]
[JsonSerializable(typeof(RenameFileResponse))]
[JsonSerializable(typeof(RuleStatusResponse))]
[JsonSerializable(typeof(ScheduleStatusResponse))]
[JsonSerializable(typeof(ScheduleTask))]
[JsonSerializable(typeof(ScheduledCommandJob))]
[JsonSerializable(typeof(SchedulesController))]
[JsonSerializable(typeof(SmbMount))]
[JsonSerializable(typeof(SmbMountsController))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.CommandGroup>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.ExecutionRecord>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.LinuxCommand>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.OperationLog>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.ScheduleTask>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SmbMount>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusDiskSnapshot>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusNetSnapshot>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusProcessSnapshot>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.SystemStatusSnapshot>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.TranscodeJob>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.TranscodePreset>))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<LinuxWebTool.Infrastructure.Persistence.Entities.WatchRule>))]
[JsonSerializable(typeof(System.Collections.Generic.List<object>))]
[JsonSerializable(typeof(System.Collections.Generic.List<string>))]
[JsonSerializable(typeof(SystemStatusController))]
[JsonSerializable(typeof(SystemStatusDiskSnapshot))]
[JsonSerializable(typeof(SystemStatusNetSnapshot))]
[JsonSerializable(typeof(SystemStatusProcessSnapshot))]
[JsonSerializable(typeof(SystemStatusSnapshot))]
[JsonSerializable(typeof(TranscodeController))]
[JsonSerializable(typeof(TranscodeJob))]
[JsonSerializable(typeof(TranscodePreset))]
[JsonSerializable(typeof(UploadFileResponse))]
[JsonSerializable(typeof(UserInfoResponse))]
[JsonSerializable(typeof(WatchRule))]

[JsonSerializable(typeof(PagedResponse<LinuxCommand>))]
[JsonSerializable(typeof(PagedResponse<OperationLog>))]
[JsonSerializable(typeof(PagedResponse<TranscodeJobBrief>))]
[JsonSerializable(typeof(PresetImportItem))]
[JsonSerializable(typeof(IEnumerable<PresetImportItem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class AppJsonSerializerContext
 : JsonSerializerContext
{
}


