using System.Collections.Immutable;

namespace Kernctl.Core.Gaming;

public enum OverlayCapability
{
    Overlay,
    Recording,
    Performance,
    Communication,
}

public sealed record OverlayApplication(
    string Id,
    string Name,
    bool IsRunning,
    ImmutableArray<OverlayCapability> Capabilities,
    string? PublisherMetadata,
    string? ExecutablePath,
    GameProcessReference? Process,
    string Status);

public interface IOverlayService
{
    Task<IReadOnlyList<OverlayApplication>> InspectAsync(
        CancellationToken cancellationToken = default);

    Task<GameProcessOperationResult> OpenAsync(
        string overlayId,
        CancellationToken cancellationToken = default);

    Task<GameProcessOperationResult> RequestCloseAsync(
        string overlayId,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken = default);
}
