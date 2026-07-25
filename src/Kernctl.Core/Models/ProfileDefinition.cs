namespace Kernctl.Core.Models;

/// <summary>Describes an in-memory kernctl profile.</summary>
public sealed record ProfileDefinition(ProfileKind Kind, string Name, string Description);
