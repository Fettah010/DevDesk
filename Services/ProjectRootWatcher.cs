using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Coalesces local filesystem changes into catalog reconciliation scans.</summary>
public sealed class ProjectRootWatcher : IDisposable
{
    private const int MaxWatchedRoots = 32;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly ProjectCatalog _catalog;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, ScanRootSettings> _pendingRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ScanRootSettings Root, string Path)> _pendingCheckouts =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ScanRootSettings> _configuredRoots = Array.Empty<ScanRootSettings>();
    private int _refreshInProgress;
    private int _refreshPending;
    private bool _disposed;

    public ProjectRootWatcher(ProjectCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timer = new Timer(OnDebounce, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Reconfigure(IEnumerable<ScanRootSettings> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var enabled = roots
            .Where(root => root.Enabled && !string.IsNullOrWhiteSpace(root.Path))
            .Select(root => new ScanRootSettings
            {
                Path = root.Path,
                Depth = root.Depth,
                Enabled = true,
                Ignores = root.Ignores?.ToList() ?? new List<string>(),
            })
            .ToArray();
        var configured = enabled.Take(MaxWatchedRoots).ToArray();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _configuredRoots = enabled;
            _pendingRoots.Clear();
            _pendingCheckouts.Clear();
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            foreach (var root in configured)
            {
                if (!Directory.Exists(root.Path))
                {
                    continue;
                }

                try
                {
                    var matcher = new IgnoreRuleMatcher(root.Ignores);
                    var watcher = new FileSystemWatcher(root.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.DirectoryName
                            | NotifyFilters.FileName
                            | NotifyFilters.LastWrite
                            | NotifyFilters.Size,
                        EnableRaisingEvents = false,
                    };
                    watcher.Changed += (_, e) => OnPathChanged(root, matcher, e.FullPath);
                    watcher.Created += (_, e) => OnPathChanged(root, matcher, e.FullPath);
                    watcher.Deleted += (_, e) => OnPathChanged(root, matcher, e.FullPath);
                    watcher.Renamed += (_, e) => OnPathChanged(root, matcher, e.FullPath);
                    watcher.Error += (_, e) => OnWatcherError(root, e.GetException());
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or PlatformNotSupportedException)
                {
                    LoggingService.Log.Warning(ex, "Could not watch project root {Root}", root.Path);
                }
            }

            if (enabled.Length > MaxWatchedRoots)
            {
                LoggingService.Log.Warning(
                    "Only the first {Limit} enabled scan roots are watched; manual refresh remains available",
                    MaxWatchedRoots);
            }
        }
    }

    private void OnPathChanged(ScanRootSettings root, IgnoreRuleMatcher matcher, string changedPath)
    {
        try
        {
            if (!ShouldInvalidatePath(root.Path, matcher, changedPath))
            {
                return;
            }

            var checkout = _catalog.Snapshot.Projects
                .Where(project => PathHygiene.IsUnder(changedPath, project.Path))
                .OrderByDescending(project => project.Path.Length)
                .FirstOrDefault();
            if (checkout is not null
                && !string.IsNullOrWhiteSpace(checkout.ScanRootPath)
                && string.Equals(
                    PathHygiene.Canonicalize(checkout.ScanRootPath),
                    PathHygiene.Canonicalize(root.Path),
                    StringComparison.OrdinalIgnoreCase))
            {
                ScheduleCheckout(root, checkout.Path);
            }
            else
            {
                ScheduleRefresh(root);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            LoggingService.Log.Warning(ex, "Could not classify a filesystem change under {Root}", root.Path);
            ScheduleRefresh(root);
        }
    }

    public static bool ShouldInvalidatePath(string root, IgnoreRuleMatcher matcher, string changedPath)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        if (!PathHygiene.IsUnder(changedPath, root))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(root, changedPath);
        if (relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            .Any(PathHygiene.IsSafetyIgnoredFolderName))
        {
            return false;
        }

        return !matcher.IsExcluded(relativePath) || matcher.MayContainNegatedPath(relativePath);
    }

    /// <summary>
    /// A watcher that overflowed (its buffer could not keep up) or stopped is
    /// treated as "we no longer know what changed here": the whole root is
    /// reconciled once the debounce settles. The tests reach this handler
    /// directly, because provoking a real buffer overflow needs thousands of
    /// events and is not reliable on CI.
    /// </summary>
    private void OnWatcherError(ScanRootSettings root, Exception? error)
    {
        LoggingService.Log.Warning(error, "Filesystem watcher for {Root} overflowed or stopped; reconciling that root", root.Path);
        ScheduleRefresh(root);
    }

    private void ScheduleRefresh(ScanRootSettings root)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingRoots[root.Path] = root;
            foreach (var checkout in _pendingCheckouts
                .Where(entry => string.Equals(
                    entry.Value.Root.Path,
                    root.Path,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToArray())
            {
                _pendingCheckouts.Remove(checkout);
            }

            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ScheduleCheckout(ScanRootSettings root, string checkoutPath)
    {
        lock (_gate)
        {
            if (_disposed || _pendingRoots.ContainsKey(root.Path))
            {
                return;
            }

            _pendingCheckouts[checkoutPath] = (root, checkoutPath);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnDebounce(object? state)
    {
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _refreshPending, 1);
            return;
        }

        ScanRootSettings[] roots;
        (ScanRootSettings Root, string Path)[] checkouts;
        IReadOnlyList<ScanRootSettings> configuredRoots;
        lock (_gate)
        {
            roots = _pendingRoots.Values.ToArray();
            _pendingRoots.Clear();
            checkouts = _pendingCheckouts.Values.ToArray();
            _pendingCheckouts.Clear();
            configuredRoots = _configuredRoots;
        }

        try
        {
            foreach (var root in roots)
            {
                try
                {
                    await _catalog.RefreshRootAsync(root, configuredRoots).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoggingService.Log.Error(ex, "Filesystem change refresh failed for root {Root}", root.Path);
                }
            }

            foreach (var checkout in checkouts)
            {
                try
                {
                    await _catalog.RefreshCheckoutAsync(checkout.Root, checkout.Path).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoggingService.Log.Error(ex, "Filesystem change refresh failed for checkout {Checkout}", checkout.Path);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
            if (Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
                lock (_gate)
                {
                    if (!_disposed && (_pendingRoots.Count > 0 || _pendingCheckouts.Count > 0))
                    {
                        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _pendingRoots.Clear();
            _pendingCheckouts.Clear();
        }
    }
}
