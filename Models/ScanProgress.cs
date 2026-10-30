namespace DevDesk.Models;

/// <summary>
/// One progress tick during a scan. <see cref="Total"/> is <c>0</c> while the
/// walk has not finished discovering candidates: the UI must treat 0 as
/// "indeterminate" rather than "nothing to do".
/// </summary>
public readonly record struct ScanProgress(int Completed, int Total, string CurrentName)
{
    public bool IsIndeterminate => Total <= 0;
}
