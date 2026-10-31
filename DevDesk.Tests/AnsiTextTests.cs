using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

/// <summary>
/// The console filter has one job and two failure modes: leave escape codes in
/// (unreadable output) or eat real text (a message nobody can act on). These
/// cases pin both sides.
/// </summary>
public class AnsiTextTests
{
    [Fact]
    public void Clean_RemovesColourCodes()
    {
        var cleaned = AnsiText.Clean("\u001b[32mBuild succeeded\u001b[0m");

        Assert.Equal("Build succeeded", cleaned);
    }

    [Fact]
    public void Clean_RemovesCursorAndPrivateModes()
    {
        Assert.Equal("spinner", AnsiText.Clean("\u001b[?25lspinner\u001b[?25h"));
        Assert.Equal("line", AnsiText.Clean("\u001b[2K\u001b[1Gline"));
    }

    [Fact]
    public void Clean_KeepsOrdinaryTextIncludingBrackets()
    {
        const string text = "Formixa.csproj(12,5): error CS1002: ; expected [C:\\Code\\Formixa]";

        Assert.Equal(text, AnsiText.Clean(text));
    }

    [Fact]
    public void Clean_RemovesABell()
    {
        Assert.Equal("done", AnsiText.Clean("done\u0007"));
    }

    [Fact]
    public void CollapseCarriageReturns_KeepsTheLastSegment()
    {
        Assert.Equal("100%", AnsiText.CollapseCarriageReturns("10%\r50%\r100%"));
    }

    [Fact]
    public void CollapseCarriageReturns_KeepsTextBeforeATrailingReturn()
    {
        Assert.Equal("done", AnsiText.CollapseCarriageReturns("done\r"));
    }

    [Fact]
    public void CollapseCarriageReturns_LeavesPlainLinesAlone()
    {
        Assert.Equal("no returns here", AnsiText.CollapseCarriageReturns("no returns here"));
    }

    [Fact]
    public void ForConsole_AppliesBothFilters()
    {
        Assert.Equal("ok", AnsiText.ForConsole("\u001b[32m5%\r\u001b[32mok\u001b[0m"));
    }
}
