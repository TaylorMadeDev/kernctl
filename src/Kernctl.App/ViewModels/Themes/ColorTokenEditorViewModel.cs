using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Themes;

namespace Kernctl.App.ViewModels.Themes;

public sealed class ColorTokenEditorViewModel : ObservableObject
{
    private readonly Action<ColorTokenEditorViewModel> changed;
    private string value;
    private Color pickerColor;

    public ColorTokenEditorViewModel(
        string key,
        string label,
        string group,
        string value,
        string baseValue,
        Action<ColorTokenEditorViewModel> changed)
    {
        Key = key;
        Label = label;
        Group = group;
        this.value = value;
        BaseValue = baseValue;
        this.changed = changed;
        pickerColor = Color.Parse(value);
        ResetCommand = new RelayCommand(Reset);
    }

    public string Key { get; }

    public string Label { get; }

    public string Group { get; }

    public string BaseValue { get; private set; }

    public IRelayCommand ResetCommand { get; }

    public string Value
    {
        get => value;
        set
        {
            var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!SetProperty(ref this.value, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(IsValid));
            if (ThemeColor.TryParse(normalized, out var parsed))
            {
                var color = Color.FromArgb(parsed.A, parsed.R, parsed.G, parsed.B);
                SetProperty(ref pickerColor, color, nameof(PickerColor));
            }

            changed(this);
        }
    }

    public Color PickerColor
    {
        get => pickerColor;
        set
        {
            if (SetProperty(ref pickerColor, value))
            {
                Value = new ThemeColor(value.A, value.R, value.G, value.B).ToHex();
            }
        }
    }

    public bool IsValid => ThemeColor.TryParse(Value, out _);

    public string ValidationMessage => IsValid
        ? string.Empty
        : "Use #RRGGBB or #AARRGGBB.";

    public void Update(string nextValue, string nextBaseValue)
    {
        BaseValue = nextBaseValue;
        value = nextValue;
        if (ThemeColor.TryParse(nextValue, out var parsed))
        {
            pickerColor = Color.FromArgb(parsed.A, parsed.R, parsed.G, parsed.B);
        }

        OnPropertyChanged(string.Empty);
    }

    private void Reset() => Value = BaseValue;
}
