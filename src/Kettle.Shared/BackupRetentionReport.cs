namespace Kettle.Shared;
public sealed record BackupRetentionReport(string Scope, string[] Kept, string[] Deleted, string[] Protected, string[] Incomplete, string? Error = null, SaveError? Failure = null);
