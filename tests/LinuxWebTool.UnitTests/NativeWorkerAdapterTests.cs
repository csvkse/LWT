using System.Security;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Workers.Adapters;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class NativeWorkerAdapterTests
{
    [Fact]
    public void CheckAvailable_NativeAdapter_AlwaysReturnsTrueWithoutCompiler()
    {
        var adapter = new NativeRuntimeAdapter();
        var available = adapter.CheckAvailable(out var missingReason, out var missingRunners);

        Assert.True(available);
        Assert.Null(missingReason);
        Assert.Empty(missingRunners);
        Assert.Equal(WorkerRuntimeType.Native, adapter.RuntimeType);
    }

    [Fact]
    public void BuildStartSpec_ValidPackageExecutable_BuildsSpecWithArgumentList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_native_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var binPath = Path.Combine(tempDir, "mytool.exe");
            File.WriteAllText(binPath, "dummy");

            var adapter = new NativeRuntimeAdapter();
            var manifest = new WorkerManifest
            {
                Id = "test-native",
                Name = "Test Native Tool",
                Runtime = WorkerRuntimeType.Native,
                Source = "package",
                ProgramRef = "mytool.exe",
                Entrypoint = "mytool.exe",
                Arguments = ["--serve", "--port=18888", "--data-dir=data"]
            };

            var spec = adapter.BuildStartSpec(manifest, tempDir, 18888);

            Assert.Equal(Path.GetFullPath(binPath), spec.FileName);
            Assert.Equal(tempDir, spec.WorkingDirectory);
            Assert.NotNull(spec.ArgumentList);
            Assert.Equal(3, spec.ArgumentList.Count);
            Assert.Equal("--serve", spec.ArgumentList[0]);
            Assert.Equal("--port=18888", spec.ArgumentList[1]);
            Assert.Equal("--data-dir=data", spec.ArgumentList[2]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildStartSpec_MissingPackageExecutable_ThrowsFileNotFoundException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_native_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var adapter = new NativeRuntimeAdapter();
            var manifest = new WorkerManifest
            {
                Id = "test-native",
                Name = "Missing Tool",
                Runtime = WorkerRuntimeType.Native,
                Source = "package",
                ProgramRef = "nonexistent.exe",
                Entrypoint = "nonexistent.exe"
            };

            Assert.Throws<FileNotFoundException>(() =>
                adapter.BuildStartSpec(manifest, tempDir, 18888));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildStartSpec_PathTraversalInProgramRef_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_native_trav_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var adapter = new NativeRuntimeAdapter();
            var manifest = new WorkerManifest
            {
                Id = "test-native",
                Name = "Malicious Tool",
                Runtime = WorkerRuntimeType.Native,
                Source = "package",
                ProgramRef = "../../windows/system32/calc.exe",
                Entrypoint = "calc.exe"
            };

            Assert.Throws<SecurityException>(() =>
                adapter.BuildStartSpec(manifest, tempDir, 18888));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
