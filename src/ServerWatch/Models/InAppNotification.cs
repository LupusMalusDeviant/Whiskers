namespace ServerWatch.Models;

/// <summary>A notification shown in the in-app bell/feed. Severity is one of
/// "Error" | "Warning" | "Info" | "Success" (mapped to MudBlazor colours in the UI).</summary>
public sealed record InAppNotification(
    string Id,
    DateTime Timestamp,
    string EventType,
    string Title,
    string Detail,
    string Severity,
    string? Link = null);
