using System.Collections.Immutable;

namespace Kernctl.Core.Gaming;

public static class GameValidation
{
    public const int MaximumArguments = 64;
    public const int MaximumArgumentLength = 512;

    public static GameValidationResult ValidateLaunch(
        string? executablePath,
        string? workingDirectory,
        IEnumerable<string>? arguments,
        bool requireExistingExecutable = true)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var normalizedExecutable = NormalizeAbsolutePath(executablePath, "Executable", errors);
        var normalizedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? normalizedExecutable is null ? null : Path.GetDirectoryName(normalizedExecutable)
            : NormalizeAbsolutePath(workingDirectory, "Working directory", errors);

        if (normalizedExecutable is not null)
        {
            if (!string.Equals(
                    Path.GetExtension(normalizedExecutable),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Only direct Windows .exe launch targets are allowed.");
            }
            else if (requireExistingExecutable && !File.Exists(normalizedExecutable))
            {
                errors.Add("The selected executable no longer exists.");
            }

            AddLocationWarnings(normalizedExecutable, warnings);
        }

        if (normalizedWorkingDirectory is not null
            && requireExistingExecutable
            && !Directory.Exists(normalizedWorkingDirectory))
        {
            errors.Add("The working directory does not exist.");
        }

        ValidateArguments(arguments, errors);
        return new(
            errors.Count == 0,
            normalizedExecutable,
            normalizedWorkingDirectory,
            errors.ToImmutable(),
            warnings.ToImmutable());
    }

    public static ImmutableArray<string> ValidateArguments(IEnumerable<string>? arguments)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        ValidateArguments(arguments, errors);
        return errors.ToImmutable();
    }

    public static bool IsAllowedPriority(GameProcessPriority priority) =>
        priority is GameProcessPriority.Normal
            or GameProcessPriority.AboveNormal
            or GameProcessPriority.High;

    public static string NormalizeIdentityPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant();

    private static string? NormalizeAbsolutePath(
        string? value,
        string label,
        ImmutableArray<string>.Builder errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} is required.");
            return null;
        }

        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add($"{label} contains invalid control characters.");
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                errors.Add($"{label} must be an absolute local path.");
                return null;
            }

            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            errors.Add($"{label} is not a valid Windows path.");
            return null;
        }
    }

    private static void ValidateArguments(
        IEnumerable<string>? arguments,
        ImmutableArray<string>.Builder errors)
    {
        var values = arguments?.ToArray() ?? [];
        if (values.Length > MaximumArguments)
        {
            errors.Add($"A game may define at most {MaximumArguments} launch arguments.");
        }

        foreach (var argument in values)
        {
            if (argument.Length > MaximumArgumentLength)
            {
                errors.Add($"Each launch argument must be {MaximumArgumentLength} characters or fewer.");
                break;
            }

            if (argument.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                errors.Add("Launch arguments cannot contain control characters.");
                break;
            }
        }
    }

    private static void AddLocationWarnings(
        string executablePath,
        ImmutableArray<string>.Builder warnings)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath();
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = string.IsNullOrWhiteSpace(user) ? null : Path.Combine(user, "Downloads");

        if (executablePath.StartsWith(@"\\", comparison))
        {
            warnings.Add("This executable is on a network path. Confirm that you trust the location.");
        }

        if (!string.IsNullOrWhiteSpace(windows)
            && IsWithin(executablePath, windows))
        {
            warnings.Add("This executable is inside the Windows directory and is unusual for a game.");
        }

        if (IsWithin(executablePath, temp))
        {
            warnings.Add("This executable is in a temporary directory and may move or be untrusted.");
        }

        if (downloads is not null && IsWithin(executablePath, downloads))
        {
            warnings.Add("This executable is in Downloads. Confirm its publisher before launching it.");
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
