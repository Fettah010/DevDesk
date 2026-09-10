using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

public sealed partial class DiagnosticsPage : Page
{
    // PLACEHOLDER: a Serilog ring-buffer sink + log file loader fill this in a
    // later phase; the rows below just show the expected format.
    private readonly ObservableCollection<string> _lines = new()
    {
        "[2026-09-10 09:12:04.112] [INF] DevDesk starting",
        "[2026-09-10 09:12:04.402] [INF] Logging initialized. Log path: ...\\Logs",
        "[2026-09-10 09:12:05.001] [INF] Update source: https://github.com/... (channel stable)",
        "[2026-09-10 09:12:05.120] [DBG] Scanning C:\\Code\\projects (depth 3)",
        "[2026-09-10 09:12:05.201] [INF] Found project: DevTem-WinUI 3",
        "[2026-09-10 09:12:05.210] [INF] Found workspace: PIgen App (4 sub-projects)",
        "[2026-09-10 09:12:05.301] [INF] Repository C:\\Code\\projects\\MyWinUIApp is 2 commits behind upstream",
        "[2026-09-10 09:12:05.404] [INF] Dashboard cards refreshed (3 projects)",
        "[2026-09-10 09:12:05.501] [DBG] GitHub enrichment skipped: token not configured",
    };

    public DiagnosticsPage()
    {
        this.InitializeComponent();
        LogList.ItemsSource = _lines;
    }
}