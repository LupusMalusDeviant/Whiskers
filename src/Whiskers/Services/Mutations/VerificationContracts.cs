namespace Whiskers.Services.Mutations;

/// <summary>How a capability's effect is checked after the call returned (R1-37, INV-43).</summary>
public enum VerificationKind
{
    /// <summary>An <see cref="IMutationVerifier"/> reads the effect back; a missing or failed read-back is not success.</summary>
    ReadBack,

    /// <summary>The effect cannot be read back; every executed call records <see cref="VerificationStatus.NotVerifiable"/>
    /// with <see cref="VerificationContract.Reason"/>.</summary>
    NotVerifiable,
}

/// <summary>A capability's declared verification: a read-back, or why there is none.</summary>
public sealed record VerificationContract(VerificationKind Kind, string Reason)
{
    public static VerificationContract ReadBack(string what) => new(VerificationKind.ReadBack, what);
    public static VerificationContract None(string why) => new(VerificationKind.NotVerifiable, why);
}

/// <summary>
/// The verification contract of every catalog capability (R1-37). A <see cref="VerificationKind.ReadBack"/> row needs a
/// registered verifier (checked by <c>VerificationOutcomeTests</c>); a <see cref="VerificationKind.NotVerifiable"/> row
/// says why, and that reason is what the gateway records. A capability without a row — or a read-back row whose verifier
/// is missing — is recorded as not verifiable with a reason saying so: the absence of a check is never a success.
/// </summary>
public static class VerificationContracts
{
    private const string OwnRecord =
        "Whiskers' own configuration or record: the write is checked by its store, there is no external effect to read back.";
    private const string NestedOnly =
        "never decided under its own id: it runs only inside another capability, whose verification covers it.";

    public static IReadOnlyDictionary<string, VerificationContract> All { get; } =
        new Dictionary<string, VerificationContract>(StringComparer.Ordinal)
        {
            // Containers and images (R1-36)
            ["container.start"] = VerificationContract.ReadBack("the container runs, or a one-shot container exited 0."),
            ["container.stop"] = VerificationContract.ReadBack("the container no longer runs."),
            ["container.restart"] = VerificationContract.ReadBack("the container runs again."),
            ["container.update"] = VerificationContract.ReadBack("the recreated container runs."),
            ["container.remove"] = VerificationContract.ReadBack("the container is gone."),
            ["container.create"] = VerificationContract.ReadBack("the container the call returned runs."),
            ["container.rollback"] = VerificationContract.ReadBack("the container of the snapshot runs again."),
            ["container.env_set"] = VerificationContract.None(
                "compose recreates the container from the .env; the new variables are read by its process, not by Whiskers."),
            ["container.env_file_save"] = VerificationContract.None(
                "compose recreates the container from the .env; the new variables are read by its process, not by Whiskers."),
            ["container.exec"] = VerificationContract.None("an arbitrary command inside the container: no semantic read-back."),
            ["terminal.container_open"] = VerificationContract.None("an interactive shell: what is typed is not recorded."),
            ["terminal.host_open"] = VerificationContract.None("an interactive shell: what is typed is not recorded."),
            ["image.pull"] = VerificationContract.None(NestedOnly),
            ["image.assess_update"] = VerificationContract.None(
                "an assessment: the pulled candidate is compared with the running image, and the report is the result."),
            ["workload.start"] = VerificationContract.None(
                "Kubernetes scales the controller asynchronously; its pods are replaced, not read back by id."),
            ["workload.stop"] = VerificationContract.None(
                "Kubernetes scales the controller asynchronously; its pods are replaced, not read back by id."),
            ["workload.restart"] = VerificationContract.None(
                "a rollout restart replaces the pods; the old pod id is gone by design."),

            // Deployments
            ["app.deploy"] = VerificationContract.ReadBack("the container the deployment returned runs."),
            ["compose.deploy"] = VerificationContract.ReadBack("every container of the compose project runs."),
            ["gitdeploy.save"] = VerificationContract.None(OwnRecord),
            ["gitdeploy.set_token"] = VerificationContract.None(OwnRecord),
            ["gitdeploy.delete"] = VerificationContract.None(OwnRecord),
            ["gitdeploy.deploy"] = VerificationContract.None(
                "the repository decides what is built and started; the build output is recorded, not read back."),
            ["webhook.save"] = VerificationContract.None(OwnRecord),
            ["webhook.rotate_secret"] = VerificationContract.None(OwnRecord),
            ["webhook.set_enabled"] = VerificationContract.None(OwnRecord),
            ["webhook.delete"] = VerificationContract.None(OwnRecord),

            // Networks
            ["network.create"] = VerificationContract.ReadBack("the network is listed with the requested driver."),
            ["network.remove"] = VerificationContract.ReadBack("the network is no longer listed."),
            ["network.connect"] = VerificationContract.ReadBack("the container is attached to the network."),
            ["network.disconnect"] = VerificationContract.ReadBack("the container is no longer attached to the network."),

            // Hosts
            ["host.exec"] = VerificationContract.None("an arbitrary shell command: no semantic read-back (its exit code decides)."),
            ["host.prune"] = VerificationContract.None(
                "docker prune removes whatever is unused at that moment; its output is recorded, not read back."),
            ["firewall.rule_add"] = VerificationContract.ReadBack("ufw lists a rule for the port, protocol and action."),
            ["firewall.rule_remove"] = VerificationContract.None(
                "ufw renumbers its rules after a delete, and the call names only the number: the removed rule cannot be identified afterwards."),
            ["firewall.set_status"] = VerificationContract.ReadBack("ufw reports the requested status."),
            ["nginx.update"] = VerificationContract.ReadBack("the site's file holds the written configuration."),
            ["nginx.reload"] = VerificationContract.ReadBack("nginx is active after the reload."),
            ["nginx.site_enable"] = VerificationContract.ReadBack("the site is linked in sites-enabled."),
            ["nginx.site_disable"] = VerificationContract.ReadBack("the site is no longer linked in sites-enabled."),
            ["systemd.manage"] = VerificationContract.ReadBack("systemctl reports the unit active, inactive, enabled or disabled as asked."),
            ["ssl.renew"] = VerificationContract.ReadBack(
                "the certificate exists and is valid beyond certbot's 30-day renewal window."),
            ["certificate.renew_mtls"] = VerificationContract.None(
                "the renewal itself confirms the presented certificate's thumbprint before it reports success."),
            ["certificate.issue_mtls"] = VerificationContract.None(NestedOnly),

            // Databases
            ["db.query"] = VerificationContract.None("an arbitrary SQL statement: no semantic read-back."),
            ["db.backup"] = VerificationContract.ReadBack("the dump file exists on the host and is not empty."),
            ["db.backup_delete"] = VerificationContract.ReadBack("no more than the kept number of dumps remain."),
            ["selfbackup.prune"] = VerificationContract.ReadBack("no more than the kept number of self-backups remain."),

            // DNS
            ["dns.record_set"] = VerificationContract.ReadBack("the provider lists the record with the requested value."),
            ["dns.record_delete"] = VerificationContract.ReadBack("the provider no longer lists the record."),

            // Servers and Whiskers itself
            ["server.onboard"] = VerificationContract.None(
                "the agent stack is installed by a script; the next health check shows whether the host reports."),
            ["server.save"] = VerificationContract.None(OwnRecord),
            ["server.remove"] = VerificationContract.None(OwnRecord),
            ["aitrigger.save"] = VerificationContract.None(OwnRecord),
            ["selfbackup.restore"] = VerificationContract.None(
                "staged only: the restore is applied by the next start, before anything opens the database."),
            ["selfbackup.create"] = VerificationContract.ReadBack("the archive is listed and on disk."),
            ["selfbackup.delete"] = VerificationContract.ReadBack("the archive is no longer on disk."),

            // Cloud servers
            ["cloud.power_on"] = VerificationContract.ReadBack("the provider reports the server running."),
            ["cloud.shutdown"] = VerificationContract.ReadBack("the provider reports the server off."),
            ["cloud.reboot"] = VerificationContract.None("a reboot ends in the state it started from: nothing tells it apart afterwards."),
            ["cloud.hard_reset"] = VerificationContract.None("a reset ends in the state it started from: nothing tells it apart afterwards."),
            ["cloud.snapshot_create"] = VerificationContract.None(
                "the provider creates the snapshot asynchronously; it is listed only when that finishes."),
            ["hetzner.rescue_enable"] = VerificationContract.ReadBack("Hetzner reports rescue mode on."),
            ["hetzner.rescue_disable"] = VerificationContract.ReadBack("Hetzner reports rescue mode off."),
            ["hetzner.backups_enable"] = VerificationContract.ReadBack("Hetzner reports a backup window."),
            ["hetzner.backups_disable"] = VerificationContract.ReadBack("Hetzner reports no backup window."),
            ["hetzner.server_type_change"] = VerificationContract.None(
                "the type changes when Hetzner's resize action finishes, minutes later; the action's status is checked when it is accepted."),
            ["hetzner.snapshot_delete"] = VerificationContract.ReadBack("Hetzner no longer finds the image."),

            // Automation and monitoring control
            ["scheduler.create"] = VerificationContract.None(OwnRecord),
            ["scheduler.update"] = VerificationContract.None(OwnRecord),
            ["scheduler.delete"] = VerificationContract.None(OwnRecord),
            ["scheduler.run"] = VerificationContract.None(
                "a run executes the task's own capability, which is verified under that capability."),
            ["logalert.create"] = VerificationContract.None(OwnRecord),
            ["logalert.delete"] = VerificationContract.None(OwnRecord),
            ["logalert.toggle"] = VerificationContract.None(OwnRecord),
            ["checks.pause"] = VerificationContract.None(OwnRecord),
            ["checks.resume"] = VerificationContract.None(OwnRecord),
            ["autopatch.preview"] = VerificationContract.None("a preview pulls images to compare digests; it changes no container."),
            ["autopatch.run"] = VerificationContract.None(
                "each container it patches is updated as container.update, which is verified."),
            ["autoupdate.policy_set"] = VerificationContract.None(OwnRecord),
            ["automation.owner_set"] = VerificationContract.None(OwnRecord),
            ["vpn.bootstrap"] = VerificationContract.None(
                "the VPN CLI's own exit code decides (a failure is recorded as failed); the mesh state is read live on the status page, not by the gateway."),

            // Volume backups
            ["volume.backup"] = VerificationContract.ReadBack("the archive is recorded and on disk, not empty."),
            ["volume.restore"] = VerificationContract.None(
                "the volume's content is the application's data: Whiskers cannot tell restored data from other data."),
            ["volume.backup_delete"] = VerificationContract.ReadBack("the archive is no longer recorded or on disk."),

            // Security configuration (R1-11)
            ["config.save"] = VerificationContract.None(OwnRecord),
            ["config.whitelist"] = VerificationContract.None(OwnRecord),
            ["config.roles"] = VerificationContract.None(OwnRecord),
            ["config.mcp_permissions"] = VerificationContract.None(OwnRecord),
            ["config.agent"] = VerificationContract.None(OwnRecord),
            ["mcp.key_create"] = VerificationContract.None(OwnRecord),
            ["mcp.key_delete"] = VerificationContract.None(OwnRecord),
            ["guardrails.save"] = VerificationContract.None(OwnRecord),
            ["guardrails.kill_switch_off"] = VerificationContract.None(OwnRecord),
            ["vault.secret_set"] = VerificationContract.None(OwnRecord),
            ["vault.secret_delete"] = VerificationContract.None(OwnRecord),
            ["registry.save"] = VerificationContract.None(OwnRecord),
            ["registry.delete"] = VerificationContract.None(OwnRecord),

            // Local accounts (R1-49)
            ["account.password_change"] = VerificationContract.None(OwnAccount),
            ["account.create"] = VerificationContract.None(OwnAccount),
            ["account.password_reset"] = VerificationContract.None(OwnAccount),
            ["account.delete"] = VerificationContract.None(OwnAccount),

            // Google/OIDC identity bindings (R1-27)
            ["identity.binding_confirm"] = VerificationContract.None(OwnRecord),
            ["identity.binding_remove"] = VerificationContract.None(OwnRecord),
        };

    private const string OwnAccount =
        "Whiskers' own account store: ASP.NET Identity writes the account in one save and reports its result; " +
        "there is no external effect to read back.";

    public static VerificationContract? Find(string capabilityId) => All.GetValueOrDefault(capabilityId);
}
