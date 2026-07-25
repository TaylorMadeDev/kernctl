using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.App.Services;
using Kernctl.App.ViewModels.Themes;
using Kernctl.Core.Themes;

namespace Kernctl.App.ViewModels.Pages;

public sealed class AppearancePageViewModel : ObservableObject
{
    private static readonly ColorTokenDescriptor[] ColorDescriptors =
    [
        new(nameof(ThemeColors.WindowBackground), "Window background", "Backgrounds"),
        new(nameof(ThemeColors.SidebarBackground), "Sidebar background", "Backgrounds"),
        new(nameof(ThemeColors.SurfacePrimary), "Primary surface", "Surfaces and borders"),
        new(nameof(ThemeColors.SurfaceSecondary), "Secondary surface", "Surfaces and borders"),
        new(nameof(ThemeColors.SurfaceElevated), "Elevated surface", "Surfaces and borders"),
        new(nameof(ThemeColors.BorderSubtle), "Subtle border", "Surfaces and borders"),
        new(nameof(ThemeColors.BorderStrong), "Strong border", "Surfaces and borders"),
        new(nameof(ThemeColors.TextPrimary), "Primary text", "Typography"),
        new(nameof(ThemeColors.TextSecondary), "Secondary text", "Typography"),
        new(nameof(ThemeColors.TextMuted), "Muted text", "Typography"),
        new(nameof(ThemeColors.AccentPrimary), "Accent", "Accent and interaction"),
        new(nameof(ThemeColors.AccentHover), "Accent hover", "Accent and interaction"),
        new(nameof(ThemeColors.AccentPressed), "Accent pressed", "Accent and interaction"),
        new(nameof(ThemeColors.FocusRing), "Focus ring", "Accent and interaction"),
        new(nameof(ThemeColors.SelectionBackground), "Selection", "Accent and interaction"),
        new(nameof(ThemeColors.Success), "Success", "Status colours"),
        new(nameof(ThemeColors.Warning), "Warning", "Status colours"),
        new(nameof(ThemeColors.Danger), "Danger", "Status colours"),
    ];

    private readonly IThemeService themeService;
    private readonly IThemeFileDialogService fileDialogService;
    private ThemeDefinition workingTheme;
    private ThemePresetViewModel? selectedPreset;
    private string themeName = string.Empty;
    private ThemeCornerStyle cornerStyle;
    private ThemeDensity density;
    private double fontScalePercent = 100;
    private bool enableAnimations = true;
    private ThemeMotionIntensity motionIntensity;
    private bool followSystemPreference;
    private bool hasAcknowledgedContrastWarning;
    private bool isResetConfirmationOpen;
    private bool isDeleteConfirmationOpen;
    private bool isLoading;
    private string statusMessage = string.Empty;
    private string errorMessage = string.Empty;

    public AppearancePageViewModel(
        IThemeService themeService,
        IThemeFileDialogService fileDialogService)
    {
        this.themeService = themeService;
        this.fileDialogService = fileDialogService;
        workingTheme = themeService.ActiveTheme;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(Cancel);
        RequestResetCommand = new RelayCommand(() => IsResetConfirmationOpen = true);
        ConfirmResetCommand = new RelayCommand(ConfirmReset);
        DismissResetCommand = new RelayCommand(() => IsResetConfirmationOpen = false);
        CreateCommand = new RelayCommand(CreateCustom);
        DuplicateCommand = new RelayCommand(Duplicate);
        RequestDeleteCommand = new RelayCommand(
            () => IsDeleteConfirmationOpen = true,
            () => !workingTheme.IsBuiltIn);
        ConfirmDeleteCommand = new AsyncRelayCommand(DeleteAsync);
        DismissDeleteCommand = new RelayCommand(() => IsDeleteConfirmationOpen = false);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);

        RefreshPresets();
        LoadWorkingTheme(themeService.ActiveTheme);
    }

    public ObservableCollection<ThemePresetViewModel> Presets { get; } = [];

    public ObservableCollection<ColorTokenGroupViewModel> ColorGroups { get; } = [];

    public ObservableCollection<ContrastWarningViewModel> ContrastWarnings { get; } = [];

    public IReadOnlyList<ThemeCornerStyle> CornerStyles { get; } =
        Enum.GetValues<ThemeCornerStyle>();

    public IReadOnlyList<ThemeDensity> Densities { get; } =
        Enum.GetValues<ThemeDensity>();

    public IReadOnlyList<ThemeMotionIntensity> MotionIntensities { get; } =
        Enum.GetValues<ThemeMotionIntensity>();

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand RequestResetCommand { get; }

    public IRelayCommand ConfirmResetCommand { get; }

    public IRelayCommand DismissResetCommand { get; }

    public IRelayCommand CreateCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    public IRelayCommand RequestDeleteCommand { get; }

    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    public IRelayCommand DismissDeleteCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public ThemePresetViewModel? SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (value is null || !SetProperty(ref selectedPreset, value) || isLoading)
            {
                return;
            }

            LoadWorkingTheme(value.Theme);
            themeService.ApplyPreview(workingTheme);
            UpdateState();
        }
    }

    public string ThemeName
    {
        get => themeName;
        set
        {
            if (!SetProperty(ref themeName, value ?? string.Empty) || isLoading)
            {
                return;
            }

            EnsureEditable();
            ApplyWorkingChanges();
        }
    }

    public ThemeCornerStyle CornerStyle
    {
        get => cornerStyle;
        set
        {
            if (SetProperty(ref cornerStyle, value) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public ThemeDensity Density
    {
        get => density;
        set
        {
            if (SetProperty(ref density, value) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public double FontScalePercent
    {
        get => fontScalePercent;
        set
        {
            var normalized = Math.Clamp(Math.Round(value), 90, 120);
            if (SetProperty(ref fontScalePercent, normalized) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public bool EnableAnimations
    {
        get => enableAnimations;
        set
        {
            if (SetProperty(ref enableAnimations, value) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public ThemeMotionIntensity MotionIntensity
    {
        get => motionIntensity;
        set
        {
            if (SetProperty(ref motionIntensity, value) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public bool FollowSystemPreference
    {
        get => followSystemPreference;
        set
        {
            if (SetProperty(ref followSystemPreference, value) && !isLoading)
            {
                EnsureEditable();
                ApplyWorkingChanges();
            }
        }
    }

    public bool HasAcknowledgedContrastWarning
    {
        get => hasAcknowledgedContrastWarning;
        set
        {
            if (SetProperty(ref hasAcknowledgedContrastWarning, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsResetConfirmationOpen
    {
        get => isResetConfirmationOpen;
        set => SetProperty(ref isResetConfirmationOpen, value);
    }

    public bool IsDeleteConfirmationOpen
    {
        get => isDeleteConfirmationOpen;
        set => SetProperty(ref isDeleteConfirmationOpen, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            if (SetProperty(ref statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public string ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetProperty(ref errorMessage, value))
            {
                OnPropertyChanged(nameof(HasErrorMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsDirty { get; private set; }

    public bool HasContrastWarnings => ContrastWarnings.Count > 0;

    public bool HasValidationErrors =>
        string.IsNullOrWhiteSpace(ThemeName)
        || ColorGroups.SelectMany(group => group.Tokens).Any(token => !token.IsValid);

    public bool CanSave =>
        IsDirty
        && !HasValidationErrors
        && (!HasContrastWarnings || HasAcknowledgedContrastWarning);

    public bool CanDelete => !workingTheme.IsBuiltIn;

    public bool IsBuiltIn => workingTheme.IsBuiltIn;

    public string ActiveThemeCaption => workingTheme.IsBuiltIn ? "Built-in theme" : "Custom theme";

    public ThemeDefinition WorkingTheme => workingTheme;

    public void BeginPreviewSession()
    {
        themeService.BeginPreview();
        LoadWorkingTheme(themeService.ActiveTheme);
    }

    public void RequestCancel()
    {
        if (IsDirty)
        {
            Cancel();
        }
    }

    private void LoadWorkingTheme(ThemeDefinition theme)
    {
        isLoading = true;
        try
        {
            workingTheme = theme;
            themeName = theme.Name;
            cornerStyle = theme.CornerStyle;
            density = theme.Density;
            fontScalePercent = theme.Typography.Scale * 100;
            enableAnimations = theme.Motion.EnableAnimations;
            motionIntensity = theme.Motion.Intensity;
            followSystemPreference = theme.Motion.FollowSystemPreference;
            HasAcknowledgedContrastWarning = false;
            BuildColorGroups(theme);
            selectedPreset = Presets.FirstOrDefault(preset => preset.Theme.Id == theme.Id);
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            isLoading = false;
        }

        UpdateState();
    }

    private void BuildColorGroups(ThemeDefinition theme)
    {
        ColorGroups.Clear();
        var baseTheme = ResolveBaseTheme(theme);
        foreach (var group in ColorDescriptors.GroupBy(descriptor => descriptor.Group))
        {
            var tokens = group
                .Select(descriptor => new ColorTokenEditorViewModel(
                    descriptor.Key,
                    descriptor.Label,
                    descriptor.Group,
                    ReadColor(theme.Colors, descriptor.Key),
                    ReadColor(baseTheme.Colors, descriptor.Key),
                    OnColorChanged))
                .ToArray();
            ColorGroups.Add(new ColorTokenGroupViewModel(group.Key, tokens));
        }
    }

    private void OnColorChanged(ColorTokenEditorViewModel _)
    {
        if (isLoading)
        {
            return;
        }

        EnsureEditable();
        ApplyWorkingChanges();
    }

    private void EnsureEditable()
    {
        if (!workingTheme.IsBuiltIn)
        {
            return;
        }

        workingTheme = themeService.DuplicateTheme(workingTheme);
        themeName = workingTheme.Name;
        selectedPreset = null;
        OnPropertyChanged(nameof(ThemeName));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(IsBuiltIn));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ActiveThemeCaption));
        RequestDeleteCommand.NotifyCanExecuteChanged();
    }

    private void ApplyWorkingChanges()
    {
        ErrorMessage = string.Empty;
        var colors = CreateColors();
        if (colors is null || string.IsNullOrWhiteSpace(ThemeName))
        {
            UpdateState();
            return;
        }

        workingTheme = workingTheme with
        {
            Name = ThemeName.Trim(),
            Colors = colors,
            CornerStyle = CornerStyle,
            Density = Density,
            Spacing = ThemeSpacing.ForDensity(Density),
            Typography = workingTheme.Typography with { Scale = FontScalePercent / 100 },
            Motion = workingTheme.Motion with
            {
                EnableAnimations = EnableAnimations,
                Intensity = MotionIntensity,
                FollowSystemPreference = FollowSystemPreference,
            },
        };

        var errors = ThemeValidation.Validate(workingTheme);
        if (errors.Count == 0)
        {
            themeService.ApplyPreview(workingTheme);
        }

        UpdateState();
    }

    private ThemeColors? CreateColors()
    {
        var values = ColorGroups
            .SelectMany(group => group.Tokens)
            .ToDictionary(token => token.Key, token => token.Value);
        if (values.Count != ColorDescriptors.Length
            || ColorGroups.SelectMany(group => group.Tokens).Any(token => !token.IsValid))
        {
            return null;
        }

        return new ThemeColors
        {
            WindowBackground = values[nameof(ThemeColors.WindowBackground)],
            SidebarBackground = values[nameof(ThemeColors.SidebarBackground)],
            SurfacePrimary = values[nameof(ThemeColors.SurfacePrimary)],
            SurfaceSecondary = values[nameof(ThemeColors.SurfaceSecondary)],
            SurfaceElevated = values[nameof(ThemeColors.SurfaceElevated)],
            BorderSubtle = values[nameof(ThemeColors.BorderSubtle)],
            BorderStrong = values[nameof(ThemeColors.BorderStrong)],
            TextPrimary = values[nameof(ThemeColors.TextPrimary)],
            TextSecondary = values[nameof(ThemeColors.TextSecondary)],
            TextMuted = values[nameof(ThemeColors.TextMuted)],
            AccentPrimary = values[nameof(ThemeColors.AccentPrimary)],
            AccentHover = values[nameof(ThemeColors.AccentHover)],
            AccentPressed = values[nameof(ThemeColors.AccentPressed)],
            Success = values[nameof(ThemeColors.Success)],
            Warning = values[nameof(ThemeColors.Warning)],
            Danger = values[nameof(ThemeColors.Danger)],
            FocusRing = values[nameof(ThemeColors.FocusRing)],
            SelectionBackground = values[nameof(ThemeColors.SelectionBackground)],
        };
    }

    private void UpdateState()
    {
        ContrastWarnings.Clear();
        var colors = CreateColors();
        if (colors is not null)
        {
            foreach (var issue in ThemeContrast.Evaluate(colors))
            {
                ContrastWarnings.Add(new ContrastWarningViewModel(issue));
            }
        }

        if (!HasContrastWarnings)
        {
            HasAcknowledgedContrastWarning = false;
        }

        IsDirty = workingTheme != themeService.CommittedTheme;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasContrastWarnings));
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(WorkingTheme));
    }

    private async Task SaveAsync()
    {
        ApplyWorkingChanges();
        if (!CanSave)
        {
            ErrorMessage = HasContrastWarnings && !HasAcknowledgedContrastWarning
                ? "Review and acknowledge the contrast warning before saving."
                : "Correct the highlighted theme values before saving.";
            return;
        }

        try
        {
            await themeService.CommitAsync(workingTheme);
            StatusMessage = $"Saved {workingTheme.Name}.";
            ErrorMessage = string.Empty;
            RefreshPresets();
            LoadWorkingTheme(themeService.CommittedTheme);
        }
        catch (Exception exception) when (exception is ThemeDataException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private void Cancel()
    {
        themeService.CancelPreview();
        StatusMessage = "Preview changes cancelled.";
        ErrorMessage = string.Empty;
        RefreshPresets();
        LoadWorkingTheme(themeService.CommittedTheme);
    }

    private void ConfirmReset()
    {
        IsResetConfirmationOpen = false;
        LoadWorkingTheme(themeService.ResetTheme(workingTheme));
        themeService.ApplyPreview(workingTheme);
        UpdateState();
        StatusMessage = "Theme restored to its base values. Save to keep the reset.";
    }

    private void CreateCustom()
    {
        var created = themeService.CreateCustomTheme("My Theme", ResolveBaseTheme(workingTheme));
        LoadWorkingTheme(created);
        themeService.ApplyPreview(created);
        UpdateState();
    }

    private void Duplicate()
    {
        var duplicate = themeService.DuplicateTheme(workingTheme);
        LoadWorkingTheme(duplicate);
        themeService.ApplyPreview(duplicate);
        UpdateState();
    }

    private async Task DeleteAsync()
    {
        IsDeleteConfirmationOpen = false;
        try
        {
            await themeService.DeleteCustomThemeAsync(workingTheme);
            StatusMessage = "Custom theme deleted.";
            RefreshPresets();
            LoadWorkingTheme(themeService.CommittedTheme);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task ImportAsync()
    {
        var path = await fileDialogService.PickImportPathAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = await themeService.ImportThemeAsync(path);
            RefreshPresets();
            LoadWorkingTheme(imported);
            themeService.ApplyPreview(imported);
            StatusMessage = $"Imported {imported.Name}. Save changes to make it active.";
        }
        catch (Exception exception) when (exception is ThemeDataException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task ExportAsync()
    {
        var fileName = ThemeValidation.SanitizeFileName(workingTheme.Name) + ".json";
        var path = await fileDialogService.PickExportPathAsync(fileName);
        if (path is null)
        {
            return;
        }

        try
        {
            await themeService.ExportThemeAsync(workingTheme, path);
            StatusMessage = $"Exported {workingTheme.Name}.";
        }
        catch (Exception exception) when (exception is ThemeDataException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var theme in themeService.AvailableThemes)
        {
            Presets.Add(new ThemePresetViewModel(theme));
        }
    }

    private ThemeDefinition ResolveBaseTheme(ThemeDefinition theme)
    {
        var baseId = theme.IsBuiltIn ? theme.Id : theme.BaseThemeId;
        return themeService.AvailableThemes.FirstOrDefault(candidate => candidate.Id == baseId)
            ?? BuiltInThemes.Default;
    }

    private static string ReadColor(ThemeColors colors, string key) => key switch
    {
        nameof(ThemeColors.WindowBackground) => colors.WindowBackground,
        nameof(ThemeColors.SidebarBackground) => colors.SidebarBackground,
        nameof(ThemeColors.SurfacePrimary) => colors.SurfacePrimary,
        nameof(ThemeColors.SurfaceSecondary) => colors.SurfaceSecondary,
        nameof(ThemeColors.SurfaceElevated) => colors.SurfaceElevated,
        nameof(ThemeColors.BorderSubtle) => colors.BorderSubtle,
        nameof(ThemeColors.BorderStrong) => colors.BorderStrong,
        nameof(ThemeColors.TextPrimary) => colors.TextPrimary,
        nameof(ThemeColors.TextSecondary) => colors.TextSecondary,
        nameof(ThemeColors.TextMuted) => colors.TextMuted,
        nameof(ThemeColors.AccentPrimary) => colors.AccentPrimary,
        nameof(ThemeColors.AccentHover) => colors.AccentHover,
        nameof(ThemeColors.AccentPressed) => colors.AccentPressed,
        nameof(ThemeColors.Success) => colors.Success,
        nameof(ThemeColors.Warning) => colors.Warning,
        nameof(ThemeColors.Danger) => colors.Danger,
        nameof(ThemeColors.FocusRing) => colors.FocusRing,
        nameof(ThemeColors.SelectionBackground) => colors.SelectionBackground,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown theme colour token."),
    };

    private sealed record ColorTokenDescriptor(string Key, string Label, string Group);
}
