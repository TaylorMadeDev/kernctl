using Kernctl.Core.Models;

namespace Kernctl.App.ViewModels;

public sealed record ProfileChoiceViewModel(
    ProfileKind Kind,
    string Name,
    string Description,
    string Icon);
