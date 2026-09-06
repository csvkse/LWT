namespace LinuxWebTool.WebHost.Composition;

/// <summary>应用启动编排入口（Program 仅调用此处）。</summary>
public static class LinuxWebToolApplication
{
    public static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddApplicationServices();

        var app = builder.Build();
        app.UseApplicationPipeline();

        await app.RunAsync();
    }
}
