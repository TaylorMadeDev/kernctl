namespace Kernctl.App.Services;

public interface IThemeFileDialogService
{
    Task<string?> PickImportPathAsync();

    Task<string?> PickExportPathAsync(string suggestedFileName);
}
