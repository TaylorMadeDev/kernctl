using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Kernctl.App.Services;

public sealed class ProfileFileDialogService : IProfileFileDialogService
{
    private static readonly FilePickerFileType ExecutableFileType = new("Windows application")
    {
        Patterns = ["*.exe"],
        MimeTypes = ["application/vnd.microsoft.portable-executable"],
    };

    private static readonly FilePickerFileType JsonProfileFileType = new("kernctl profile")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickExecutableAsync()
    {
        var files = await GetStorageProvider().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Assign a game executable",
            AllowMultiple = false,
            FileTypeFilter = [ExecutableFileType],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImportPathAsync()
    {
        var files = await GetStorageProvider().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import kernctl profile",
            AllowMultiple = false,
            FileTypeFilter = [JsonProfileFileType],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportPathAsync(string suggestedFileName)
    {
        var file = await GetStorageProvider().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export kernctl profile",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [JsonProfileFileType],
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
