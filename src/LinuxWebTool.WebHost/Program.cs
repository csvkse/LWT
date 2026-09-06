namespace LinuxWebTool.WebHost;

public static class Program
{
    public static Task Main(string[] args) => Composition.LinuxWebToolApplication.RunAsync(args);
}
