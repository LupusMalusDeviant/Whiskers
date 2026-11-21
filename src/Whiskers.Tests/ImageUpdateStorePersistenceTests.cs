using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>
/// The image-update store has to survive a restart (2026-08-28).
///
/// <para>It lived only in memory. With a six-hour check interval that meant every restart left Whiskers
/// answering "no updates available" for hours — not because there were none, but because it had not looked
/// yet. On a day with ten deploys it never got to look at all, and the answer it gave was
/// indistinguishable from a checked one.</para>
///
/// <para>That is the same shape as everything else this project has been removing: an empty result that
/// carries no sign of being empty for the wrong reason.</para>
/// </summary>
public sealed class ImageUpdateStorePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"iustore-{Guid.NewGuid():N}");

    public ImageUpdateStorePersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private ImageUpdateStore New() => new(null, new DataPathOptions(_dir));

    private static ImageUpdateInfo Info(string container, bool available = true) => new()
    {
        ContainerId = container,
        ContainerName = container,
        ServerId = "host-b",
        Image = "tecnativa/docker-socket-proxy:latest",
        UpdateAvailable = available
    };

    [Fact]
    public async Task What_was_checked_survives_a_restart()
    {
        // THE case. Ten deploys in a day meant ten wipes, and after each one the honest answer was "I have
        // not looked", while the reported answer was "nothing to update".
        var first = New();
        first.Set(Info("socket-proxy"));
        first.LastCheckAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        await first.SaveAsync();

        var afterRestart = New();

        var pending = Assert.Single(afterRestart.GetAllPendingUpdates());
        Assert.Equal("socket-proxy", pending.ContainerName);
        Assert.Equal(first.LastCheckAt, afterRestart.LastCheckAt);
    }

    [Fact]
    public async Task Containers_without_an_update_survive_too()
    {
        // Knowing that something WAS checked and had nothing is as valuable as knowing about an update —
        // it is the difference between "checked, nothing there" and "never looked".
        var first = New();
        first.Set(Info("caddy", available: false));
        await first.SaveAsync();

        var afterRestart = New();

        Assert.Single(afterRestart.GetAll());
        Assert.Empty(afterRestart.GetAllPendingUpdates());
    }

    [Fact]
    public void A_fresh_installation_starts_empty_without_complaining()
    {
        var store = New();

        Assert.Empty(store.GetAll());
        Assert.Null(store.LastCheckAt);
    }

    [Fact]
    public async Task A_damaged_file_does_not_stop_the_service_from_starting()
    {
        // Refusing to boot over an unreadable cache file would turn a cosmetic problem into an outage. It is
        // logged instead — and the empty LastCheckAt then correctly reads as "never looked".
        await File.WriteAllTextAsync(new DataPathOptions(_dir).ImageUpdatesJson, "{ this is not json");

        var store = New();

        Assert.Empty(store.GetAll());
        Assert.Null(store.LastCheckAt);
    }

    [Fact]
    public async Task The_newest_state_wins_when_it_is_written_again()
    {
        var store = New();
        store.Set(Info("socket-proxy"));
        await store.SaveAsync();

        store.Set(Info("socket-proxy", available: false));   // updated, no longer pending
        await store.SaveAsync();

        Assert.Empty(New().GetAllPendingUpdates());
    }
}
