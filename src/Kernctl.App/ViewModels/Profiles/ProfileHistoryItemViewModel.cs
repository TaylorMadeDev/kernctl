using Kernctl.Core.Profiles;
using System.Globalization;

namespace Kernctl.App.ViewModels.Profiles;

public sealed record ProfileHistoryItemViewModel(ProfileActivationHistoryEntry Entry)
{
    public string ProfileName => Entry.ProfileName;

    public string Trigger => Entry.Trigger;

    public string Started => Entry.StartedAtUtc
        .ToLocalTime()
        .ToString("g", CultureInfo.CurrentCulture);

    public string Ended => Entry.EndedAtUtc is { } ended
        ? ended.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "In progress";

    public string Outcome => Entry.Outcome.ToString();

    public string ActionSummary => $"{Entry.ActionsApplied} applied · {Entry.FailedActions} failed";

    public string Rollback => Entry.RollbackStatus;
}
