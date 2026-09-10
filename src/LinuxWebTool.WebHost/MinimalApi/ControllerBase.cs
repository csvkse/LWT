using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using LinuxWebTool.WebHost.Routes;
using LinuxWebTool.WebHost.Composition;

namespace LinuxWebTool.WebHost.MinimalApi;

public abstract class ControllerBase
{
    public HttpContext HttpContext { get; set; } = null!;
    public HttpRequest Request => HttpContext.Request;
    public HttpResponse Response => HttpContext.Response;
    public ClaimsPrincipal User => HttpContext.User;
    protected IResult Ok() => Results.Ok();
    protected IResult Ok(object? value) => Results.Ok(value);
    protected IResult BadRequest() => Results.BadRequest();
    protected IResult BadRequest(object? error) => Results.BadRequest(error);
    protected IResult NotFound() => Results.NotFound();
    protected IResult NotFound(object? value) => Results.NotFound(value);
    protected IResult StatusCode(int statusCode) => Results.StatusCode(statusCode);
    protected IResult StatusCode(int statusCode, MessageResponse value) => new MessageJsonResult(value, statusCode);
    protected IResult File(byte[] fileContents, string contentType, string? fileDownloadName = null) => Results.File(fileContents, contentType, fileDownloadName);
    protected IResult File(Stream fileStream, string contentType, string? fileDownloadName = null) => Results.File(fileStream, contentType, fileDownloadName);
    protected IResult PhysicalFile(string physicalPath, string contentType, string? fileDownloadName = null) => Results.File(physicalPath, contentType, fileDownloadName);
    protected IResult Unauthorized() => Results.Unauthorized();
    protected IResult Unauthorized(MessageResponse value) => new MessageJsonResult(value, 401);
    protected IResult Forbid() => Results.Forbid();
    private sealed class MessageJsonResult(MessageResponse value, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, value, AppJsonSerializerContext.Default.MessageResponse, httpContext.RequestAborted);
        }
    }
}
