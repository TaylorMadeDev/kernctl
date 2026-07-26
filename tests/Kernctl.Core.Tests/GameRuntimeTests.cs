using Kernctl.Core.Actions;
using Kernctl.Core.Gaming;
using Kernctl.Core.Profiles;
using Kernctl.Platform.Windows;

#pragma warning disable xUnit1051 // The deterministic in-memory fakes do not perform cancellable waits.

namespace Kernctl.Core.Tests;

public sealed class GameRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-game-runtime-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task PriorityActionCapturesAppliesVerifiesAndRestoresOriginal()
    {
        var process = new FakeGameProcessService();
        var target = new GamePriorityTargetContext();
        var action = new GameProcessPrioritySystemAction(
            process,
            target,
            GameProcessPriority.High);
        using var fixture = new ActionEngineTestFixture(action);
        using (target.BeginSelection(process.Reference, GameProcessPriority.High))
        {
            var plan = await fixture.PlanAsync(default, action.Descriptor.Id);
            var result = await fixture.Engine.ExecuteAsync(plan);

            Assert.True(result.Succeeded);
            Assert.Equal(GameProcessPriority.High, process.Priority);
            var rollback = await fixture.Engine.RollbackAsync(result.TransactionId);
            Assert.Equal(TransactionState.RolledBack, rollback.FinalState);
        }

        Assert.Equal(GameProcessPriority.Normal, process.Priority);
    }

    [Fact]
    public async Task PriorityActionReportsAccessDeniedWithoutRetryLoop()
    {
        var process = new FakeGameProcessService { DenyPriorityChange = true };
        var target = new GamePriorityTargetContext();
        var action = new GameProcessPrioritySystemAction(
            process,
            target,
            GameProcessPriority.AboveNormal);
        using var fixture = new ActionEngineTestFixture(action);
        using (target.BeginSelection(process.Reference, GameProcessPriority.AboveNormal))
        {
            var plan = await fixture.PlanAsync(default, action.Descriptor.Id);
            var result = await fixture.Engine.ExecuteAsync(plan);

            Assert.False(result.Succeeded);
        }

        Assert.Equal(1, process.SetPriorityCount);
        Assert.Equal(GameProcessPriority.Normal, process.Priority);
    }

    [Fact]
    public void RealtimeAndUnknownPrioritiesAreRejected()
    {
        Assert.False(GameValidation.IsAllowedPriority((GameProcessPriority)999));
        Assert.Throws<InvalidDataException>(
            () => GamePriorityActionIds.For((GameProcessPriority)999));
    }

    [Fact]
    public async Task CoordinatorMonitorsChildAbstractionAndRestoresPriority()
    {
        var setup = await CreateCoordinatorAsync(automaticProfile: false);

        var session = await setup.Coordinator.LaunchAndMonitorAsync(setup.Game.Id);

        Assert.Equal(GameSessionOutcome.Completed, session.Outcome);
        Assert.Equal(1, setup.Monitor.CallCount);
        Assert.Equal(setup.Process.Reference.ProcessId, setup.Monitor.LastRoot?.ProcessId);
        Assert.Equal(setup.Game.Installation.ExecutablePath, setup.Monitor.LastRoot?.ExecutablePath);
        Assert.Equal(GameProcessPriority.Normal, setup.Process.Priority);
        Assert.Single(setup.Store.Snapshot.Sessions);
        setup.Dispose();
    }

    [Fact]
    public async Task CoordinatorReportsDeniedPriorityAndStillMonitorsSession()
    {
        var setup = await CreateCoordinatorAsync(automaticProfile: false);
        setup.Process.DenyPriorityChange = true;

        var session = await setup.Coordinator.LaunchAndMonitorAsync(setup.Game.Id);

        Assert.Equal(GameSessionOutcome.Completed, session.Outcome);
        Assert.Contains("priority", session.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, setup.Monitor.CallCount);
        setup.Dispose();
    }

    [Fact]
    public async Task CoordinatorRestoresProfileWhenMonitoringCrashes()
    {
        var setup = await CreateCoordinatorAsync(automaticProfile: true);
        setup.Monitor.ThrowOnMonitor = true;
        await setup.Library.SaveGameAsync(setup.Game with
        {
            Launch = setup.Game.Launch with { RestorePreviousProfileOnExit = false },
        });

        var session = await setup.Coordinator.LaunchAndMonitorAsync(setup.Game.Id);

        Assert.Equal(GameSessionOutcome.MonitoringFailed, session.Outcome);
        Assert.Equal(1, setup.ProfileEngine.ApplyCount);
        Assert.Equal(1, setup.ProfileEngine.RestoreCount);
        Assert.Equal(BuiltInProfiles.DefaultProfileId, setup.ProfileCatalog.ActiveProfile.Id);
        setup.Dispose();
    }

    [Fact]
    public async Task ShutdownCancelsMonitoringAndRestoresProfileBeforeCompleting()
    {
        var setup = await CreateCoordinatorAsync(automaticProfile: true);
        setup.Monitor.BlockUntilCancelled = true;
        var launch = setup.Coordinator.LaunchAndMonitorAsync(setup.Game.Id);
        await setup.Monitor.Entered.Task;

        await setup.Coordinator.ShutdownAsync();
        var session = await launch;

        Assert.Equal(GameSessionOutcome.Cancelled, session.Outcome);
        Assert.Equal(1, setup.ProfileEngine.RestoreCount);
        Assert.Equal(BuiltInProfiles.DefaultProfileId, setup.ProfileCatalog.ActiveProfile.Id);
        setup.Dispose();
    }

    [Fact]
    public async Task MissingExecutablePreventsLaunchAndStillRecordsRedactedOutcome()
    {
        var setup = await CreateCoordinatorAsync(automaticProfile: false);
        File.Delete(setup.Game.Installation.ExecutablePath!);

        var session = await setup.Coordinator.LaunchAndMonitorAsync(setup.Game.Id);

        Assert.Equal(GameSessionOutcome.LaunchFailed, session.Outcome);
        Assert.Equal(0, setup.Process.LaunchCount);
        Assert.Null(Assert.Single(setup.Store.Snapshot.Sessions).ProcessId);
        setup.Dispose();
    }

    [Fact]
    public async Task UnavailableFpsProviderReturnsNoFabricatedValue()
    {
        var provider = new UnavailableFpsProvider();
        var value = await provider.TryGetFramesPerSecondAsync(
            new(1, DateTimeOffset.UtcNow, string.Empty));

        Assert.False(provider.IsAvailable);
        Assert.Equal("FPS provider unavailable.", provider.Status);
        Assert.Null(value);
    }

    [Fact]
    public async Task OverlayExitRequiresExplicitConfirmation()
    {
        var process = new FakeGameProcessService();
        var service = new WindowsOverlayService(process);

        var result = await service.RequestCloseAsync(
            "discord",
            explicitlyConfirmed: false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, process.CloseCount);
    }

    [Fact]
    public async Task WindowsProcessServiceLaunchesHarmlessKernctlFixtureDirectly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is unavailable.");
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        for (var index = 0; index < 5; index++)
        {
            repositoryRoot = repositoryRoot.Parent
                ?? throw new InvalidOperationException("The repository root is unavailable.");
        }

        var executable = Path.Combine(
            repositoryRoot.FullName,
            "src",
            "Kernctl.Broker",
            "bin",
            configuration,
            "net10.0",
            "Kernctl.Broker.exe");
        Assert.True(File.Exists(executable), $"Harmless broker fixture was not built: {executable}");
        var service = new WindowsGameProcessService();

        var reference = await service.LaunchAsync(
            executable,
            Path.GetDirectoryName(executable)!,
            ["--game-test-smoke"]);
        var running = true;
        for (var attempt = 0; attempt < 100 && running; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            running = await service.IsRunningAsync(reference);
        }

        Assert.True(reference.ProcessId > 0);
        Assert.False(running);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<CoordinatorSetup> CreateCoordinatorAsync(bool automaticProfile)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, $"fixture-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(executable, []);
        var store = new GameLibraryTests.MemoryGameLibraryStore();
        var library = new GameLibraryService(store, []);
        await library.InitializeAsync();
        await library.SavePreferencesAsync(new()
        {
            AutomaticProfilesEnabled = automaticProfile,
        });
        var game = await library.AddManualAsync(
            executable,
            suspiciousLocationConfirmed: true);
        if (automaticProfile)
        {
            game = game with
            {
                Launch = game.Launch with
                {
                    AutoApplyProfile = true,
                    ProfileId = BuiltInProfiles.GamingId,
                },
            };
            await library.SaveGameAsync(game);
        }

        var process = new FakeGameProcessService();
        var monitor = new FakeTreeMonitor();
        var profileCatalog = new FakeProfileCatalog();
        var profileEngine = new FakeProfileEngine(profileCatalog);
        var target = new GamePriorityTargetContext();
        var priorityActions = new[]
        {
            new GameProcessPrioritySystemAction(process, target, GameProcessPriority.Normal),
            new GameProcessPrioritySystemAction(process, target, GameProcessPriority.AboveNormal),
            new GameProcessPrioritySystemAction(process, target, GameProcessPriority.High),
        };
        var fixture = new ActionEngineTestFixture(priorityActions);
        var coordinator = new GameSessionCoordinator(
            library,
            process,
            monitor,
            profileCatalog,
            profileEngine,
            fixture.Engine,
            target);
        return new(
            game,
            store,
            library,
            process,
            monitor,
            profileCatalog,
            profileEngine,
            fixture,
            coordinator);
    }

    private sealed class FakeGameProcessService : IGameProcessService
    {
        public GameProcessReference Reference { get; } =
            new(1234, DateTimeOffset.UtcNow, @"C:\fixture.exe");

        public GameProcessPriority Priority { get; private set; } = GameProcessPriority.Normal;

        public bool IsRunning { get; set; } = true;

        public bool DenyPriorityChange { get; set; }

        public int SetPriorityCount { get; private set; }

        public int LaunchCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task<GameProcessReference?> FindRunningAsync(
            string executablePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameProcessReference?>(null);

        public Task<GameProcessReference> LaunchAsync(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return Task.FromResult(Reference with { ExecutablePath = executablePath });
        }

        public Task<bool> IsRunningAsync(
            GameProcessReference process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IsRunning);

        public Task<GameProcessPriority?> GetPriorityAsync(
            GameProcessReference process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameProcessPriority?>(IsRunning ? Priority : null);

        public Task<GameProcessOperationResult> SetPriorityAsync(
            GameProcessReference process,
            GameProcessPriority priority,
            CancellationToken cancellationToken = default)
        {
            SetPriorityCount++;
            if (DenyPriorityChange)
            {
                return Task.FromResult(
                    GameProcessOperationResult.Failure("Access denied."));
            }

            Priority = priority;
            return Task.FromResult(GameProcessOperationResult.Success("Priority changed."));
        }

        public Task<GameProcessOperationResult> RequestCloseAsync(
            GameProcessReference process,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.FromResult(GameProcessOperationResult.Success("Close requested."));
        }
    }

    private sealed class FakeTreeMonitor : IGameProcessTreeMonitor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public GameProcessReference? LastRoot { get; private set; }

        public bool ThrowOnMonitor { get; set; }

        public bool BlockUntilCancelled { get; set; }

        public async Task<GameProcessTreeResult> MonitorAsync(
            GameProcessReference root,
            Action<GameProcessMetrics>? metricsChanged = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRoot = root;
            Entered.TrySetResult();
            if (ThrowOnMonitor)
            {
                throw new InvalidOperationException("Fixture crash.");
            }

            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            metricsChanged?.Invoke(new(
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(2),
                12,
                1024,
                GameProcessPriority.Normal,
                2));
            return new(
                TimeSpan.FromSeconds(2),
                1024,
                12,
                "Exited.");
        }
    }

    private sealed class FakeProfileCatalog : IProfileCatalogService
    {
        public SystemProfile ActiveProfile { get; private set; } =
            BuiltInProfiles.GetRequired(BuiltInProfiles.DefaultProfileId);

        public IReadOnlyList<SystemProfile> Profiles => BuiltInProfiles.All;

        public IReadOnlyList<string> LoadErrors => [];

        public event EventHandler<SystemProfile>? ActiveProfileChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SystemProfile> CreateAsync(
            string name,
            string description,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SystemProfile> DuplicateAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(
            SystemProfile profile,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetActiveAsync(
            string profileId,
            CancellationToken cancellationToken = default)
        {
            ActiveProfile = GetRequired(profileId);
            ActiveProfileChanged?.Invoke(this, ActiveProfile);
            return Task.CompletedTask;
        }

        public SystemProfile GetRequired(string profileId) =>
            BuiltInProfiles.GetRequired(profileId);
    }

    private sealed class FakeProfileEngine(FakeProfileCatalog catalog) : IProfileEngine
    {
        public int ApplyCount { get; private set; }

        public int RestoreCount { get; private set; }

        public Task<ProfileApplicationPlan> BuildPlanAsync(
            SystemProfile profile,
            CancellationToken cancellationToken = default)
        {
            var descriptor = new ActionDescriptor(
                "fixture.profile",
                1,
                "Fixture",
                "Fixture",
                "Fixture",
                SystemActionCategory.Other,
                ActionRiskLevel.Low,
                ActionPrivilegeLevel.StandardUser,
                ActionRestartRequirement.None,
                [ActionPlatform.Windows],
                true,
                true,
                null);
            var actionPlan = new ActionPlan(
                descriptor.Id,
                1,
                "Off",
                "On",
                [new("Fixture", "Fixture")],
                ["fixture"],
                ActionRiskLevel.Low,
                ActionPrivilegeLevel.StandardUser,
                ActionRestartRequirement.None,
                true,
                [],
                [],
                "Fixture");
            var transaction = new ActionTransactionPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                false,
                [new(descriptor, ActionDetectionResult.Available("Off", "Fixture"), actionPlan, ActionValidationResult.Valid)]);
            return Task.FromResult(new ProfileApplicationPlan(
                Guid.NewGuid(),
                profile,
                DateTimeOffset.UtcNow,
                [new(
                    Guid.NewGuid(),
                    descriptor.Id,
                    "Fixture",
                    "Off",
                    "On",
                    "Fixture",
                    true,
                    true,
                    "Standard user",
                    ProfilePlanDisposition.WillChange,
                    [])],
                transaction,
                new(true, [])));
        }

        public async Task<ProfileApplicationResult> ApplyAsync(
            ProfileApplicationPlan plan,
            string trigger,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            await catalog.SetActiveAsync(plan.Profile.Id, cancellationToken);
            return new(
                Guid.NewGuid(),
                plan.Profile.Id,
                plan.Profile.Name,
                Guid.NewGuid(),
                ProfileOutcome.Succeeded,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [],
                "Applied.");
        }

        public Task<ProfileApplicationResult> RestoreAsync(
            Guid transactionId,
            string profileId,
            string profileName,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            return Task.FromResult(new ProfileApplicationResult(
                Guid.NewGuid(),
                profileId,
                profileName,
                transactionId,
                ProfileOutcome.RolledBack,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [],
                "Restored."));
        }
    }

    private sealed record CoordinatorSetup(
        GameDefinition Game,
        GameLibraryTests.MemoryGameLibraryStore Store,
        GameLibraryService Library,
        FakeGameProcessService Process,
        FakeTreeMonitor Monitor,
        FakeProfileCatalog ProfileCatalog,
        FakeProfileEngine ProfileEngine,
        ActionEngineTestFixture Fixture,
        GameSessionCoordinator Coordinator) : IDisposable
    {
        public void Dispose()
        {
            Coordinator.Dispose();
            Library.Dispose();
            Fixture.Dispose();
        }
    }
}
