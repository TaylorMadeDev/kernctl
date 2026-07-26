using System.Text.Json;
using Kernctl.Core.Profiles;

namespace Kernctl.Core.Tests;

public sealed class ProfileModelAndStoreTests
{
    [Fact]
    public void BuiltInsAreImmutableDistinctValidAndBalancedByDefault()
    {
        Assert.Equal(
            ["battery-saver", "balanced", "gaming", "competitive"],
            BuiltInProfiles.All.Select(profile => profile.Id));
        Assert.All(BuiltInProfiles.All, profile =>
        {
            Assert.True(profile.IsBuiltIn);
            Assert.True(ProfileValidation.Validate(profile).IsValid);
            Assert.NotEmpty(profile.OrderedActions);
        });
        Assert.Equal("balanced", BuiltInProfiles.DefaultProfileId);
        Assert.All(
            BuiltInProfiles.All.SelectMany(profile => profile.OrderedActions),
            action => Assert.DoesNotContain("Realtime", action.TargetKey, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationRejectsDuplicateTargetsAndMalformedTypedValues()
    {
        var profile = Custom(
            ProfileActionDefinition.Power(KnownPowerScheme.Balanced),
            ProfileActionDefinition.Power(KnownPowerScheme.HighPerformance),
            new ProfileActionDefinition
            {
                Id = Guid.NewGuid(),
                Kind = ProfileActionKind.Monitoring,
                TargetKey = "kernctl.monitoring.fps",
                MonitoringFeature = MonitoringFeature.Fps,
            });

        var result = ProfileValidation.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "action.conflict");
        Assert.Contains(result.Issues, issue => issue.Code == "action.value");
    }

    [Fact]
    public void OrderedActionsSurviveSerialization()
    {
        var profile = Custom(
            ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true),
            ProfileActionDefinition.Power(KnownPowerScheme.PowerSaver));

        var json = JsonSerializer.Serialize(profile, ProfileJson.Options);
        var restored = JsonSerializer.Deserialize<SystemProfile>(json, ProfileJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(
            [ProfileActionKind.Monitoring, ProfileActionKind.PowerScheme],
            restored.OrderedActions.Select(action => action.Kind));
        Assert.Equal(KnownPowerScheme.PowerSaver, restored.OrderedActions[1].PowerScheme);
    }

    [Fact]
    public async Task StoreSkipsMalformedFilesAndFallsBackSafely()
    {
        using var directory = new TemporaryDirectory("profile-store");
        var profilesDirectory = Path.Combine(directory.Path, "profiles");
        Directory.CreateDirectory(profilesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(profilesDirectory, "broken.json"),
            "{ not-json",
            TestContext.Current.CancellationToken);
        var store = new ProfileStore(directory.Path);

        var snapshot = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.CustomProfiles);
        Assert.Equal(BuiltInProfiles.DefaultProfileId, snapshot.ActiveProfileId);
        Assert.Single(snapshot.Errors);
    }

    [Fact]
    public async Task CustomProfilesUseAtomicVersionedPersistence()
    {
        using var directory = new TemporaryDirectory("profile-store");
        var store = new ProfileStore(directory.Path);
        var profile = Custom(ProfileActionDefinition.Power(KnownPowerScheme.Balanced));

        await store.SaveAsync(profile, TestContext.Current.CancellationToken);
        await store.SaveActiveProfileIdAsync(profile.Id, TestContext.Current.CancellationToken);
        var snapshot = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(profile.Name, Assert.Single(snapshot.CustomProfiles).Name);
        Assert.Equal(profile.Id, snapshot.ActiveProfileId);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HistoryRedactsPathsArgumentsAndCanBeCleared()
    {
        using var directory = new TemporaryDirectory("profile-history");
        using var store = new ProfileHistoryStore(directory.Path);
        var entry = new ProfileActivationHistoryEntry(
            Guid.NewGuid(),
            "gaming",
            "Gaming",
            @"C:\Users\name\game.exe --token hidden",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ProfileOutcome.Succeeded,
            2,
            0,
            "Not required",
            Guid.NewGuid());

        await store.AppendAsync(entry, TestContext.Current.CancellationToken);
        var read = Assert.Single(await store.ReadAsync(TestContext.Current.CancellationToken));
        var persisted = await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "profile-history.json"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Automatic trigger", read.Trigger);
        Assert.DoesNotContain("Users", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", persisted, StringComparison.OrdinalIgnoreCase);
        await store.ClearAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CatalogCreatesEditsDuplicatesAndDeletesCustomProfiles()
    {
        using var directory = new TemporaryDirectory("profile-catalog");
        var catalog = new ProfileCatalogService(new ProfileStore(directory.Path));
        await catalog.InitializeAsync(TestContext.Current.CancellationToken);

        var created = await catalog.CreateAsync(
            "My profile",
            "Created through the profile builder workflow.",
            TestContext.Current.CancellationToken);
        var edited = created with
        {
            Name = "My edited profile",
            OrderedActions =
            [
                ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true),
            ],
        };
        await catalog.SaveAsync(edited, TestContext.Current.CancellationToken);
        var duplicate = await catalog.DuplicateAsync(
            BuiltInProfiles.GamingId,
            TestContext.Current.CancellationToken);

        Assert.Equal("My edited profile", catalog.GetRequired(created.Id).Name);
        Assert.False(duplicate.IsBuiltIn);
        Assert.Equal(3, duplicate.OrderedActions.Length);

        await catalog.DeleteAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(catalog.Profiles, profile => profile.Id == created.Id);
    }

    [Fact]
    public async Task ImportRemovesExecutableAssignmentsAndAutomaticApproval()
    {
        using var directory = new TemporaryDirectory("profile-import");
        var profile = Custom(ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true)) with
        {
            TriggerConfiguration = new()
            {
                IsEnabled = true,
                AutomaticBehaviourApproved = true,
                Triggers =
                [
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Kind = ProfileTriggerKind.GameStarted,
                        SelectedExecutablePath = Path.GetFullPath("selected-game.exe"),
                        Priority = 50,
                    },
                ],
            },
        };
        var path = Path.Combine(directory.Path, "import.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(profile, ProfileJson.Options),
            TestContext.Current.CancellationToken);

        var imported = await ProfileStore.ImportAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.False(imported.TriggerConfiguration.IsEnabled);
        Assert.False(imported.TriggerConfiguration.AutomaticBehaviourApproved);
        Assert.Empty(imported.TriggerConfiguration.Triggers);
    }

    private static SystemProfile Custom(params ProfileActionDefinition[] actions)
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = "Test profile",
            Description = "A deterministic test profile.",
            Icon = ProfileIcon.Custom,
            Accent = ProfileAccent.Blue,
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            OrderedActions = [.. actions],
        };
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string label)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kernctl-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
