using Kernctl.Core.Profiles;

namespace Kernctl.App.ViewModels.Profiles;

public sealed record ProfilePlanItemViewModel(ProfileActionPlanItem Item)
{
    public string Name => Item.FriendlyName;

    public string CurrentValue => Item.CurrentValue;

    public string ProposedValue => Item.ProposedValue;

    public string Explanation => Item.Explanation;

    public string Reversibility => Item.IsReversible ? "Reversible" : "Not reversible";

    public string Privilege => Item.Privilege;

    public string Status => Item.Disposition switch
    {
        ProfilePlanDisposition.WillChange => "WILL CHANGE",
        ProfilePlanDisposition.AlreadyConfigured => "ALREADY CONFIGURED",
        ProfilePlanDisposition.Unsupported => "UNSUPPORTED",
        ProfilePlanDisposition.RequiresConfirmation => "REQUIRES CONFIRMATION",
        ProfilePlanDisposition.RequiresRestart => "REQUIRES RESTART",
        _ => "CHECK",
    };

    public string Messages => string.Join(" · ", Item.Messages);
}
