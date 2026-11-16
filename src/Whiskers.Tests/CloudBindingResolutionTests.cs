namespace Whiskers.Tests;

/// <summary>
/// Contract 0018, INV-57: the cloud bootstrap acts on the provider VM bound to the record (<c>CloudServerId</c>) and
/// still holding its IP — never on a VM found by name, never on a VM bound to a second record. Further cases of the
/// same invariant: <c>HetznerRescueBootstrapTests.Vm_with_another_ip_is_refused</c> and, for the target resolver,
/// <c>CloudBootstrapRuleTests.Unreadable_vm_refuses_without_raising_an_approval</c> and
/// <c>CloudBootstrapRuleTests.A_readable_vm_raises_an_approval_naming_it</c>.
/// </summary>
public sealed class CloudBindingResolutionTests : IDisposable
{
    private readonly RescueBootstrapRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Every_provider_call_names_the_bound_vm_id()
    {
        var result = await _rig.RunAsync(await _rig.RecordAsync());

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(_rig.VmCalls);
        Assert.All(_rig.VmCalls, c => Assert.Equal(RescueBootstrapRig.VmId, c.Id));
    }

    [Fact]
    public async Task A_record_without_a_bound_vm_is_refused_before_any_provider_call()
    {
        var result = await _rig.RunAsync(await _rig.RecordAsync(vmId: null));

        Assert.False(result.Success);
        Assert.Empty(_rig.Cloud.Calls);
        Assert.Empty(_rig.Ssh.GeneratedIn);
    }

    [Fact]
    public async Task A_vm_bound_to_a_second_record_is_refused()
    {
        await _rig.RecordAsync(id: "web-old");
        var second = await _rig.RecordAsync(id: "web-new");

        var result = await _rig.RunAsync(second);

        Assert.False(result.Success);
        Assert.Empty(_rig.Mutations());
    }

    [Fact]
    public async Task A_bound_id_the_provider_no_longer_knows_is_refused()
    {
        var result = await _rig.RunAsync(await _rig.RecordAsync(vmId: RescueBootstrapRig.VmId + 1));

        Assert.False(result.Success);
        Assert.Empty(_rig.Mutations());
        Assert.Equal([("get", RescueBootstrapRig.VmId + 1)], _rig.VmCalls);
    }
}
