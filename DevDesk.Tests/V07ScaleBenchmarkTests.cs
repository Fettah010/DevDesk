using System.Diagnostics;
using System.Threading;
using DevDesk.Models;
using DevDesk.Services;
using Xunit;
using Xunit.Abstractions;

namespace DevDesk.Tests;

[CollectionDefinition("v0.7 scale benchmarks", DisableParallelization = true)]
public sealed class V07ScaleBenchmarkCollection
{
}

[Collection("v0.7 scale benchmarks")]
public sealed class V07ScaleBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public V07ScaleBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "V07Benchmarks")]
    public async Task SyntheticFiftyRepositoryScanMeetsFirstCardAndFullScanBudgets()
    {
        using var sandbox = new GitSandbox();
        var root = Path.Combine(sandbox.Root, "fifty");
        Directory.CreateDirectory(root);
        await CreateRepositoriesAsync(root, 50);

        var fixtureDrive = new DriveInfo(Path.GetPathRoot(root)!);
        TimeSpan? defaultElapsed = null;
        TimeSpan? defaultFirstCard = null;
        foreach (var parallelism in new[] { 1, 2, 4, 8, 16, 24, 32, 50 })
        {
            long firstCardTimestamp = 0;
            var started = Stopwatch.GetTimestamp();
            var progress = new InlineProgress<ScanProgress>(update =>
            {
                if (update.Project is not null)
                {
                    Interlocked.CompareExchange(ref firstCardTimestamp, Stopwatch.GetTimestamp(), 0);
                }
            });
            var result = await new ScanEngine(
                selfPath: sandbox.PathOf("_no-self_"),
                maxParallelism: parallelism,
                rootsProvider: () => new[] { new ScanRootSettings { Path = root, Depth = 1 } })
                .ScanAsync(CancellationToken.None, progress);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var firstCard = firstCardTimestamp == 0
                ? (TimeSpan?)null
                : Stopwatch.GetElapsedTime(started, firstCardTimestamp);

            _output.WriteLine(
                $"M4 synthetic 50-repository scan at parallelism {parallelism}: {elapsed.TotalMilliseconds:0.0} ms; first real card: {firstCard?.TotalMilliseconds:0.0} ms; cards: {result.Projects.Count}.");
            Assert.Equal(50, result.Projects.Count);
            if (parallelism == Math.Min(4, Environment.ProcessorCount))
            {
                defaultElapsed = elapsed;
                defaultFirstCard = firstCard;
            }
        }

        _output.WriteLine(
            $"Environment: {Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs; fixture drive {fixtureDrive.Name} ({fixtureDrive.DriveType}).");
        Assert.True(defaultFirstCard <= TimeSpan.FromMilliseconds(300),
            $"First card arrived after {defaultFirstCard?.TotalMilliseconds:0.0} ms.");
        Assert.True(defaultElapsed < TimeSpan.FromSeconds(3),
            $"Fifty-repository scan took {defaultElapsed?.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    [Trait("Category", "V07Benchmarks")]
    public async Task SyntheticEightRepositoryScanMeetsOnePointFiveSecondBudget()
    {
        using var sandbox = new GitSandbox();
        var root = Path.Combine(sandbox.Root, "eight");
        Directory.CreateDirectory(root);
        await CreateRepositoriesAsync(root, 8);

        var started = Stopwatch.GetTimestamp();
        var result = await new ScanEngine(
            selfPath: sandbox.PathOf("_no-self_"),
            rootsProvider: () => new[] { new ScanRootSettings { Path = root, Depth = 1 } })
            .ScanAsync();
        var elapsed = Stopwatch.GetElapsedTime(started);
        _output.WriteLine($"M3 synthetic 8-repository scan: {elapsed.TotalMilliseconds:0.0} ms; cards: {result.Projects.Count}.");

        Assert.Equal(8, result.Projects.Count);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1500), $"Eight-repository scan took {elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    [Trait("Category", "V07Benchmarks")]
    public void WarmCacheForOneHundredCardsLoadsWithinSevenHundredMilliseconds()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "root");
        var projects = Enumerable.Range(0, 100)
            .Select(index => new ProjectModel
            {
                Name = $"Project {index:D3}",
                Path = Path.Combine(root, $"Project {index:D3}"),
                ScanRootPath = root,
                Branch = "main",
                Version = "1.0.0",
                HasRemote = false,
                RemoteOk = false,
                RemoteHost = "Local only",
            })
            .ToArray();
        var cache = new ScanCacheStore(new JsonStore(Path.Combine(sandbox.Directory, "scan-cache.json")));
        cache.Save(new CatalogSnapshot(projects, null, capturedAt: DateTimeOffset.UtcNow));
        var started = Stopwatch.GetTimestamp();

        var snapshot = cache.Load(new[] { new ScanRootSettings { Path = root } });
        var elapsed = Stopwatch.GetElapsedTime(started);
        _output.WriteLine($"M1 warm cache load: {elapsed.TotalMilliseconds:0.0} ms for {snapshot?.Projects.Count ?? 0} cards.");

        Assert.Equal(100, snapshot?.Projects.Count);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(700), $"Warm cache load took {elapsed.TotalMilliseconds:0.0} ms.");
    }

    private static async Task CreateRepositoriesAsync(string root, int count)
    {
        var initialization = Enumerable.Range(0, count).Select(async index =>
        {
            var path = Path.Combine(root, $"Repo-{index:D2}");
            Directory.CreateDirectory(path);
            var result = await GitRunner.RunAsync(path, "init", "--quiet");
            Assert.Equal(0, result.ExitCode);
            File.WriteAllText(
                Path.Combine(path, $"Repo-{index:D2}.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
        });
        await Task.WhenAll(initialization);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
