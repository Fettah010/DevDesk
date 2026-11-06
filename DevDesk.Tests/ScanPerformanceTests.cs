using System.Diagnostics;
using DevDesk.Services;
using Xunit;
using Xunit.Abstractions;

namespace DevDesk.Tests;

[CollectionDefinition("Real ecosystem performance", DisableParallelization = true)]
public sealed class RealEcosystemPerformanceCollection
{
}

[Collection("Real ecosystem performance")]
public sealed class ScanPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public ScanPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Scan_RealEcosystem_ReportsDuration()
    {
        if (!Directory.Exists(ScanEngine.DefaultScanRoot))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await new ScanEngine().ScanAsync();
        stopwatch.Stop();

        _output.WriteLine(
            $"real scan: {stopwatch.ElapsedMilliseconds} ms for {result.Projects.Count} project(s)");
        _output.WriteLine("  " + string.Join(", ", result.Projects.Select(project => project.Name)));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"real scan took {stopwatch.ElapsedMilliseconds} ms");
    }
}
