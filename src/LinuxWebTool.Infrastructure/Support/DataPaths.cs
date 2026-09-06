using Microsoft.Extensions.Configuration;

namespace LinuxWebTool.Infrastructure.Support;

/// <summary>
/// 持久化数据根目录（SQLite、admin.json、jwt 密钥、logs 均聚合于此，便于整卷挂载/备份）。
/// 由配置 Data:Directory 指定（环境变量 Data__Directory），相对路径锚定应用根目录，默认 "data"。
/// </summary>
public sealed class DataPaths
{
    public const string SectionName = "Data";

    public string Root { get; }

    public DataPaths(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Data:Directory"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "data";
        }
        Root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(Root);
    }

    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    /// <summary>把相对路径锚定到数据目录（如日志目录 "logs" → &lt;data&gt;/logs）；绝对路径原样返回。</summary>
    public string Resolve(string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute) ? relativeOrAbsolute : Path.GetFullPath(Path.Combine(Root, relativeOrAbsolute));
}
