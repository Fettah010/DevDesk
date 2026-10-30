using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The updater used to reach the network through a lazily built manager behind a
/// synchronous wait; on the UI thread that is a deadlock waiting for a slow
/// GitHub. Reflection cannot see a method body, so this guard reads the source:
/// every blocking call must either be gone or carry an explicit
/// <c>block-exempt</c> justification on its line.
/// </summary>
public class UpdateServiceTests
{
    private static readonly Regex[] BlockingPatterns =
    {
        new(@"\.Wait\(", RegexOptions.Compiled),
        new(@"\.Result\b", RegexOptions.Compiled),
        new(@"GetAwaiter\(\)\s*\.\s*GetResult\(\)", RegexOptions.Compiled),
        new(@"\.GetResult\(", RegexOptions.Compiled),
    };

    private static readonly string[] ScannedDirectories = { "Services", "Pages", "ViewModels", "Models" };

    private static readonly string[] ScannedRootFiles = { "Program.cs", "App.xaml.cs", "MainWindow.xaml.cs" };

    private const string Exemption = "block-exempt";

    [Fact]
    public void UpdateService_DoesNotBlockSynchronously()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                if (line.Contains(Exemption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (BlockingPatterns.Any(pattern => pattern.IsMatch(line)))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoLayout.Root, file)}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Blocking calls on a Task in app code (annotate a deliberate one with 'block-exempt'):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public async Task UpdateService_CheckFromTheBuildOutputIsHonestAboutNotBeingInstalled()
    {
        // The test host is not a Velopack-installed app, so this is exactly what a
        // developer running from bin\ sees: a real answer, not a crash and not a
        // pretend success.
        var result = await UpdateService.Current.CheckAsync();

        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
        Assert.Equal(UpdateService.NotInstalledMessage, result.Message);
        Assert.False(result.HasUpdate);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task UpdateService_SetChannelPersistsThroughSettings()
    {
        using var sandbox = new SettingsSandbox();
        var settings = sandbox.NewService();
        AppServices.Initialize(new ProjectCatalog(new FakeScanEngine()), settings);

        var original = UpdateService.Current.Channel;

        try
        {
            await UpdateService.Current.SetChannelAsync(ReleaseChannels.Dev);

            Assert.Equal(ReleaseChannels.Dev, UpdateService.Current.Channel);
            Assert.Equal(ReleaseChannels.Dev, settings.Channel);
            Assert.Equal("dev", sandbox.ReadJson()["channel"]!.GetValue<string>());

            // An unknown channel is refused by name, persisted as the default.
            await UpdateService.Current.SetChannelAsync("alpha");

            Assert.Equal(ReleaseChannels.Default, UpdateService.Current.Channel);
            Assert.Equal(ReleaseChannels.Default, sandbox.ReadJson()["channel"]!.GetValue<string>());
        }
        finally
        {
            UpdateService.Current.Channel = original;
        }
    }

    /// <summary>The app's own C# files: no build output, no tests, no docs.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var directory in ScannedDirectories)
        {
            var path = RepoLayout.Path(directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", options))
            {
                yield return file;
            }
        }

        foreach (var name in ScannedRootFiles)
        {
            var file = RepoLayout.Path(name);
            if (File.Exists(file))
            {
                yield return file;
            }
        }
    }
}
