namespace DevDesk.Models;

public sealed record GitHubRepositoryIdentity(string Host, string Owner, string Repository)
{
    public bool SupportsPublicApi =>
        string.Equals(Host, "github.com", StringComparison.OrdinalIgnoreCase);
}
