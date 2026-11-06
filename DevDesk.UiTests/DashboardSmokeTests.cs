using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;

namespace DevDesk.UiTests;

public sealed class DashboardSmokeTests
{
    private const int M2WindowVisibleBudgetMs = 400;
    private readonly ITestOutputHelper _output;

    public DashboardSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Desktop")]
    public void AppSupportsSearchPinningAndIgnorePreviewThroughUiAutomation()
    {
        var executable = Environment.GetEnvironmentVariable("DEVDESK_UI_EXE");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Set DEVDESK_UI_EXE to the built DevDesk.exe.");
        Assert.True(File.Exists(executable), $"DevDesk.exe was not found at {executable}.");

        var previousDataDirectory = Environment.GetEnvironmentVariable("DEVDESK_DATA_DIR");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "devdesk-ui-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(dataDirectory, "root");
        Directory.CreateDirectory(dataDirectory);
        for (var index = 0; index < 100; index++)
        {
            var name = index == 0 ? "UiProject" : $"Project-{index:D2}";
            var project = Path.Combine(root, name);
            Directory.CreateDirectory(project);
            if (index == 0)
            {
                Directory.CreateDirectory(Path.Combine(project, "bin"));
            }

            File.WriteAllText(
                Path.Combine(project, $"{name}.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");
        }
        File.WriteAllText(
            Path.Combine(dataDirectory, "settings.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                lastRoute = "dashboard",
                roots = new[]
                {
                    new
                    {
                        path = root,
                        depth = 2,
                        enabled = true,
                        ignores = new[] { "bin", "obj", ".vs", ".git" },
                    },
                },
            }));
        Environment.SetEnvironmentVariable("DEVDESK_DATA_DIR", dataDirectory);

        try
        {
            using var app = Application.Launch(executable);
            _output.WriteLine("Launched DevDesk.");
            double? measuredUiDispatchDelay = null;
            using var process = Process.GetProcessById(app.ProcessId);
            using var automation = new UIA3Automation();
            try
            {
                var elapsed = Stopwatch.StartNew();
                Window? window = null;
                while (window is null && elapsed.Elapsed < TimeSpan.FromSeconds(30))
                {
                    window = app.GetMainWindow(automation);
                    if (window is null)
                    {
                        Thread.Sleep(250);
                    }
                }

                Assert.NotNull(window);
                Assert.NotNull(window.FindFirstDescendant(condition => condition.ByName("Dashboard")));
                _output.WriteLine("Dashboard is accessible.");
                var windowActivatedMs = ReadStartupMark(dataDirectory, "window activated");
                _output.WriteLine(windowActivatedMs is null
                    ? "Window activation startup mark was not found."
                    : $"App window activation mark: {windowActivatedMs} ms.");

                Assert.Equal(100, WaitForCachedProjectCount(dataDirectory, 100, TimeSpan.FromSeconds(30)));
                var projectsNavigation = FindNavigationItem(window, "Projects", TimeSpan.FromSeconds(10));
                Assert.NotNull(projectsNavigation);
                NavigateTo(projectsNavigation);
                _output.WriteLine("Opened Projects.");

                var projectsList = FindFirstAutomationId(window, "ProjectsList", TimeSpan.FromSeconds(10));
                Assert.NotNull(projectsList);
                var search = FindFirstAutomationId(window, "FilterBox", TimeSpan.FromSeconds(10))?.AsTextBox();
                Assert.NotNull(search);
                search.Text = "UiProject";
                var projectResult = FindFirst(projectsList, "UiProject", TimeSpan.FromSeconds(10));
                Assert.NotNull(projectResult);
                Assert.NotNull(FindAncestor(projectResult, ControlType.ListItem));
                var detailTitle = FindFirstAutomationId(window, "DetailTitle", TimeSpan.FromSeconds(10));
                Assert.NotNull(detailTitle);
                Assert.True(WaitForName(detailTitle, "UiProject", TimeSpan.FromSeconds(5)),
                    $"The selected project details did not update. Detail title: '{detailTitle.Name}'.");
                _output.WriteLine("Found and selected the filtered project.");

                var pin = FindFirst(window, "Pin project", TimeSpan.FromSeconds(10))
                    ?? window.FindFirstDescendant(condition => condition.ByAutomationId("PinProjectButton"))
                    ?? FindFirst(window, "Pin", TimeSpan.FromSeconds(1));
                if (pin is null)
                {
                    _output.WriteLine("Projects-page UIA descendants:");
                    foreach (var element in window.FindAllDescendants())
                    {
                        _output.WriteLine(
                            $" {element.Properties.ControlType.ValueOrDefault} " +
                            $"id='{element.Properties.AutomationId.ValueOrDefault}' " +
                            $"name='{element.Properties.Name.ValueOrDefault}'");
                    }
                }

                Assert.NotNull(pin);
                pin.AsButton().Invoke();
                Assert.NotNull(FindFirst(window, "Unpin project", TimeSpan.FromSeconds(5)));
                _output.WriteLine("Pinned the selected project.");

                var settingsNavigation = FindNavigationItem(window, "Settings", TimeSpan.FromSeconds(10));
                Assert.NotNull(settingsNavigation);
                NavigateTo(settingsNavigation);
                _output.WriteLine("Opened Settings.");
                var previewPath = FindFirstAutomationId(window, "IgnorePreviewPathBox", TimeSpan.FromSeconds(10))?.AsTextBox();
                Assert.NotNull(previewPath);
                _output.WriteLine("Found the ignore preview input.");
                previewPath.Text = @"UiProject\bin\generated";
                var previewButton = FindFirst(window, "Preview", TimeSpan.FromSeconds(10));
                Assert.NotNull(previewButton);
                previewButton.AsButton().Invoke();
                _output.WriteLine("Ran the ignore preview.");
                Assert.NotNull(FindFirst(window, "UiProject/bin/generated is excluded by 'bin'.", TimeSpan.FromSeconds(10)));
                _output.WriteLine("Ignore preview matched the scanner rule.");
                var diagnosticsNavigation = FindNavigationItem(window, "Diagnostics", TimeSpan.FromSeconds(10));
                Assert.NotNull(diagnosticsNavigation);
                NavigateTo(diagnosticsNavigation);
                var dispatchDelay = ReadUiDispatchDelay(window, TimeSpan.FromSeconds(10));
                Assert.NotNull(dispatchDelay);
                _output.WriteLine(
                    $"M6 maximum UI dispatch delay during startup, scan, and page navigation: {dispatchDelay:0} ms.");
                measuredUiDispatchDelay = dispatchDelay;
                var cachedProjectCount = WaitForCachedProjectCount(dataDirectory, 100, TimeSpan.FromSeconds(30));
                Assert.Equal(100, cachedProjectCount);
                process.Refresh();
                var workingSet = process.WorkingSet64;
                _output.WriteLine($"M5 application working set with 100 cached projects: {workingSet / 1024 / 1024} MiB.");
                Assert.True(workingSet < 300L * 1024 * 1024, $"DevDesk working set reached {workingSet / 1024 / 1024} MiB with 100 projects.");
            }
            finally
            {
                _ = app.Close();
                if (!process.HasExited && !process.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    app.Kill();
                    Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)), "The launched DevDesk process did not exit after termination.");
                }
            }

            var logPath = Path.Combine(dataDirectory, "Logs");
            var logFile = Directory.GetFiles(logPath, "applog-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            Assert.NotNull(logFile);
            Assert.DoesNotContain("Projects Git status for", File.ReadAllText(logFile));

            var organizationPath = Path.Combine(dataDirectory, "project-organization.json");
            Assert.True(File.Exists(organizationPath), "The UI pin action did not persist project organization metadata.");
            Assert.Contains("\"isPinned\": true", File.ReadAllText(organizationPath), StringComparison.OrdinalIgnoreCase);

            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject()
                ?? throw new InvalidDataException("The isolated settings file could not be read.");
            settings["lastRoute"] = "dashboard";
            File.WriteAllText(settingsPath, settings.ToJsonString());

            var warmStartup = Stopwatch.StartNew();
            using var warmApp = Application.Launch(executable);
            using var warmProcess = Process.GetProcessById(warmApp.ProcessId);
            using var warmAutomation = new UIA3Automation();
            long? warmCardVisibleMs = null;
            long? warmWindowVisibleMs = null;
            long? warmProjectCardsReadyMs = null;
            try
            {
                var visibleHandle = WaitForVisibleWindow(warmProcess, TimeSpan.FromSeconds(30));
                Assert.NotEqual(IntPtr.Zero, visibleHandle);
                warmWindowVisibleMs = warmStartup.ElapsedMilliseconds;
                _output.WriteLine(
                    $"M2 observed the cached window handle visible after {warmWindowVisibleMs.Value} ms " +
                    $"(target <{M2WindowVisibleBudgetMs} ms).");
                var warmWindow = warmApp.GetMainWindow(warmAutomation);
                Assert.NotNull(warmWindow);
                Assert.NotNull(warmWindow.FindFirstDescendant(condition => condition.ByName("Dashboard")));
                var dashboardProjects = warmWindow.FindFirstDescendant(
                    condition => condition.ByAutomationId("DashboardProjectsGrid"));
                Assert.NotNull(dashboardProjects);
                var warmCard = FindFirst(
                    dashboardProjects,
                    "UiProject",
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromMilliseconds(10));
                Assert.NotNull(warmCard);
                Assert.NotNull(FindAncestor(warmCard, ControlType.ListItem));
                warmCardVisibleMs = warmStartup.ElapsedMilliseconds;
                _output.WriteLine($"UIA observed the cached project card after {warmCardVisibleMs} ms.");
                warmProjectCardsReadyMs = ReadStartupMark(dataDirectory, "first project cards ready");
                Assert.NotNull(warmProjectCardsReadyMs);
                _output.WriteLine($"Warm cached project cards ready mark: {warmProjectCardsReadyMs} ms.");
                var firstSnapshotMs = ReadStartupMark(dataDirectory, "first snapshot");
                Assert.NotNull(firstSnapshotMs);
                _output.WriteLine($"Warm cached snapshot available: {firstSnapshotMs} ms (data readiness, not M1).");
                var warmWindowActivatedMs = ReadStartupMark(dataDirectory, "window activated");
                Assert.NotNull(warmWindowActivatedMs);
                _output.WriteLine($"App window activation mark: {warmWindowActivatedMs} ms.");
                foreach (var mark in ReadStartupMarks(dataDirectory))
                {
                    _output.WriteLine($"Startup stage '{mark.Name}': {mark.ElapsedMs} ms.");
                }
            }
            finally
            {
                _ = warmApp.Close();
                if (!warmProcess.HasExited && !warmProcess.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    warmApp.Kill();
                    Assert.True(warmProcess.WaitForExit(TimeSpan.FromSeconds(5)), "The warm-launch DevDesk process did not exit after termination.");
                }
            }

            var gateFailures = new List<string>();
            if (warmProjectCardsReadyMs is not { } cardsReadyMs || cardsReadyMs >= 700)
            {
                gateFailures.Add($"M1 cached project card layout {warmProjectCardsReadyMs?.ToString() ?? "not measured"} ms (target <700 ms)");
            }

            if (warmCardVisibleMs is not { } cardMs || cardMs >= 700)
            {
                gateFailures.Add($"M1 cached card {warmCardVisibleMs?.ToString() ?? "not observed"} ms (target <700 ms)");
            }

            if (warmWindowVisibleMs is not { } windowMs || windowMs >= M2WindowVisibleBudgetMs)
            {
                gateFailures.Add($"M2 visible window {warmWindowVisibleMs?.ToString() ?? "not observed"} ms (target <{M2WindowVisibleBudgetMs} ms)");
            }

            if (measuredUiDispatchDelay is not { } dispatchMs || dispatchMs >= 50)
            {
                gateFailures.Add($"M6 UI dispatch delay {measuredUiDispatchDelay?.ToString("0") ?? "not measured"} ms (target <50 ms)");
            }

            Assert.True(gateFailures.Count == 0, string.Join("; ", gateFailures));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVDESK_DATA_DIR", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Delete(dataDirectory, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(250);
                    }
                }
            }
        }
    }

    private static AutomationElement? FindFirst(
        AutomationElement root,
        string name,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            var element = root.FindFirstDescendant(condition => condition.ByName(name));
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(pollInterval ?? TimeSpan.FromMilliseconds(100));
        }

        return null;
    }

    private static AutomationElement? FindNavigationItem(Window window, string name, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            var item = window.FindAllDescendants(condition => condition.ByName(name))
                .FirstOrDefault(element => element.Properties.ControlType.ValueOrDefault == ControlType.ListItem);
            if (item is not null)
            {
                return item;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static void NavigateTo(AutomationElement navigationItem)
    {
        if (navigationItem.Patterns.Invoke.IsSupported)
        {
            navigationItem.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        navigationItem.Patterns.SelectionItem.Pattern.Select();
    }

    private static AutomationElement? FindFirstAutomationId(AutomationElement root, string automationId, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            var element = root.FindFirstDescendant(condition => condition.ByAutomationId(automationId));
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static double? ReadUiDispatchDelay(Window window, TimeSpan timeout)
    {
        var label = FindFirst(window, "Max UI dispatch delay", timeout);
        if (label is null)
        {
            return null;
        }

        var row = label.Parent;
        while (row is not null)
        {
            var texts = row.FindAllDescendants(condition => condition.ByControlType(ControlType.Text))
                .Select(element => element.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            if (texts.Any(name => name.Contains("budget 50 ms · M6", StringComparison.Ordinal)))
            {
                var value = texts.FirstOrDefault(name =>
                    Regex.IsMatch(name, @"^\d+(?:\.\d+)? ms$", RegexOptions.CultureInvariant));
                return value is not null
                    && double.TryParse(
                        value.AsSpan(0, value.Length - 3),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var milliseconds)
                    ? milliseconds
                    : null;
            }

            row = row.Parent;
        }

        return null;
    }

    private static IntPtr WaitForVisibleWindow(Process process, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero && IsWindowVisible(handle))
            {
                return handle;
            }

            Thread.Sleep(10);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private static bool WaitForName(AutomationElement element, string name, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (element.Name == name)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return element.Name == name;
    }

    private static AutomationElement? FindAncestor(AutomationElement element, ControlType controlType)
    {
        var current = element;
        while (current is not null)
        {
            if (current.Properties.ControlType.ValueOrDefault == controlType)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static long? ReadStartupMark(string dataDirectory, string stage)
    {
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        var logPath = Directory.Exists(logDirectory)
            ? Directory.GetFiles(logDirectory, "applog-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (logPath is null)
        {
            return null;
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var log = reader.ReadToEnd();
        var matches = Regex.Matches(log, $@"Startup: {Regex.Escape(stage)} at \+(\d+) ms");
        var match = matches.LastOrDefault();
        return match is not null && long.TryParse(match.Groups[1].Value, out var elapsed) ? elapsed : null;
    }

    private static IReadOnlyList<(string Name, long ElapsedMs)> ReadStartupMarks(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        var logPath = Directory.Exists(logDirectory)
            ? Directory.GetFiles(logDirectory, "applog-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (logPath is null)
        {
            return Array.Empty<(string, long)>();
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var log = reader.ReadToEnd();
        return Regex.Matches(log, @"Startup: (?<name>.+?) at \+(\d+) ms")
            .Select(match => (match.Groups["name"].Value, long.Parse(match.Groups[1].Value)))
            .ToArray();
    }

    private static int? WaitForCachedProjectCount(string dataDirectory, int expectedCount, TimeSpan timeout)
    {
        var cachePath = Path.Combine(dataDirectory, "scan-cache.json");
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (File.Exists(cachePath))
            {
                var cache = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject;
                var count = (cache?["projects"] as JsonArray)?.Count;
                if (count == expectedCount)
                {
                    return count;
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }
}
