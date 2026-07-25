using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Kernctl.App.Services;

public sealed class ThemeFileDialogService : IThemeFileDialogService
{
    private static readonly FilePickerFileType JsonThemeFileType = new("kernctl theme")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickImportPathAsync()
    {
        var provider = GetStorageProvider();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import kernctl theme",
            AllowMultiple = false,
            FileTypeFilter = [JsonThemeFileType],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportPathAsync(string suggestedFileName)
    {
        var provider = GetStorageProvider();
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export kernctl theme",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [JsonThemeFileType],
        });
        return file?.TryGetLocalPath();
    }

    private static IStorageProvider GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktop)
        {
            throw new InvalidOperationException("The kernctl window is unavailable.");
        }

        return desktop.MainWindow.StorageProvider;
    }
}
