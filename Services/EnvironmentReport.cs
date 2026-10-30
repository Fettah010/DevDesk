using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>
/// The facts a support conversation needs before it can start: what build, what
/// OS, what runtime, what tools (roadmap §6.4, §6.5.7). Rendered into
/// <c>environment.txt</c> in the export bundle and shown on Diagnostics.
/// <para>
/// <see cref="Collect"/> is pure — every value that needs a process call arrives
/// as an argument — so the report can be tested without git, without vpk and
/// without a network.
/// </para>
/// </summary>
public sealed class EnvironmentReport
{
    private readonly Dictionary<string, string> _byLabel;

    private EnvironmentReport(IReadOnlyList<KeyValuePair<string, string>> facts)
    {
        Facts = facts;
        _byLabel = new Dictionary<string, string>(
            facts.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<KeyValuePair<string, string>> Facts { get; }

    public string? Value(string label) => _byLabel.TryGetValue(label, out var value) ? value : null;

    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var fact in Facts)
        {
            builder.Append(fact.Key).Append(": ").AppendLine(fact.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the report. <paramref name="gitVersion"/>/<paramref name="vpkVersion"/>
    /// are null when the tool is not installed, and say so rather than guessing.
    /// </summary>
    public static EnvironmentReport Collect(
        string? gitVersion = null,
        string? vpkVersion = null,
        string? channel = null,
        string? scanRoot = null,
        string? settingsPath = null)
    {
        var facts = new List<KeyValuePair<string, string>>();

        var settings = settingsPath ?? SettingsService.Current.SettingsPath;

        Add("DevDesk", AppInfo.Current.DisplayVersion);
        Add("Update channel", channel ?? ReleaseChannels.Default);
        Add("OS", Describe(() => RuntimeInformation.OSDescription));
        Add("OS version", Describe(() => Environment.OSVersion.VersionString));
        Add("Process architecture", Describe(() => RuntimeInformation.ProcessArchitecture.ToString()));
        Add("Windows App SDK", WindowsAppSdkVersion());
        Add(".NET runtime", Describe(() => RuntimeInformation.FrameworkDescription));
        Add("git", gitVersion ?? "not found");
        Add("vpk", vpkVersion ?? "not found");
        Add("Scan root", scanRoot ?? ScanEngine.DefaultScanRoot);
        Add("Settings", settings);
        Add("Logs", string.IsNullOrEmpty(LoggingService.LogDirectoryPath)
            ? "(initialising)"
            : LoggingService.LogDirectoryPath);
        Add("Working set", Describe(() => $"{Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB"));
        Add("Collected", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));

        return new EnvironmentReport(facts);

        void Add(string label, string value) => facts.Add(new KeyValuePair<string, string>(label, value));
    }

    /// <summary>
    /// Collects the facts that need a subprocess (git, vpk). Bounded: a missing
    /// or hung tool costs a few seconds at most and never blocks the caller.
    /// </summary>
    public static async Task<EnvironmentReport> CollectAsync(
        string? channel = null,
        string? scanRoot = null,
        CancellationToken cancellationToken = default)
    {
        var git = await GitVersionAsync(cancellationToken).ConfigureAwait(false);
        var vpk = await VpkVersionAsync(cancellationToken).ConfigureAwait(false);

        return Collect(git, vpk, channel, scanRoot);
    }

    private static async Task<string?> GitVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await GitRunner
                .RunAsync(AppContext.BaseDirectory, cancellationToken, "--version")
                .ConfigureAwait(false);

            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut)
                ? result.StdOut.Trim()
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// vpk is a local dotnet tool (<c>dotnet vpk</c>) and has no
    /// <c>--version</c> flag, but its help banner names the version:
    /// <c>Velopack CLI 1.2.161, for distributing applications.</c>
    /// </summary>
    private static async Task<string?> VpkVersionAsync(CancellationToken cancellationToken)
    {
        foreach (var (file, args) in new[]
                 {
                     ("vpk", new[] { "--help" }),
                     ("dotnet", new[] { "vpk", "--help" }),
                 })
        {
            var text = await TryRunAsync(file, args, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            var match = VelopackBanner.Match(text);
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex VelopackBanner = new(
        @"Velopack CLI\s+(?<version>[0-9][0-9A-Za-z.\-+]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Runs a tool and returns its standard output, or null when it could not be
    /// started. The exit code is deliberately ignored: <c>vpk --help</c> prints
    /// its banner and then exits 1.
    /// </summary>
    private static async Task<string?> TryRunAsync(
        string fileName,
        string[] args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }

                return null;
            }

            var text = (await output.ConfigureAwait(false)).Trim();
            await error.ConfigureAwait(false);

            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string Describe(Func<string> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The Windows App SDK <em>package</em> version, which is what a bug report
    /// needs. The WinUI assembly is versioned 3.x independently of the package
    /// (2.6.4), so reading it would report a number nobody can match to a release;
    /// the runtime dependency list carries the real one.
    /// </summary>
    private static string WindowsAppSdkVersion()
    {
        try
        {
            foreach (var deps in DepsCandidates())
            {
                if (TryReadWindowsAppSdkVersion(deps) is { } version)
                {
                    return version;
                }
            }
        }
        catch
        {
            // A missing or unreadable deps file is not a reason to fail a report.
        }

        return "unknown";
    }

    private static System.Collections.Generic.IEnumerable<string> DepsCandidates()
    {
        var preferred = System.IO.Path.Combine(AppContext.BaseDirectory, "DevDesk.deps.json");
        if (System.IO.File.Exists(preferred))
        {
            yield return preferred;
        }

        string[] all;
        try
        {
            all = System.IO.Directory.GetFiles(AppContext.BaseDirectory, "*.deps.json");
        }
        catch
        {
            yield break;
        }

        Array.Sort(all, StringComparer.OrdinalIgnoreCase);
        foreach (var file in all)
        {
            if (!string.Equals(file, preferred, StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static string? TryReadWindowsAppSdkVersion(string depsPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(depsPath));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return null;
        }

        const string prefix = "Microsoft.WindowsAppSDK/";
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return library.Name[prefix.Length..];
            }
        }

        return null;
    }
}
