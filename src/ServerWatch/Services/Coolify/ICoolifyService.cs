using ServerWatch.Models.Coolify;

namespace ServerWatch.Services.Coolify;

public interface ICoolifyService
{
    /// <summary>Tests the connection to the Coolify instance.</summary>
    Task<bool> TestConnectionAsync();

    // Applications
    Task<List<CoolifyApplication>> ListApplicationsAsync();
    Task<CoolifyApplication?> GetApplicationAsync(string uuid);
    Task<CoolifyDeployment> DeployApplicationAsync(string uuid, bool force = false);
    Task StartApplicationAsync(string uuid);
    Task StopApplicationAsync(string uuid);
    Task RestartApplicationAsync(string uuid);
    Task<string> GetApplicationLogsAsync(string uuid);

    // Environment Variables
    Task<List<CoolifyEnvironmentVar>> GetEnvVarsAsync(string appUuid);
    Task SetEnvVarAsync(string appUuid, string key, string value, bool isBuildTime = false);

    // Servers
    Task<List<CoolifyServer>> ListServersAsync();
    Task<CoolifyServer?> GetServerAsync(string uuid);

    // Databases
    Task<List<CoolifyDatabase>> ListDatabasesAsync();
    Task StartDatabaseAsync(string uuid);
    Task StopDatabaseAsync(string uuid);

    // Batch Deploy
    Task<List<CoolifyDeployment>> DeployByTagAsync(string tag, bool force = false);
}
