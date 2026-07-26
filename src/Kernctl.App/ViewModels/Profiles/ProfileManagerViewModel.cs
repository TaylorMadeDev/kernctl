using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.App.Services;
using Kernctl.Core.Profiles;

namespace Kernctl.App.ViewModels.Profiles;

public enum ProfileWorkspaceMode
{
    Browser,
    Details,
    Editor,
    Plan,
    Result,
    History,
}

public sealed partial class ProfileManagerViewModel : ObservableObject
{
    private readonly IProfileCatalogService catalog;
    private readonly IProfileEngine engine;
    private readonly IProfileHistoryStore historyStore;
    private readonly IProfileFileDialogService fileDialogs;
    private ProfileApplicationPlan? currentPlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowserVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetailsVisible))]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsPlanVisible))]
    [NotifyPropertyChangedFor(nameof(IsResultVisible))]
    [NotifyPropertyChangedFor(nameof(IsHistoryVisible))]
    private ProfileWorkspaceMode mode = ProfileWorkspaceMode.Browser;

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmApplyCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private ProfileCardViewModel? selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmApply))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmApplyCommand))]
    private bool isConfirmationChecked;

    [ObservableProperty]
    private ProfileApplicationResult? lastResult;

    [ObservableProperty]
    private string editorName = string.Empty;

    [ObservableProperty]
    private string editorDescription = string.Empty;

    [ObservableProperty]
    private ProfileIcon editorIcon = ProfileIcon.Custom;

    [ObservableProperty]
    private ProfileAccent editorAccent = ProfileAccent.Violet;

    [ObservableProperty]
    private bool editorAutomaticEnabled;

    [ObservableProperty]
    private bool editorAutomaticApproved;

    [ObservableProperty]
    private string editorGamePath = string.Empty;

    [ObservableProperty]
    private int editorTriggerPriority = 50;

    [ObservableProperty]
    private int editorCooldownSeconds = 30;

    [ObservableProperty]
    private bool editorRestoreAfterGameExit = true;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    private SystemProfile? editingProfile;

    public ProfileManagerViewModel(
        IProfileCatalogService catalog,
        IProfileEngine engine,
        IProfileHistoryStore historyStore,
        IProfileFileDialogService fileDialogs)
    {
        this.catalog = catalog;
        this.engine = engine;
        this.historyStore = historyStore;
        this.fileDialogs = fileDialogs;
        catalog.ActiveProfileChanged += OnActiveProfileChanged;
    }

    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = [];

    public ObservableCollection<ProfileActionEditorViewModel> EditorActions { get; } = [];

    public ObservableCollection<ProfilePlanItemViewModel> PlanItems { get; } = [];

    public ObservableCollection<ProfileHistoryItemViewModel> History { get; } = [];

    public IReadOnlyList<ProfileIcon> AvailableIcons { get; } = Enum.GetValues<ProfileIcon>();

    public IReadOnlyList<ProfileAccent> AvailableAccents { get; } = Enum.GetValues<ProfileAccent>();

    public bool IsBrowserVisible => Mode == ProfileWorkspaceMode.Browser;

    public bool IsDetailsVisible => Mode == ProfileWorkspaceMode.Details;

    public bool IsEditorVisible => Mode == ProfileWorkspaceMode.Editor;

    public bool IsPlanVisible => Mode == ProfileWorkspaceMode.Plan;

    public bool IsResultVisible => Mode == ProfileWorkspaceMode.Result;

    public bool IsHistoryVisible => Mode == ProfileWorkspaceMode.History;

    public bool CanConfirmApply =>
        IsConfirmationChecked
        && currentPlan?.CanApply == true
        && !IsBusy;

    public SystemProfile ActiveProfile => catalog.ActiveProfile;

    public event EventHandler<SystemProfile>? ActiveProfileChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshProfiles();
        return RefreshHistoryAsync(cancellationToken);
    }

    [RelayCommand]
    private void Open()
    {
        RefreshProfiles();
        Mode = ProfileWorkspaceMode.Browser;
        StatusMessage = string.Empty;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy)
        {
            return;
        }

        IsOpen = false;
        currentPlan = null;
        IsConfirmationChecked = false;
    }

    [RelayCommand]
    private void Back()
    {
        Mode = ProfileWorkspaceMode.Browser;
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
        currentPlan = null;
        IsConfirmationChecked = false;
    }

    [RelayCommand]
    private void ShowDetails(ProfileCardViewModel? profile)
    {
        SelectedProfile = profile ?? SelectedProfile;
        if (SelectedProfile is not null)
        {
            Mode = ProfileWorkspaceMode.Details;
        }
    }

    [RelayCommand]
    private async Task PreviewAsync(ProfileCardViewModel? profile)
    {
        var selected = profile ?? SelectedProfile;
        if (selected is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Detecting current state and validating every action…";
        try
        {
            SelectedProfile = selected;
            currentPlan = await engine.BuildPlanAsync(selected.Profile);
            PlanItems.Clear();
            foreach (var item in currentPlan.Actions)
            {
                PlanItems.Add(new(item));
            }

            selected.IsFullySupported = currentPlan.Actions.All(item =>
                item.Disposition != ProfilePlanDisposition.Unsupported);
            IsConfirmationChecked = false;
            ValidationMessage = string.Join(
                Environment.NewLine,
                currentPlan.Validation.Issues.Select(issue => issue.Message));
            StatusMessage = currentPlan.CanApply
                ? "Review the complete plan. No changes have been made."
                : "This plan cannot be applied until required issues are resolved.";
            Mode = ProfileWorkspaceMode.Plan;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanConfirmApply));
            ConfirmApplyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmApply))]
    private async Task ConfirmApplyAsync()
    {
        if (currentPlan is null)
        {
            return;
        }

        IsBusy = true;
        OnPropertyChanged(nameof(CanConfirmApply));
        ConfirmApplyCommand.NotifyCanExecuteChanged();
        StatusMessage = "Applying actions transactionally…";
        try
        {
            LastResult = await engine.ApplyAsync(currentPlan, "Manual");
            StatusMessage = LastResult.Summary;
            Mode = ProfileWorkspaceMode.Result;
            RefreshProfiles();
            await RefreshHistoryAsync();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanConfirmApply));
            ConfirmApplyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task RestorePreviousAsync()
    {
        if (LastResult?.TransactionId is not { } transactionId || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Restoring every captured value in reverse order…";
        try
        {
            LastResult = await engine.RestoreAsync(
                transactionId,
                LastResult.ProfileId,
                LastResult.ProfileName);
            StatusMessage = LastResult.Summary;
            await RefreshHistoryAsync();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        var now = DateTimeOffset.UtcNow;
        BeginEditing(new SystemProfile
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = "New profile",
            Description = "Describe when this profile should be used.",
            Icon = ProfileIcon.Custom,
            Accent = ProfileAccent.Violet,
            IsBuiltIn = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
    }

    [RelayCommand]
    private async Task DuplicateAsync(ProfileCardViewModel? profile)
    {
        var source = profile ?? SelectedProfile;
        if (source is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            BeginEditing(await catalog.DuplicateAsync(source.Id));
            RefreshProfiles();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Edit(ProfileCardViewModel? profile)
    {
        var selected = profile ?? SelectedProfile;
        if (selected is null)
        {
            return;
        }

        if (selected.IsBuiltIn)
        {
            StatusMessage = "Built-in profiles are immutable. Duplicate this profile to edit it.";
            return;
        }

        BeginEditing(selected.Profile);
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (editingProfile is null || IsBusy)
        {
            return;
        }

        var triggers = ImmutableArray.CreateBuilder<ProfileTriggerDefinition>();
        if (!string.IsNullOrWhiteSpace(EditorGamePath))
        {
            triggers.Add(new()
            {
                Id = Guid.NewGuid(),
                Kind = ProfileTriggerKind.GameStarted,
                SelectedExecutablePath = EditorGamePath,
                Priority = EditorTriggerPriority,
                RestorePreviousProfileOnExit = EditorRestoreAfterGameExit,
            });
        }

        var candidate = editingProfile with
        {
            Name = EditorName.Trim(),
            Description = EditorDescription.Trim(),
            Icon = EditorIcon,
            Accent = EditorAccent,
            OrderedActions = [.. EditorActions.Select(action => action.BuildDefinition())],
            TriggerConfiguration = new()
            {
                IsEnabled = EditorAutomaticEnabled,
                AutomaticBehaviourApproved = EditorAutomaticApproved,
                Cooldown = TimeSpan.FromSeconds(EditorCooldownSeconds),
                Triggers = triggers.ToImmutable(),
            },
        };
        var validation = ProfileValidation.Validate(candidate);
        ValidationMessage = string.Join(
            Environment.NewLine,
            validation.Issues.Select(issue => issue.Message));
        if (!validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await catalog.SaveAsync(candidate);
            RefreshProfiles();
            SelectedProfile = Profiles.Single(profile => profile.Id == candidate.Id);
            Mode = ProfileWorkspaceMode.Details;
            StatusMessage = "Custom profile saved.";
        }
        catch (Exception exception) when (
            exception is ProfileDataException or IOException or UnauthorizedAccessException)
        {
            ValidationMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileCardViewModel? profile)
    {
        var selected = profile ?? SelectedProfile;
        if (selected is null || selected.IsBuiltIn || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await catalog.DeleteAsync(selected.Id);
            SelectedProfile = null;
            RefreshProfiles();
            Mode = ProfileWorkspaceMode.Browser;
            StatusMessage = "Custom profile deleted.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddPowerAction() =>
        AddAction(ProfileActionDefinition.Power(KnownPowerScheme.Balanced));

    [RelayCommand]
    private void AddMonitoringAction() =>
        AddAction(ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true));

    [RelayCommand]
    private void AddPreferenceAction() =>
        AddAction(ProfileActionDefinition.PreferenceToggle(KernctlPreference.PerformanceMode, true));

    [RelayCommand]
    private void RemoveAction(ProfileActionEditorViewModel? action)
    {
        if (action is not null)
        {
            EditorActions.Remove(action);
            ValidateEditorActions();
        }
    }

    [RelayCommand]
    private void MoveActionUp(ProfileActionEditorViewModel? action)
    {
        if (action is null)
        {
            return;
        }

        var index = EditorActions.IndexOf(action);
        if (index > 0)
        {
            EditorActions.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveActionDown(ProfileActionEditorViewModel? action)
    {
        if (action is null)
        {
            return;
        }

        var index = EditorActions.IndexOf(action);
        if (index >= 0 && index < EditorActions.Count - 1)
        {
            EditorActions.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private async Task SelectGameAsync()
    {
        var path = await fileDialogs.PickExecutableAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorGamePath = path;
            EditorAutomaticEnabled = true;
            EditorAutomaticApproved = false;
        }
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        await RefreshHistoryAsync();
        Mode = ProfileWorkspaceMode.History;
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await historyStore.ClearAsync();
        History.Clear();
        StatusMessage = "Profile history cleared.";
    }

    [RelayCommand]
    private async Task ExportAsync(ProfileCardViewModel? profile)
    {
        var selected = profile ?? SelectedProfile;
        if (selected is null || selected.IsBuiltIn)
        {
            StatusMessage = "Duplicate a built-in profile before exporting it.";
            return;
        }

        var path = await fileDialogs.PickExportPathAsync(
            $"{ProfileValidation.SanitizeFileName(selected.Name)}.json");
        if (path is not null)
        {
            await ProfileStore.ExportAsync(selected.Profile, path);
            StatusMessage = "Profile exported as data-only JSON.";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await fileDialogs.PickImportPathAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = await ProfileStore.ImportAsync(path);
            await catalog.SaveAsync(imported);
            RefreshProfiles();
            StatusMessage = "Profile imported with automatic switching disabled.";
        }
        catch (Exception exception) when (
            exception is ProfileDataException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
    }

    private void BeginEditing(SystemProfile profile)
    {
        editingProfile = profile;
        EditorName = profile.Name;
        EditorDescription = profile.Description;
        EditorIcon = profile.Icon;
        EditorAccent = profile.Accent;
        EditorAutomaticEnabled = profile.TriggerConfiguration.IsEnabled;
        EditorAutomaticApproved = profile.TriggerConfiguration.AutomaticBehaviourApproved;
        EditorCooldownSeconds = (int)profile.TriggerConfiguration.Cooldown.TotalSeconds;
        var gameTrigger = profile.TriggerConfiguration.Triggers.FirstOrDefault(trigger =>
            trigger.Kind == ProfileTriggerKind.GameStarted);
        EditorGamePath = gameTrigger?.SelectedExecutablePath ?? string.Empty;
        EditorTriggerPriority = gameTrigger?.Priority ?? 50;
        EditorRestoreAfterGameExit = gameTrigger?.RestorePreviousProfileOnExit ?? true;
        EditorActions.Clear();
        foreach (var action in profile.OrderedActions)
        {
            EditorActions.Add(new(action));
        }

        ValidationMessage = string.Empty;
        Mode = ProfileWorkspaceMode.Editor;
    }

    private void AddAction(ProfileActionDefinition definition)
    {
        EditorActions.Add(new(definition));
        ValidateEditorActions();
    }

    private void ValidateEditorActions()
    {
        var conflicts = EditorActions
            .Select(action => action.BuildDefinition())
            .GroupBy(action => action.TargetKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        ValidationMessage = conflicts.Length == 0
            ? string.Empty
            : $"Resolve duplicate actions targeting: {string.Join(", ", conflicts)}.";
    }

    private void RefreshProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in catalog.Profiles)
        {
            Profiles.Add(new(profile, profile.Id == catalog.ActiveProfile.Id));
        }

        SelectedProfile = selectedId is null
            ? null
            : Profiles.FirstOrDefault(profile => profile.Id == selectedId);
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var entries = await historyStore.ReadAsync(cancellationToken);
        History.Clear();
        foreach (var entry in entries)
        {
            History.Add(new(entry));
        }
    }

    private void OnActiveProfileChanged(object? sender, SystemProfile profile)
    {
        RefreshProfiles();
        ActiveProfileChanged?.Invoke(this, profile);
    }
}
