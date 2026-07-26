using Kernctl.Core.Profiles;

namespace Kernctl.Core.Tests;

public sealed class AutomaticProfileSwitcherTests
{
    [Fact]
    public void HighestPriorityApprovedTriggerWins()
    {
        var switcher = new AutomaticProfileSwitcher();
        var gamePath = Path.GetFullPath("game.exe");
        var low = Triggered("low", gamePath, priority: 10);
        var high = Triggered("high", gamePath, priority: 90);

        var decision = switcher.Evaluate(
            new(ProfileTriggerKind.GameStarted, DateTimeOffset.UtcNow, gamePath),
            [low, high],
            "balanced");

        Assert.True(decision.ShouldActivate);
        Assert.Equal("high", decision.ProfileId);
    }

    [Fact]
    public void CooldownPreventsRapidReactivation()
    {
        var switcher = new AutomaticProfileSwitcher();
        var now = DateTimeOffset.UtcNow;
        var gamePath = Path.GetFullPath("game.exe");
        var profile = Triggered("gaming", gamePath, priority: 50);
        var trigger = profile.TriggerConfiguration.Triggers[0];
        switcher.RecordActivation(profile.Id, "balanced", trigger, now);

        var decision = switcher.Evaluate(
            new(ProfileTriggerKind.GameStarted, now.AddSeconds(5), gamePath),
            [profile],
            "balanced");

        Assert.False(decision.ShouldActivate);
        Assert.Contains("cooldown", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameExitRestoresPreviousProfileWhenConfigured()
    {
        var switcher = new AutomaticProfileSwitcher();
        var now = DateTimeOffset.UtcNow;
        var gamePath = Path.GetFullPath("game.exe");
        var profile = Triggered("gaming", gamePath, priority: 50);
        switcher.RecordActivation(
            profile.Id,
            "balanced",
            profile.TriggerConfiguration.Triggers[0],
            now);

        var decision = switcher.Evaluate(
            new(ProfileTriggerKind.GameExited, now.AddMinutes(2), gamePath),
            [profile],
            "gaming");

        Assert.True(decision.ShouldActivate);
        Assert.True(decision.ShouldRestorePreviousProfile);
        Assert.Equal("balanced", decision.ProfileId);
    }

    [Fact]
    public void UnapprovedAutomaticBehaviourNeverActivates()
    {
        var gamePath = Path.GetFullPath("game.exe");
        var profile = Triggered("gaming", gamePath, priority: 50) with
        {
            TriggerConfiguration = Triggered("gaming", gamePath, 50)
                .TriggerConfiguration with
            {
                AutomaticBehaviourApproved = false,
            },
        };

        var decision = new AutomaticProfileSwitcher().Evaluate(
            new(ProfileTriggerKind.GameStarted, DateTimeOffset.UtcNow, gamePath),
            [profile],
            "balanced");

        Assert.False(decision.ShouldActivate);
    }

    [Fact]
    public void EqualHighestPrioritiesAreReportedAsAConflict()
    {
        var gamePath = Path.GetFullPath("game.exe");
        var decision = new AutomaticProfileSwitcher().Evaluate(
            new(ProfileTriggerKind.GameStarted, DateTimeOffset.UtcNow, gamePath),
            [Triggered("one", gamePath, 50), Triggered("two", gamePath, 50)],
            "balanced");

        Assert.False(decision.ShouldActivate);
        Assert.Contains("conflict", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static SystemProfile Triggered(string id, string path, int priority)
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Id = id,
            Name = id,
            Description = "Automatic profile",
            Icon = ProfileIcon.Custom,
            Accent = ProfileAccent.Violet,
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            OrderedActions = [ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true)],
            TriggerConfiguration = new()
            {
                IsEnabled = true,
                AutomaticBehaviourApproved = true,
                Cooldown = TimeSpan.FromSeconds(30),
                Triggers =
                [
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Kind = ProfileTriggerKind.GameStarted,
                        SelectedExecutablePath = path,
                        Priority = priority,
                        RestorePreviousProfileOnExit = true,
                    },
                ],
            },
        };
    }
}
