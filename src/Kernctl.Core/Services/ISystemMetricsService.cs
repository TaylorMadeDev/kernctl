using Kernctl.Core.Models;

namespace Kernctl.Core.Services;

/// <summary>Provides harmless, read-only system metric snapshots.</summary>
public interface ISystemMetricsService
{
    ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
