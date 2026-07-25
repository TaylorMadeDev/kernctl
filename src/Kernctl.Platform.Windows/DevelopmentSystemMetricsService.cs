using Kernctl.Core.Models;
using Kernctl.Core.Services;

namespace Kernctl.Platform.Windows;

/// <summary>
/// Returns explicitly labelled sample values until a reviewed read-only Windows
/// metrics provider is introduced.
/// </summary>
public sealed class DevelopmentSystemMetricsService : ISystemMetricsService
{
    public ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new SystemMetricsSnapshot(18, 42, "Balanced", true, DateTimeOffset.UtcNow));
    }
}
