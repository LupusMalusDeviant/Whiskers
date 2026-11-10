using System.Reflection;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 test support: typed <see cref="ResolvedTarget"/>s for tests that do not run against an inventory.
/// Only the resolver constructs one in the app (D4 a); a test reaches its token and its object builder by reflection —
/// the one way the contract leaves open — and builds the object of the target as named, marked resolved.
/// </summary>
internal static class TestTargets
{
    private static readonly CapabilityCatalog Catalog = new();

    private static readonly MethodInfo DefaultObject = typeof(MutationTargetResolver)
        .GetMethod("Default", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MutationTargetResolver.Default not found.");

    private static readonly PropertyInfo TokenProperty = typeof(MutationTargetResolver)
        .GetProperty("Token", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MutationTargetResolver.Token not found.");

    private static MutationTargetResolver.ResolverToken Token => (MutationTargetResolver.ResolverToken)TokenProperty.GetValue(null)!;

    /// <summary>The object of <paramref name="capability"/>'s declared type for <paramref name="target"/>, without
    /// reading an inventory (the fingerprint is the id).</summary>
    public static ResolvedTarget Of(Capability capability, MutationTarget target)
    {
        var record = target with { Resolved = true };
        var token = Token;
        var type = capability.TargetType;
        if (type == typeof(ResolvedNew) || type == typeof(ResolvedTarget)) return new ResolvedNew(token, record);
        if (type == typeof(ResolvedRecord))
            return new ResolvedRecord(token, record, record.Id, capability.TargetKind, record.Id);
        if (type == typeof(ResolvedVolume)) return new ResolvedVolume(token, record, record.Id, "local");
        if (type == typeof(ResolvedVolumeBackup))
            return new ResolvedVolumeBackup(token, record, record.Id, record.Name ?? "", "", null, false);
        if (type == typeof(ResolvedLogAlertRule)) return new ResolvedLogAlertRule(token, record, record.Id);
        return (ResolvedTarget)DefaultObject.Invoke(null, [capability, record])!;
    }

    /// <summary>The object of <paramref name="capabilityId"/> on the target its <paramref name="arguments"/> name, as
    /// <typeparamref name="TTarget"/> (a runner double hands it to a typed lambda).</summary>
    public static TTarget Of<TTarget>(string capabilityId, object arguments) where TTarget : ResolvedTarget
    {
        var capability = Catalog.Find(capabilityId) ?? throw new ArgumentException($"Unknown capability {capabilityId}.");
        var args = UiMutationRunner.ToArguments(arguments);
        var built = Of(capability, Named(MutationTargets.FromArguments(capability, args), args));
        return built as TTarget ?? throw new InvalidOperationException(
            $"'{capabilityId}' resolves to a {built.GetType().Name}, not a {typeof(TTarget).Name}.");
    }

    /// <summary>The name the call gives its target (<c>name</c>), as the inventory would answer it.</summary>
    private static MutationTarget Named(MutationTarget target, IReadOnlyDictionary<string, System.Text.Json.JsonElement> args) =>
        target.Name is null && MutationTargets.String(args, "name") is { } name ? target with { Name = name } : target;

    /// <summary>A resolver that reads no inventory: the target as named (or as <paramref name="inner"/> resolved it)
    /// with its typed object; the confirmation always holds.</summary>
    public static IMutationTargetResolver Resolver(IMutationTargetResolver? inner = null) => new PassThrough(inner);

    private sealed class PassThrough(IMutationTargetResolver? inner) : IMutationTargetResolver
    {
        public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
        {
            var resolution = inner is null ? new TargetResolution(Named(request.Target, request.Arguments), null) : await inner.ResolveAsync(request, capability, ct);
            return resolution.Target is { } target && resolution.Object is null
                ? resolution with { Object = Of(capability, target) }
                : resolution;
        }

        public Task<TargetResolution> ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target,
            CancellationToken ct) => Task.FromResult(new TargetResolution(target.Record, null) { Object = target });
    }
}
