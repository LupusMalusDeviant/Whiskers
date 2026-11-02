using Whiskers.Models.Hetzner;
using Whiskers.Services.Cloud.Providers;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Cloud;

/// <summary>
/// Read-backs of the cloud capabilities (R1-37, INV-43). The provider accepts a call and applies it a little later, so
/// the server is read again until it shows the effect or the poll budget (within the capability's deadline) is spent;
/// a server that then still shows the old state fails the outcome. A provider that cannot be read fails it with the
/// state unknown.
/// </summary>
public abstract class CloudVerifier(IServiceProvider services, TimeSpan? budget, TimeSpan? interval)
    : ServiceVerifier(services)
{
    protected TimeSpan Budget { get; } = budget ?? TimeSpan.FromMinutes(3);
    protected TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(5);

    /// <summary>The Whiskers server the call named: the resolved target, else the argument.</summary>
    protected static string Server(MutationContext context) =>
        (context.Request.Target.Id is { Length: > 0 } id ? id : null)
        ?? VerifierSupport.Arg(context, "server")
        ?? throw new MutationNotVerifiableException("the call named no server to read back.");

    protected static async Task<T> ReadAsync<T>(Func<Task<T>> read, string what)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or MutationVerificationFailedException
                                       or MutationNotVerifiableException))
        {
            throw new MutationVerificationFailedException($"{what} could not be read ({ex.Message}).", stateUnknown: true);
        }
    }
}

/// <summary>cloud.power_on / cloud.shutdown: the provider reports the server running, or off.</summary>
public sealed class CloudPowerVerifier(IServiceProvider services, bool on, TimeSpan? budget = null, TimeSpan? interval = null)
    : CloudVerifier(services, budget, interval)
{
    public override string CapabilityId => on ? "cloud.power_on" : "cloud.shutdown";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var cloud = Module<ICloudControlService>("cloud control");
        var name = Server(context);
        var server = cloud.ResolveWhiskers(name)
                     ?? throw new MutationNotVerifiableException($"'{name}' is no configured cloud server.");
        var status = await VerifierSupport.PollAsync(
            async _ => (await ReadAsync(() => cloud.ResolveAsync(server), "the provider"))?.Status ?? "",
            Reached, Budget, Interval, ct);
        if (!Reached(status))
            throw new MutationVerificationFailedException(status.Length == 0
                ? "the provider no longer finds the server."
                : $"the provider reports the server '{status}', not {(on ? "running" : "off")}.");
    }

    // Hetzner: running / off; Hostinger: running / stopped.
    private bool Reached(string status) => on
        ? string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
        : status.ToLowerInvariant() is "off" or "stopped";
}

/// <summary>hetzner.rescue_enable / _disable, hetzner.backups_enable / _disable: Hetzner reports the flag as asked.</summary>
public sealed class HetznerFlagVerifier(IServiceProvider services, string capabilityId, TimeSpan? budget = null,
    TimeSpan? interval = null) : CloudVerifier(services, budget, interval)
{
    public override string CapabilityId => capabilityId;

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var cloud = Module<ICloudControlService>("cloud control");
        var name = Server(context);
        var (label, want, read) = capabilityId switch
        {
            "hetzner.rescue_enable" => ("rescue mode", true, (Func<HetznerServer, bool>)(s => s.RescueEnabled)),
            "hetzner.rescue_disable" => ("rescue mode", false, s => s.RescueEnabled),
            "hetzner.backups_enable" => ("backups", true, s => s.BackupsEnabled),
            "hetzner.backups_disable" => ("backups", false, s => s.BackupsEnabled),
            _ => throw new MutationNotVerifiableException($"'{capabilityId}' has no read-back."),
        };
        var state = await VerifierSupport.PollAsync(async _ =>
            {
                var ctx = await ReadAsync(() => cloud.HetznerContextAsync(name), "Hetzner");
                return ctx is { } c ? read(c.server) : (bool?)null;
            },
            // A server Hetzner no longer finds will not come back by waiting.
            s => s == want || s is null, Budget, Interval, ct);
        if (state is null)
            throw new MutationVerificationFailedException($"'{name}' is no longer found at Hetzner.", stateUnknown: true);
        if (state != want)
            throw new MutationVerificationFailedException($"Hetzner reports {label} {(state.Value ? "on" : "off")}.");
    }
}

/// <summary>hetzner.snapshot_delete: Hetzner no longer finds the image.</summary>
public sealed class HetznerSnapshotDeleteVerifier(IServiceProvider services) : CloudVerifier(services, null, null)
{
    public override string CapabilityId => "hetzner.snapshot_delete";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var cloud = Module<ICloudControlService>("cloud control");
        var hetzner = Module<IHetznerExtensions>("cloud control");
        var imageId = VerifierSupport.LongArg(context, "imageId")
                      ?? throw new MutationNotVerifiableException("the call named no image to read back.");
        var ctx = await ReadAsync(() => cloud.HetznerContextAsync(Server(context)), "Hetzner")
                  ?? throw new MutationNotVerifiableException("the server is no Hetzner server.");
        var image = await ReadAsync(() => hetzner.GetImageAsync(ctx.token, imageId, ct), "the image");
        if (image is not null)
            throw new MutationVerificationFailedException($"image #{imageId} still exists ({image.Status}).");
    }
}
