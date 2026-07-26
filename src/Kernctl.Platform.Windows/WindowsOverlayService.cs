using System.Collections.Immutable;
using System.Diagnostics;
using Kernctl.Core.Gaming;

namespace Kernctl.Platform.Windows;

public sealed class WindowsOverlayService(
    IGameProcessService processService) : IOverlayService
{
    private static readonly ImmutableArray<KnownOverlay> KnownOverlays =
    [
        new(
            "steam",
            "Steam Overlay",
            ["steam"],
            [OverlayCapability.Overlay, OverlayCapability.Communication]),
        new(
            "discord",
            "Discord",
            ["Discord"],
            [OverlayCapability.Overlay, OverlayCapability.Communication]),
        new(
            "xbox-game-bar",
            "Xbox Game Bar",
            ["GameBar", "GameBarFTServer"],
            [OverlayCapability.Overlay, OverlayCapability.Recording, OverlayCapability.Performance]),
        new(
            "nvidia-app",
            "NVIDIA App",
            ["NVIDIA Overlay", "NVIDIA Share"],
            [OverlayCapability.Overlay, OverlayCapability.Recording, OverlayCapability.Performance]),
        new(
            "amd-software",
            "AMD Software",
            ["RadeonSoftware"],
            [OverlayCapability.Overlay, OverlayCapability.Recording, OverlayCapability.Performance]),
    ];

    public Task<IReadOnlyList<OverlayApplication>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcesses();
        try
        {
            var results = KnownOverlays.Select(overlay => Inspect(overlay, processes)).ToArray();
            return Task.FromResult<IReadOnlyList<OverlayApplication>>(results);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<GameProcessOperationResult> OpenAsync(
        string overlayId,
        CancellationToken cancellationToken = default)
    {
        var overlay = (await InspectAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == overlayId);
        if (overlay?.ExecutablePath is null)
        {
            return GameProcessOperationResult.Failure(
                "No verified local executable path is available for this application.");
        }

        var validation = GameValidation.ValidateLaunch(
            overlay.ExecutablePath,
            Path.GetDirectoryName(overlay.ExecutablePath),
            []);
        if (!validation.IsValid)
        {
            return GameProcessOperationResult.Failure(validation.Errors[0]);
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = validation.NormalizedExecutablePath!,
                WorkingDirectory = validation.NormalizedWorkingDirectory!,
                UseShellExecute = false,
            });
            process?.Dispose();
            return process is null
                ? GameProcessOperationResult.Failure("Windows did not open the application.")
                : GameProcessOperationResult.Success("Opened the application directly.");
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            return GameProcessOperationResult.Failure(
                $"Windows could not open the application: {exception.Message}");
        }
    }

    public async Task<GameProcessOperationResult> RequestCloseAsync(
        string overlayId,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyConfirmed)
        {
            return GameProcessOperationResult.Failure(
                "Explicit confirmation is required before asking an overlay application to exit.");
        }

        var overlay = (await InspectAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == overlayId);
        if (overlay?.Process is null)
        {
            return GameProcessOperationResult.Failure("The application is not running.");
        }

        return await processService.RequestCloseAsync(overlay.Process, cancellationToken);
    }

    private static OverlayApplication Inspect(KnownOverlay overlay, Process[] processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!overlay.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    || process.HasExited)
                {
                    continue;
                }

                var path = process.MainModule?.FileName;
                var publisher = path is null
                    ? null
                    : FileVersionInfo.GetVersionInfo(path).CompanyName;
                return new(
                    overlay.Id,
                    overlay.Name,
                    IsRunning: true,
                    overlay.Capabilities,
                    publisher,
                    path,
                    new(
                        process.Id,
                        new DateTimeOffset(process.StartTime.ToUniversalTime()),
                        path ?? string.Empty),
                    "Running");
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
            {
                // Protected and exiting processes are represented as not inspectable.
            }
        }

        return new(
            overlay.Id,
            overlay.Name,
            IsRunning: false,
            overlay.Capabilities,
            PublisherMetadata: null,
            ExecutablePath: null,
            Process: null,
            "Not running");
    }

    private sealed record KnownOverlay(
        string Id,
        string Name,
        ImmutableArray<string> ProcessNames,
        ImmutableArray<OverlayCapability> Capabilities);
}
