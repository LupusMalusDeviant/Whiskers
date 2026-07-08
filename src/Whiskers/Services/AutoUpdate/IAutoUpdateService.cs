using Whiskers.Models;

namespace Whiskers.Services.AutoUpdate;

/// <summary>Background auto-update of container images; exposes policy + history for the UI.</summary>
public interface IAutoUpdateService
{
    Task<List<UpdatePolicyEntity>> GetPoliciesAsync();
    Task SetPolicyAsync(UpdatePolicyEntity policy);
    Task<List<UpdateHistoryEntity>> GetHistoryAsync(string? containerId = null, int limit = 20);
}
