using LinuxWebTool.ArchitectureTests.Support;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public class BaselineEngineTests
{
    [Fact]
    public void BaselineEngine_空扫描应显式失败防假绿()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            BaselineEngine.AssertNonEmptyScope(0, "测试空扫描范围");
        });
    }

    [Fact]
    public void BaselineEngine_未命中基线条目应断言失败以防僵尸残留()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"baseline_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, """
            {
              "entries": [
                {
                  "ruleId": "TEST-001",
                  "path": "src/dummy.cs",
                  "target": "DummySymbol",
                  "reason": "测试未命中债务"
                }
              ]
            }
            """);

            var engine = new BaselineEngine(tempFile);
            // 不调用 IsSuppressed 模拟该问题已被修复，基线未命中

            Assert.ThrowsAny<Exception>(() =>
            {
                engine.AssertNoStaleEntries();
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void BaselineEngine_命中基线条目应正常放行且不报Stale()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"baseline_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, """
            {
              "entries": [
                {
                  "ruleId": "TEST-002",
                  "path": "src/dummy.cs",
                  "target": "DummySymbol",
                  "reason": "测试已知债务"
                }
              ]
            }
            """);

            var engine = new BaselineEngine(tempFile);
            var isSuppressed = engine.IsSuppressed("TEST-002", "src/dummy.cs", "DummySymbol");
            Assert.True(isSuppressed);

            // 命中后，AssertNoStaleEntries 应该顺利通过
            engine.AssertNoStaleEntries();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
