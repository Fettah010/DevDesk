using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// What the caller asks the activity centre to run. The delegate receives the
/// operation's cancellation token and the console progress it was given, so a
/// build streams through exactly one path whether it was started by a button or
/// by Retry.
/// </summary>
public sealed record OperationRequest(
    OperationKind Kind,
    string ProjectName,
    string ProjectPath,
    string Title,
    string Command,
    Func<CancellationToken, IProgress<ProcessOutputLine>?, Task<OperationOutcome>> Work,
    IProgress<ProcessOutputLine>? Output = null,
    string Configuration = "",
    string Platform = "");

public sealed record OperationHandle(string Id, Task<Operation> Completion);

/// <summary>
/// The queue, the running operation, the failures and the history (W-0.3.6).
/// <para>
/// One build at a time, process-wide: two <c>dotnet build</c> calls on the same
/// <c>obj/</c> corrupt each other, so a second request waits as a real
/// <see cref="OperationStatus.Queued"/> row instead of becoming a hidden second
/// process (D-0.3.4). A queued row is honest; a phantom is not.
/// </para>
/// <para>
/// No UI types and no dispatcher here: state changes are announced with
/// <see cref="Changed"/> and the view model marshals them. That is what makes the
/// queue testable without a window.
/// </para>
/// </summary>
public sealed class ActivityCenter
{
    private const int OperationCap = 200;

    private readonly object _gate = new();
    private readonly List<Operation> _operations = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly TimeProvider _time;
    private readonly BuildHistory _history;
    private readonly int _historyLimit;

    private int _sequence;
    private int _failures;

    public ActivityCenter(
        TimeProvider? time = null,
        BuildHistory? history = null,
        int historyLimit = BuildHistory.DefaultLimit)
    {
        _time = time ?? TimeProvider.System;
        _history = history ?? new BuildHistory(time: _time);
        _historyLimit = Math.Max(1, historyLimit);
    }

    /// <summary>Raised on the thread that changed state, never marshalled.</summary>
    public event EventHandler? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Operation> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToArray();
            }
        }
    }

    public Operation? Running
    {
        get
        {
            lock (_gate)
            {
                foreach (var operation in _operations)
                {
                    if (operation.IsRunning)
                    {
                        return operation;
                    }
                }
            }

            return null;
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var operation in _operations)
                {
                    if (operation.Status == OperationStatus.Queued)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>Failures in this session; the footer badge shows this and nothing else.</summary>
    public int FailureCount => Volatile.Read(ref _failures);

    public BuildHistory History => _history;

    /// <summary>
    /// The clock every timestamp on this centre came from. The title bar derives
    /// its elapsed time from this and the operation's <c>StartedAt</c>, so the
    /// strip and the duration in Activity can never disagree (gotcha 15).
    /// </summary>
    public TimeProvider Time => _time;

    public Operation? Find(string id)
    {
        lock (_gate)
        {
            return FindLocked(id);
        }
    }

    /// <summary>Queues the work and returns immediately; execution starts when the gate frees.</summary>
    public OperationHandle Submit(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = NewId();
        var operation = new Operation(
            id,
            request.Kind,
            request.ProjectName,
            request.ProjectPath,
            request.Title,
            request.Command,
            OperationStatus.Queued,
            _time.GetUtcNow(),
            Configuration: request.Configuration,
            Platform: request.Platform);

        var completion = new TaskCompletionSource<Operation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(request, new CancellationTokenSource(), completion);

        lock (_gate)
        {
            _entries[id] = entry;
            _operations.Insert(0, operation);
            TrimLocked();
        }

        RaiseChanged();
        _ = ExecuteAsync(id);

        return new OperationHandle(id, completion.Task);
    }

    /// <summary>
    /// Stops an operation. A queued one is marked cancelled and never starts; a
    /// running one is signalled and reports <see cref="OperationStatus.Canceled"/>
    /// when its process has actually been killed.
    /// </summary>
    public bool Cancel(string id)
    {
        Operation? cancelled = null;

        lock (_gate)
        {
            var current = FindLocked(id);
            if (current is null || current.IsFinished)
            {
                return false;
            }

            var entry = _entries[id];

            if (current.Status == OperationStatus.Queued)
            {
                cancelled = current with
                {
                    Status = OperationStatus.Canceled,
                    FinishedAt = _time.GetUtcNow(),
                    Hint = "Cancelled before it started.",
                };

                ReplaceLocked(cancelled);
            }

            entry.Cancellation.Cancel();

            if (cancelled is not null)
            {
                entry.Completion.TrySetResult(cancelled);
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Queues a new operation with the same request; the old id is never reused.</summary>
    public OperationHandle? Retry(string id)
    {
        OperationRequest request;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return null;
            }

            request = entry.Request;
        }

        return Submit(request);
    }

    private async Task ExecuteAsync(string id)
    {
        await _oneAtATime.WaitAsync().ConfigureAwait(false);

        try
        {
            Entry entry;
            Operation? started;

            lock (_gate)
            {
                entry = _entries[id];
                var current = FindLocked(id);

                // Cancelled while queued: the row is already final.
                if (current is null || current.Status != OperationStatus.Queued)
                {
                    return;
                }

                started = current with { Status = OperationStatus.Running, StartedAt = _time.GetUtcNow() };
                ReplaceLocked(started);
            }

            RaiseChanged();

            OperationOutcome outcome;

            try
            {
                outcome = await entry.Request.Work(entry.Cancellation.Token, entry.Request.Output)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = OperationOutcome.Canceled();
            }
            catch (Exception ex)
            {
                LoggingService.Log.Error(ex, "Operation {Id} ({Title}) failed unexpectedly", id, started.Title);
                outcome = OperationOutcome.Failed(
                    null,
                    ex.Message,
                    hint: "The operation failed unexpectedly. Open the log for details.");
            }

            Complete(id, outcome);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private void Complete(string id, OperationOutcome outcome)
    {
        Operation finished;

        lock (_gate)
        {
            var current = FindLocked(id);
            if (current is null)
            {
                return;
            }

            finished = current with
            {
                Status = outcome.Status,
                FinishedAt = _time.GetUtcNow(),
                ExitCode = outcome.ExitCode ?? current.ExitCode,
                FirstError = outcome.FirstError,
                LogPath = outcome.LogPath,
                Hint = outcome.Hint,
                OutputPath = outcome.OutputPath,
            };

            ReplaceLocked(finished);

            if (outcome.Status == OperationStatus.Failed)
            {
                Interlocked.Increment(ref _failures);
            }

            _entries[id].Completion.TrySetResult(finished);
        }

        _history.Add(
            new BuildRecord(
                finished.ProjectPath,
                finished.ProjectName,
                finished.Kind,
                finished.Status,
                finished.StartedAt ?? finished.QueuedAt,
                finished.Duration ?? TimeSpan.Zero,
                finished.ExitCode,
                finished.FirstError,
                finished.LogPath,
                finished.Configuration,
                finished.Platform),
            _historyLimit);

        RaiseChanged();
    }

    private Operation? FindLocked(string id)
    {
        foreach (var operation in _operations)
        {
            if (string.Equals(operation.Id, id, StringComparison.Ordinal))
            {
                return operation;
            }
        }

        return null;
    }

    private void ReplaceLocked(Operation updated)
    {
        for (var index = 0; index < _operations.Count; index++)
        {
            if (string.Equals(_operations[index].Id, updated.Id, StringComparison.Ordinal))
            {
                _operations[index] = updated;
                return;
            }
        }
    }

    private void TrimLocked()
    {
        if (_operations.Count <= OperationCap)
        {
            return;
        }

        _operations.RemoveRange(OperationCap, _operations.Count - OperationCap);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private string NewId() => $"op-{Interlocked.Increment(ref _sequence):000}-{Guid.NewGuid():N}";

    private sealed record Entry(
        OperationRequest Request,
        CancellationTokenSource Cancellation,
        TaskCompletionSource<Operation> Completion);
}
