using Kernctl.Core.Themes;

namespace Kernctl.App.ViewModels.Themes;

public sealed record ContrastWarningViewModel(ContrastIssue Issue)
{
    public string Message => Issue.Message;
}
