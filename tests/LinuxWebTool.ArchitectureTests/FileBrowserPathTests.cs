using LinuxWebTool.Infrastructure.Support;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class FileBrowserPathTests
{
    [Fact]
    public void Native_paths_preserve_roots_and_directory_boundaries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("C:/工作 space", FileBrowserPath.Normalize("C:\\工作 space\\"));
            Assert.Equal("C:/", FileBrowserPath.Parent("C:/工作 space"));
            Assert.Equal("/", FileBrowserPath.Parent("C:/"));
            Assert.True(FileBrowserPath.IsRoot("C:/"));
            Assert.True(FileBrowserPath.IsRoot("//server/share/"));
            Assert.Equal("//server/share/", FileBrowserPath.Parent("//server/share/folder"));
            Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("C:relative"));
            Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("C:/file:stream"));
            Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("//?/C:/folder"));
            Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("C:/data./file"));
            Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("C:/NUL.txt"));
            Assert.True(FileBrowserPath.IsWithin("C:/DATA/file", "c:/data"));
        }
        else
        {
            Assert.Equal("/tmp/目录", FileBrowserPath.Normalize("/tmp/./目录/"));
            Assert.Equal("/", FileBrowserPath.Parent("/tmp"));
        }
        Assert.False(FileBrowserPath.IsWithin("/data2/file", "/data"));
        Assert.Equal("file.txt", FileBrowserPath.Name(Path.Combine(Path.GetTempPath(), "file.txt")));
    }
}
