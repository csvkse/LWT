namespace LinuxWebTool.Infrastructure.Persistence.Entities;

public sealed class WebDavMount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool AutoMount { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
