using Kernctl.Core.Actions;
using Kernctl.Core.Profiles;

namespace Kernctl.Core.Tests;

public sealed class ProfileEngineTests
{
    [Fact]
    public async Task SuccessfulProfileIsAppliedVerifiedHistorizedAndRestorable()
    {
        using var fixture = await ProfileEngineFixture.CreateAsync();
        var profile = BuiltInProfiles.GetRequired(BuiltInProfiles.GamingId);
        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);

        var result = await fixture.ProfileEngine.ApplyAsync(
            plan,
            "Manual",
            TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Equal(ProfileOutcome.Succeeded, result.Outcome);
        Assert.Equal(profile.Id, fixture.Catalog.ActiveProfile.Id);
        Assert.True(await fixture.Features.GetPreferenceAsync(
            KernctlPreference.PerformanceMode,
            TestContext.Current.CancellationToken));
        Assert.True(await fixture.Features.GetMonitoringEnabledAsync(
            MonitoringFeature.Fps,
            TestContext.Current.CancellationToken));
        Assert.Equal(3, result.Actions.Count(action => action.Succeeded));
        Assert.Single(await fixture.ProfileHistory.ReadAsync(TestContext.Current.CancellationToken));

        var restored = await fixture.ProfileEngine.RestoreAsync(
            Assert.IsType<Guid>(result.TransactionId),
            result.ProfileId,
            result.ProfileName,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileOutcome.RolledBack, restored.Outcome);
        Assert.False(await fixture.Features.GetPreferenceAsync(
            KernctlPreference.PerformanceMode,
            TestContext.Current.CancellationToken));
        Assert.False(await fixture.Features.GetMonitoringEnabledAsync(
            MonitoringFeature.Fps,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequiredPowerActionFailureNeverMarksProfileActive()
    {
        using var fixture = await ProfileEngineFixture.CreateAsync();
        fixture.Power.FailWrites = true;
        var profile = BuiltInProfiles.GetRequired(BuiltInProfiles.CompetitiveId);
        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);

        var result = await fixture.ProfileEngine.ApplyAsync(
            plan,
            "Manual",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(ProfileOutcome.Succeeded, result.Outcome);
        Assert.Equal(BuiltInProfiles.BalancedId, fixture.Catalog.ActiveProfile.Id);
        Assert.NotEqual(profile.Id, fixture.Catalog.ActiveProfile.Id);
    }

    [Fact]
    public async Task UnsupportedRequiredActionIsVisibleAndCannotApply()
    {
        using var fixture = await ProfileEngineFixture.CreateAsync(includePowerActions: false);
        var profile = BuiltInProfiles.GetRequired(BuiltInProfiles.BalancedId);

        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Actions, action =>
            action.IsRequired && action.Disposition == ProfilePlanDisposition.Unsupported);
    }

    [Fact]
    public async Task ConcurrentProfileTransactionsAreRejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionId = "kernctl.preference.performance-mode.on";
        var slowAction = new TestSystemAction(
            actionId,
            applyEntered: entered,
            applyRelease: release);
        using var fixture = await ProfileEngineFixture.CreateAsync(
            customActions: [slowAction]);
        var profile = Custom(ProfileActionDefinition.PreferenceToggle(
            KernctlPreference.PerformanceMode,
            true,
            isRequired: true));
        await fixture.Catalog.SaveAsync(profile, TestContext.Current.CancellationToken);
        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);
        var first = fixture.ProfileEngine.ApplyAsync(
            plan,
            "Manual",
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProfileBusyException>(
            () => fixture.ProfileEngine.ApplyAsync(
                plan,
                "Manual",
                TestContext.Current.CancellationToken));
        release.SetResult();
        var result = await first;

        Assert.Equal(ProfileOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task ProfileEnginePreservesDeclaredActionOrder()
    {
        var operations = new List<string>();
        var firstId = "kernctl.monitoring.fps.on";
        var secondId = "kernctl.preference.performance-mode.on";
        var first = new TestSystemAction(firstId, operations);
        var second = new TestSystemAction(secondId, operations);
        using var fixture = await ProfileEngineFixture.CreateAsync(customActions: [first, second]);
        var profile = Custom(
            ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true, isRequired: true),
            ProfileActionDefinition.PreferenceToggle(
                KernctlPreference.PerformanceMode,
                true,
                isRequired: true));
        await fixture.Catalog.SaveAsync(profile, TestContext.Current.CancellationToken);
        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);

        await fixture.ProfileEngine.ApplyAsync(plan, "Manual", TestContext.Current.CancellationToken);

        Assert.True(operations.IndexOf($"apply:{firstId}") < operations.IndexOf($"apply:{secondId}"));
    }

    [Fact]
    public async Task RollbackFailureIsReportedAsRollbackFailed()
    {
        var firstId = "windows.power-scheme.balanced";
        var secondId = "kernctl.monitoring.fps.on";
        var rollbackFails = new TestSystemAction(firstId, failRollback: true);
        var verificationFails = new TestSystemAction(secondId, failVerification: true);
        using var fixture = await ProfileEngineFixture.CreateAsync(
            customActions: [rollbackFails, verificationFails]);
        var profile = Custom(
            ProfileActionDefinition.Power(KnownPowerScheme.Balanced),
            ProfileActionDefinition.Monitoring(
                MonitoringFeature.Fps,
                true,
                isRequired: true));
        await fixture.Catalog.SaveAsync(profile, TestContext.Current.CancellationToken);
        var plan = await fixture.ProfileEngine.BuildPlanAsync(
            profile,
            TestContext.Current.CancellationToken);

        var result = await fixture.ProfileEngine.ApplyAsync(
            plan,
            "Manual",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileOutcome.RollbackFailed, result.Outcome);
        Assert.NotEqual(profile.Id, fixture.Catalog.ActiveProfile.Id);
    }

    private static SystemProfile Custom(params ProfileActionDefinition[] actions)
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = "Transactional test",
            Description = "Uses isolated test actions only.",
            Icon = ProfileIcon.Custom,
            Accent = ProfileAccent.Violet,
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            OrderedActions = [.. actions],
        };
    }
}

internal sealed class ProfileEngineFixture : IDisposable
{
    private readonly TemporaryDirectory directory;
    private readonly ActionTransactionEngine actionEngine;
    private readonly ProfileEngine profileEngine;
    private readonly ProfileHistoryStore profileHistory;

    private ProfileEngineFixture(
        TemporaryDirectory directory,
        FakePowerSchemeService power,
        KernctlFeatureState features,
        ProfileStore store,
        ProfileCatalogService catalog,
        ProfileHistoryStore profileHistory,
        ActionTransactionEngine actionEngine,
        ProfileEngine profileEngine)
    {
        this.directory = directory;
        this.actionEngine = actionEngine;
        this.profileEngine = profileEngine;
        this.profileHistory = profileHistory;
        Power = power;
        Features = features;
        Store = store;
        Catalog = catalog;
        ProfileHistory = profileHistory;
        ProfileEngine = profileEngine;
    }

    public FakePowerSchemeService Power { get; }

    public KernctlFeatureState Features { get; }

    public ProfileStore Store { get; }

    public ProfileCatalogService Catalog { get; }

    public ProfileHistoryStore ProfileHistory { get; }

    public ProfileEngine ProfileEngine { get; }

    public static async Task<ProfileEngineFixture> CreateAsync(
        bool includePowerActions = true,
        ISystemAction[]? customActions = null)
    {
        var directory = new TemporaryDirectory("profile-engine");
        var power = new FakePowerSchemeService();
        var features = new KernctlFeatureState();
        var actions = customActions ??
        [
            .. includePowerActions
                ? new ISystemAction[]
                {
                    new PowerSchemeSystemAction(power, KnownPowerScheme.PowerSaver),
                    new PowerSchemeSystemAction(power, KnownPowerScheme.Balanced),
                    new PowerSchemeSystemAction(power, KnownPowerScheme.HighPerformance),
                }
                : [],
            new KernctlFeatureSystemAction(features, MonitoringFeature.Fps, false),
            new KernctlFeatureSystemAction(features, MonitoringFeature.Fps, true),
            new KernctlFeatureSystemAction(features, KernctlPreference.PerformanceMode, false),
            new KernctlFeatureSystemAction(features, KernctlPreference.PerformanceMode, true),
        ];
        var journalStore = new FileActionJournalStore(new(
            Path.Combine(directory.Path, "transactions"),
            HistoryRetention: 20));
        var actionHistory = new ActionHistoryService(journalStore);
        var actionEngine = new ActionTransactionEngine(
            new ActionRegistry(actions),
            journalStore,
            actionHistory);
        var store = new ProfileStore(directory.Path);
        var catalog = new ProfileCatalogService(store);
        await catalog.InitializeAsync(TestContext.Current.CancellationToken);
        var profileHistory = new ProfileHistoryStore(directory.Path);
        var profileEngine = new ProfileEngine(actionEngine, catalog, profileHistory);
        return new(
            directory,
            power,
            features,
            store,
            catalog,
            profileHistory,
            actionEngine,
            profileEngine);
    }

    public void Dispose()
    {
        profileEngine.Dispose();
        actionEngine.Dispose();
        profileHistory.Dispose();
        directory.Dispose();
    }
}

internal sealed class FakePowerSchemeService : IPowerSchemeService
{
    public Guid ActiveScheme { get; set; } = PowerSchemeIds.Balanced;

    public bool FailWrites { get; set; }

    public HashSet<Guid> AvailableSchemes { get; } =
    [
        PowerSchemeIds.PowerSaver,
        PowerSchemeIds.Balanced,
        PowerSchemeIds.HighPerformance,
    ];

    public Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ActiveScheme);
    }

    public Task<bool> IsSchemeAvailableAsync(
        KnownPowerScheme scheme,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AvailableSchemes.Contains(GetSchemeId(scheme)));
    }

    public Task SetActiveSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites)
        {
            throw new InvalidOperationException("Deterministic power write failure.");
        }

        ActiveScheme = schemeId;
        return Task.CompletedTask;
    }

    public Guid GetSchemeId(KnownPowerScheme scheme) => PowerSchemeIds.For(scheme);
}
