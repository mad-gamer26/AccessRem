using System.Diagnostics;

namespace AssistBridge.Tests;

/// <summary>The add-on's check for windows a portable NVDA cannot operate (run with plain Python).</summary>
public class ElevationTests
{
    [Fact]
    public void ReportsIntegrityLevelsOfOrdinaryAndSystemProcesses()
    {
        var module = Path.Combine(TestUtil.RepoRoot, "addon", "globalPlugins", "assistBridge");
        var lsass = Process.GetProcessesByName("lsass").First().Id;
        var explorer = Process.GetProcessesByName("explorer").FirstOrDefault()?.Id ?? Environment.ProcessId;
        var script = Path.Combine(TestUtil.TempDirectory("elevation"), "check.py");
        File.WriteAllText(script, $$"""
            import os, sys
            sys.path.insert(0, r"{{module}}")
            import elevation as e
            integrity, ui = e.ownPrivileges()
            print(integrity, ui)
            print(e.processIntegrity({{explorer}}))
            print(e.processIntegrity({{lsass}}))
            """);
        using var py = Process.Start(new ProcessStartInfo("python", $"-I \"{script}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = py.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        py.WaitForExit();
        Assert.True(py.ExitCode == 0, py.StandardError.ReadToEnd());
        Assert.Equal("8192 False", output[0]);   // Tests run at medium integrity without UI Access.
        Assert.Equal("8192", output[1]);         // Explorer: same level, so it can be operated.
        Assert.Equal("12288", output[2]);        // A system process: out of reach (reported as high).
    }
}
