using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerWatch.Services.Tailscale;

public class TailscaleDevice
{
    public string Hostname { get; set; } = "";
    public string TailscaleIP { get; set; } = "";
    public string OS { get; set; } = "";
    public bool Online { get; set; }
    public bool Self { get; set; }
}

public class TailscaleStatus
{
    public bool Running { get; set; }
    public bool Authenticated { get; set; }
    public string? OwnIP { get; set; }
    public string? Hostname { get; set; }
    public string? TailnetName { get; set; }
    public List<TailscaleDevice> Devices { get; set; } = new();
}

public class TailscaleService
{
    private readonly ILogger<TailscaleService> _logger;

    public TailscaleService(ILogger<TailscaleService> logger)
    {
        _logger = logger;
    }

    /// <summary>Check if tailscale CLI is available in the container.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        var result = await RunAsync("tailscale", "version");
        return result.ExitCode == 0;
    }

    /// <summary>Get full Tailscale status including device list.</summary>
    public async Task<TailscaleStatus> GetStatusAsync()
    {
        var status = new TailscaleStatus();

        // Check if daemon is running
        var pingResult = await RunAsync("tailscale", "status --json");
        if (pingResult.ExitCode != 0)
        {
            status.Running = false;
            return status;
        }

        status.Running = true;

        try
        {
            using var doc = JsonDocument.Parse(pingResult.Output);
            var root = doc.RootElement;

            // BackendState
            if (root.TryGetProperty("BackendState", out var backendState))
                status.Authenticated = backendState.GetString() == "Running";

            // Self node
            if (root.TryGetProperty("Self", out var self))
            {
                if (self.TryGetProperty("HostName", out var hn))
                    status.Hostname = hn.GetString();
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0)
                    status.OwnIP = ips[0].GetString();
            }

            // Current Tailnet
            if (root.TryGetProperty("CurrentTailnet", out var tailnet))
            {
                if (tailnet.TryGetProperty("Name", out var name))
                    status.TailnetName = name.GetString();
            }

            // Peer nodes
            if (root.TryGetProperty("Peer", out var peers))
            {
                foreach (var peer in peers.EnumerateObject())
                {
                    var peerObj = peer.Value;
                    var device = new TailscaleDevice();

                    if (peerObj.TryGetProperty("HostName", out var phn))
                        device.Hostname = phn.GetString() ?? "";
                    if (peerObj.TryGetProperty("OS", out var os))
                        device.OS = os.GetString() ?? "";
                    if (peerObj.TryGetProperty("Online", out var online))
                        device.Online = online.GetBoolean();
                    if (peerObj.TryGetProperty("TailscaleIPs", out var pIps) && pIps.GetArrayLength() > 0)
                        device.TailscaleIP = pIps[0].GetString() ?? "";

                    status.Devices.Add(device);
                }
            }

            // Add self as device
            status.Devices.Insert(0, new TailscaleDevice
            {
                Hostname = status.Hostname ?? "self",
                TailscaleIP = status.OwnIP ?? "",
                Online = true,
                Self = true,
                OS = "linux"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse tailscale status JSON");
        }

        return status;
    }

    /// <summary>Connect to Tailscale with an auth key.</summary>
    public async Task<(bool Success, string Message)> ConnectAsync(string authKey, string? hostname = null)
    {
        var args = $"up --authkey={authKey} --accept-routes";
        if (!string.IsNullOrEmpty(hostname))
            args += $" --hostname={hostname}";

        var result = await RunAsync("tailscale", args, timeoutSeconds: 30);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Tailscale connected successfully");
            return (true, "Verbunden mit Tailscale.");
        }

        _logger.LogWarning("Tailscale connect failed: {Error}", result.Error);
        return (false, $"Verbindung fehlgeschlagen: {result.Error}");
    }

    /// <summary>Disconnect from Tailscale.</summary>
    public async Task<(bool Success, string Message)> DisconnectAsync()
    {
        var result = await RunAsync("tailscale", "down");
        return result.ExitCode == 0
            ? (true, "Tailscale getrennt.")
            : (false, $"Fehler: {result.Error}");
    }

    /// <summary>Get list of devices in the Tailnet (online peers).</summary>
    public async Task<List<TailscaleDevice>> GetDevicesAsync()
    {
        var status = await GetStatusAsync();
        return status.Devices.Where(d => !d.Self).ToList();
    }

    /// <summary>Ping a Tailscale IP to verify connectivity.</summary>
    public async Task<bool> PingAsync(string tailscaleIP)
    {
        var result = await RunAsync("tailscale", $"ping --c 1 --timeout 5s {tailscaleIP}", timeoutSeconds: 10);
        return result.ExitCode == 0;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string fileName, string arguments, int timeoutSeconds = 10)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
