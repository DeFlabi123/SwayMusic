namespace SwayMusic.Core.Storage;

public sealed record AccountSession(
    string CookieHeader,
    string? VisitorData = null,
    string? DataSyncId = null,
    int AuthUser = 0,
    string? DisplayName = null,
    string? Handle = null,
    string? ChannelId = null)
{
    public override string ToString() => "AccountSession { Credentials = [redacted] }";
}
