using System;
using System.Collections.Generic;

namespace LinuxWebTool.WebHost.Routes;

public record MessageResponse(string message);
public record IdResponse(Guid Id);
public record ScheduleStatusResponse(bool Enabled, DateTime? NextRunTime);
public record PagedResponse<T>(IEnumerable<T> items, int total);
public record ImportResponse(int imported, int skipped, List<string> messages);
public record RuleStatusResponse(bool Enabled, Guid Id);
public record UserInfoResponse(string userName);
public record FileContentResponse(string path, string name, long size, string content);
public record LogContentResponse(string name, int tail, string content);
public record ChangeCredentialResponse(string message, bool requireRelogin);
public record CommandItemResponse(Guid Id, string Name, string CommandText, int ScriptType, string? Description, Guid? GroupId, string? GroupName, bool IsPinned, int SortOrder, int TimeoutSeconds, DateTime? LastExecTime, DateTime CreateTime, DateTime UpdateTime);
public record GroupItemResponse(Guid Id, string Name, int SortOrder, DateTime CreateTime, int UsageCount);
public record ReadFileErrorResponse(string message, bool tooLarge, bool binary);
public record CreateFileResponse(string message, string path);
public record RenameFileResponse(string message, string path);
public record DeleteFileErrorResponse(string message, bool needRecursive);
public record UploadFileResponse(string message, string path);
public record TranscodeJobBrief(Guid Id, string SourcePath, string OutputPath, string PresetName, int OutputMode, int Trigger, Guid? WatchRuleId, int Status, double Progress, string SpeedText, long? DurationMs, string ErrorOutput, long? SourceSizeBytes, long? OutputSizeBytes, DateTime QueueTime, DateTime? StartTime, DateTime? EndTime, bool UseHardwareAccel, string HardwareBackend, bool UsedHardwareAccel, string CommandLine, string FallbackReason, string FallbackFromCommand, bool IsFullCommand);


public record GroupBrief(Guid Id, string Name, int SortOrder, DateTime CreateTime, int UsageCount);
