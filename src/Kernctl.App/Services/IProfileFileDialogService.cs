namespace Kernctl.App.Services;

public interface IProfileFileDialogService
{
    Task<string?> PickExecutableAsync();

    Task<string?> PickImportPathAsync();

    Task<string?> PickExportPathAsync(string suggestedFileName);
}
