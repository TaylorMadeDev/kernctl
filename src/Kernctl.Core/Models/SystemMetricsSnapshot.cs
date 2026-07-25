namespace Kernctl.Core.Models;

/// <summary>Represents a read-only point-in-time system metrics result.</summary>
public sealed record SystemMetricsSnapshot(
    int CpuPercent,
    int MemoryPercent,
    string PowerState,
    bool IsSample,
    DateTimeOffset CapturedAt);
