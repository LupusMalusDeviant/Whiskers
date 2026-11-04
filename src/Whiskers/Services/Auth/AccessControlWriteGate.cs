namespace Whiskers.Services.Auth;

/// <summary>
/// The one write boundary of the access-control stores (R1-48, review round 2): every write to the roles and to the
/// whitelist — the ordinary ones the gateway decides and the first-run wizard's first-admin writes — runs while holding
/// it. The wizard holds it across its whole sequence (check, whitelist entry, Admin role), so no other writer can
/// establish an Admin or change the whitelist between the check and the writes, or between the two writes.
/// Readers never take it. A singleton in the app; one instance must be shared by <see cref="RoleService"/>,
/// <see cref="WhitelistService"/> and the setup wizard.
/// </summary>
public sealed class AccessControlWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the boundary and returns the lease that holds it until it is disposed.</summary>
    public async Task<Lease> EnterAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new Lease(this);
    }

    /// <summary>A held boundary. The first-admin writes take it as proof that their caller holds this gate.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly AccessControlWriteGate _gate;
        private int _released;

        internal Lease(AccessControlWriteGate gate) => _gate = gate;

        /// <summary>True while this lease holds <paramref name="gate"/>.</summary>
        public bool Holds(AccessControlWriteGate gate) => Volatile.Read(ref _released) == 0 && ReferenceEquals(gate, _gate);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _gate._semaphore.Release();
        }
    }

    /// <summary>Throws unless <paramref name="lease"/> holds this gate (fail closed).</summary>
    internal void Demand(Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.Holds(this))
            throw new InvalidOperationException("The first-admin write requires the held access-control write boundary.");
    }
}
