# Disk health (SMART, md RAID, ZFS)

Whiskers can show whether each host's disks and arrays are healthy and alert when one fails (R1-58, contract
[0005](../contracts/0005-disk-health-readings.md)). It reads this **only** from the host's existing metrics endpoint —
node_exporter scraped into VictoriaMetrics (`ServerConfig.MetricsEndpoint`). Whiskers never runs `smartctl` or anything
else on a host and has no write path there. The SMART job on the host is yours; this guide gives you one.

The feature is **off by default**. An Admin switches it on in **Settings → Disk health (SMART, RAID, ZFS)**; the switch is
a gated, audited configuration change.

## What is read

| Source | Needs on the host | Freshness evidence |
|---|---|---|
| SMART | `smartmontools` and the timer below, writing `smartmon.prom` for node_exporter's textfile collector | `smartmon_smartctl_run` (the run's time) **and** the file's modification time (`node_textfile_mtime_seconds`), both within **2 hours** |
| md RAID | nothing — node_exporter's built-in `mdadm` collector | the collector reports success in the same query |
| ZFS pools | nothing — node_exporter's built-in `zfs` collector | the collector reports success in the same query |

The query's own sample time never counts as fresh: node_exporter keeps serving an old `smartmon.prom` with a new sample
time every scrape. If the timer stops, SMART turns **Unknown** within 2 hours.

Hosts onboarded by Whiskers, and the template in `deploy/telemetry/host/docker-compose.yml`, start node_exporter with
`--collector.textfile.directory=/host/var/lib/node_exporter/textfile_collector`. The directory is reached through the
existing read-only `/:/host` mount, so no new mount is needed. A host set up before this change needs the flag added
and node_exporter recreated (`docker compose up -d` in its telemetry directory).

## Setting up SMART on a host

Run as root on each host with SMART-capable disks. Skip hosts without any, such as most VMs: md and ZFS need nothing.

### 1. Install smartmontools and the directory

```sh
apt-get install -y smartmontools          # Debian/Ubuntu; dnf install smartmontools on Fedora/RHEL
install -d -m 0755 /var/lib/node_exporter/textfile_collector
```

### 2. The script

Save as `/usr/local/bin/whiskers-smartmon.sh` and make it executable (`chmod 0755`). It is a plain POSIX shell script. It
writes the metric names of prometheus-community's `smartmon.sh` and replaces `smartmon.prom` atomically: a temporary
file in the same directory, renamed over the old one. node_exporter therefore never reads a half-written file. It only
reads from the disks (`smartctl -i`, `smartctl -H`).

```sh
#!/bin/sh
# Writes SMART health for node_exporter's textfile collector, in smartmon.sh's metric format.
set -eu

DIR=/var/lib/node_exporter/textfile_collector
OUT="$DIR/smartmon.prom"
BODY="$(mktemp)"
TMP="$(mktemp "$DIR/.smartmon.XXXXXX")"     # same directory: the rename below is atomic; no .prom suffix while written
trap 'rm -f "$BODY" "$TMP"' EXIT

smartctl --scan-open 2>/dev/null | while read -r dev flag type rest; do
    case "$dev" in ''|'#'*) continue ;; esac
    [ "$flag" = "-d" ] || type=auto
    labels="disk=\"$dev\",type=\"$type\""

    info="$(smartctl -i -d "$type" "$dev" 2>/dev/null || true)"
    available=0; enabled=0
    printf '%s\n' "$info" | grep -Eq 'SMART support is:[[:space:]]+Available' && available=1
    printf '%s\n' "$info" | grep -Eq 'SMART support is:[[:space:]]+Enabled' && enabled=1
    case "$type" in nvme*) available=1; enabled=1 ;; esac     # NVMe always reports its health

    echo "smartmon_device_active{$labels} 1" >> "$BODY"
    echo "smartmon_device_smart_available{$labels} $available" >> "$BODY"
    echo "smartmon_device_smart_enabled{$labels} $enabled" >> "$BODY"

    if [ "$available" = 1 ] && [ "$enabled" = 1 ]; then
        health="$(smartctl -H -d "$type" "$dev" 2>/dev/null || true)"
        # Only a clear answer is written: no line at all reads Unknown in Whiskers, never failed or healthy.
        if printf '%s\n' "$health" | grep -Eq 'test result: PASSED|SMART Health Status: OK'; then
            echo "smartmon_device_smart_healthy{$labels} 1" >> "$BODY"
        elif printf '%s\n' "$health" | grep -Eq 'test result: FAILED|SMART Health Status: FAILURE'; then
            echo "smartmon_device_smart_healthy{$labels} 0" >> "$BODY"
        fi
    fi
    echo "smartmon_smartctl_run{$labels} $(date +%s)" >> "$BODY"
done

# Every metric as one group with its TYPE line, as the text format wants it.
LC_ALL=C sort "$BODY" | awk '{ n = $0; sub(/[{ ].*/, "", n); if (n != last) { print "# TYPE " n " gauge"; last = n } print }' > "$TMP"
chmod 0644 "$TMP"
mv -f "$TMP" "$OUT"
```

### 3. Every 15 minutes through a systemd timer

`/etc/systemd/system/whiskers-smartmon.service`:

```ini
[Unit]
Description=Write SMART health for node_exporter (Whiskers disk health)

[Service]
Type=oneshot
ExecStart=/usr/local/bin/whiskers-smartmon.sh
Nice=10
IOSchedulingClass=idle
# A smartctl hung on a dying disk must not block the timer for good.
TimeoutStartSec=10min
```

`/etc/systemd/system/whiskers-smartmon.timer`:

```ini
[Unit]
Description=Write SMART health every 15 minutes

[Timer]
OnCalendar=*:0/15
Persistent=true
AccuracySec=1min

[Install]
WantedBy=timers.target
```

```sh
systemctl daemon-reload
systemctl enable --now whiskers-smartmon.timer
systemctl start whiskers-smartmon.service              # first run now
cat /var/lib/node_exporter/textfile_collector/smartmon.prom
```

### 4. Check it arrives

From the controller, ask VictoriaMetrics (replace the id with the server's Whiskers id):

```sh
curl -s 'http://<controller-mesh-ip>:8428/api/v1/query' --data-urlencode 'query=smartmon_device_smart_healthy{server="<id>"}'
curl -s 'http://<controller-mesh-ip>:8428/api/v1/query' --data-urlencode 'query=node_textfile_mtime_seconds{server="<id>"}'
```

Within 5 minutes the server's expanded panel on the dashboard shows a **Disks** block.

## What the states mean

| State | Meaning |
|---|---|
| **Healthy** | Every disk, array and pool reads good, and the SMART data is less than 2 hours old. |
| **Degraded / failing** | A disk with SMART available and enabled reports overall-health failed, an md array has a failed disk or fewer active disks than it needs, or a ZFS pool is not `online`. A bad value counts even when its data is old. |
| **Unknown** | Whiskers cannot vouch for it. The query failed, was malformed or could not be read to its end; a disk, array or pool reported before is missing now; SMART data is older than 2 hours or has no run time; a collector did not succeed (every source carrying the server's label must report success); SMART is not available, not enabled or its availability is not reported (a health value from such a disk is no verdict); an md array is inactive; two sources (instance/job) with the same server label disagree about one device; one disk, array or pool is reported more than once under the same name (for example `/dev/sda` with two `type` labels) — one line is shown, failing if any copy reads failing, Unknown otherwise. It is never shown as healthy. |
| **Not supported** | The host never had a reading: it has no metrics endpoint (a Docker-source server), or none of SMART, md and ZFS report anything. A host whose first read failed says "Not read yet" with the reason instead. |

The dashboard block says when the reading was made. A reading older than two cycles and a minute (11 minutes) is shown
as Unknown: Whiskers has not read the host since, so it vouches for nothing there. Hosts are read one after another, each
query waiting up to 10 seconds; in a large fleet with many unreachable endpoints a cycle can take long enough that healthy
hosts briefly show as Unknown until their next read. That fails closed and raises no alert by itself.

A disk, array or pool that disappears stays **Unknown** ("no longer reported"), also across a restart of Whiskers. It
might have died, so Whiskers keeps showing it. The same holds for a device that was **renamed**: an md array that comes
back as `md127` instead of `md0`, a `/dev/sdX` name that shifted after a disk was pulled — the old name stays Unknown
(and `disk_health_unknown` open) while the new one is read normally. The same goes for a server whose metrics source
changed from Prometheus to Docker after a reading: its items stay Unknown ("no metrics endpoint"). To forget what is gone:

- **One host:** disable the server in Whiskers (its *Enabled* setting off), wait for one disk-health cycle (up to 5 minutes),
  then enable it again. The cycle closes that host's open disk-health alerts as "recovery not confirmed" and drops its
  last reading; the next cycle reads it afresh. Other hosts are untouched.
- **All hosts:** switch disk health off in Settings. That clears all last readings at once and closes every host's open
  disk-health alerts as "recovery not confirmed". Then switch it on again; the next cycle (within 5 minutes) reads every
  host afresh.
- **The setting comes from the environment** (`DiskHealth__Enabled`, the switch is read-only): use the per-host way above,
  or remove the variable (the switch is then yours again), or stop Whiskers, delete `disk-health.json` in its data
  directory and start it again. Without that file Whiskers starts with no last readings; an alert still going on is
  raised again rather than lost.

A per-host "forget missing items" action does not exist yet; it needs a change of contract 0005.

## Alerts

| Alert | Raised when | Cleared when |
|---|---|---|
| `disk_failing` | a SMART disk reads failing for 10 minutes | every SMART disk reads healthy, with fresh data, for 10 minutes |
| `array_degraded` | an md array or ZFS pool reads degraded for 10 minutes | every array and pool reads healthy for 10 minutes |
| `disk_health_unknown` | a disk, array or pool that read Healthy or Degraded / failing reads Unknown (is lost), counted from the first reading with a lost item; after 10 minutes the next reading with a lost item raises it. A single reading in between in which nothing is lost does not restart the count, as for the other breach alerts | no item is lost any more, for 10 minutes |

Rules that keep the alerts honest:

- Unknown and Not supported never clear an alert. A failing disk stays alerted until a fresh, good reading.
- For `disk_failing` and `array_degraded`, the 10 minutes are 10 minutes without an Unknown reading in between: a
  reading that cannot be judged restarts the count, both for a raise and for a clear.
- `disk_health_unknown` is armed per disk, array and pool. A host that is Unknown for a harmless reason (a USB disk
  without SMART) still alerts when its other, good readings are lost. An item that was never read good or bad (Unknown
  from the start) raises nothing.
- Not supported never alerts.
- Quiet hours never hold these alerts or their recoveries.
- Switching the feature off (at once, not at the next cycle), or removing a server, closes its open alerts with
  "recovery not confirmed".

The setting can also come from the environment (`DiskHealth__Enabled=true`) or `appsettings.json`. That value wins, and
Settings shows it read-only with its source.
