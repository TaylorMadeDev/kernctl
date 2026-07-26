namespace Kernctl.Core.Profiles;

public interface IProfileCatalogService
{
    SystemProfile ActiveProfile { get; }

    IReadOnlyList<SystemProfile> Profiles { get; }

    IReadOnlyList<string> LoadErrors { get; }

    event EventHandler<SystemProfile>? ActiveProfileChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<SystemProfile> CreateAsync(
        string name,
        string description,
        CancellationToken cancellationToken = default);

    Task<SystemProfile> DuplicateAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(SystemProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);

    Task SetActiveAsync(string profileId, CancellationToken cancellationToken = default);

    SystemProfile GetRequired(string profileId);
}

public sealed class ProfileCatalogService(IProfileStore store) : IProfileCatalogService
{
    private readonly List<SystemProfile> customProfiles = [];
    private readonly List<string> loadErrors = [];
    private bool initialized;

    public SystemProfile ActiveProfile { get; private set; } =
        BuiltInProfiles.GetRequired(BuiltInProfiles.DefaultProfileId);

    public IReadOnlyList<SystemProfile> Profiles =>
        [.. BuiltInProfiles.All, .. customProfiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> LoadErrors => loadErrors;

    public event EventHandler<SystemProfile>? ActiveProfileChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        var snapshot = await store.LoadAsync(cancellationToken);
        customProfiles.Clear();
        customProfiles.AddRange(snapshot.CustomProfiles);
        loadErrors.Clear();
        loadErrors.AddRange(snapshot.Errors);
        ActiveProfile = Profiles.Single(profile => profile.Id == snapshot.ActiveProfileId);
        initialized = true;
    }

    public async Task<SystemProfile> CreateAsync(
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new SystemProfile
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = name.Trim(),
            Description = description.Trim(),
            Icon = ProfileIcon.Custom,
            Accent = ProfileAccent.Violet,
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await SaveAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<SystemProfile> DuplicateAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var source = GetRequired(profileId);
        var now = DateTimeOffset.UtcNow;
        var duplicate = source with
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = CreateUniqueName($"{source.Name} copy"),
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            OrderedActions =
            [
                .. source.OrderedActions.Select(action => action with { Id = Guid.NewGuid() }),
            ],
            TriggerConfiguration = source.TriggerConfiguration with
            {
                IsEnabled = false,
                AutomaticBehaviourApproved = false,
                Triggers =
                [
                    .. source.TriggerConfiguration.Triggers.Select(trigger =>
                        trigger with { Id = Guid.NewGuid() }),
                ],
            },
        };
        await SaveAsync(duplicate, cancellationToken);
        return duplicate;
    }

    public async Task SaveAsync(
        SystemProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in profiles cannot be changed.");
        }

        var validation = ProfileValidation.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileDataException(validation.Issues[0].Message);
        }

        var updated = profile with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync(updated, cancellationToken);
        var index = customProfiles.FindIndex(item => item.Id == updated.Id);
        if (index < 0)
        {
            customProfiles.Add(updated);
        }
        else
        {
            customProfiles[index] = updated;
        }
    }

    public async Task DeleteAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = GetRequired(profileId);
        if (profile.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in profiles cannot be deleted.");
        }

        if (ActiveProfile.Id == profile.Id)
        {
            await SetActiveAsync(BuiltInProfiles.DefaultProfileId, cancellationToken);
        }

        await store.DeleteAsync(profile, cancellationToken);
        customProfiles.RemoveAll(item => item.Id == profile.Id);
    }

    public async Task SetActiveAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var selected = GetRequired(profileId);
        if (selected.Id == ActiveProfile.Id)
        {
            return;
        }

        await store.SaveActiveProfileIdAsync(selected.Id, cancellationToken);
        ActiveProfile = selected;
        ActiveProfileChanged?.Invoke(this, selected);
    }

    public SystemProfile GetRequired(string profileId) =>
        Profiles.SingleOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown profile.");

    private string CreateUniqueName(string preferred)
    {
        var names = Profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(preferred))
        {
            return preferred;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{preferred} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{preferred} {Guid.NewGuid():N}"[..64];
    }
}
