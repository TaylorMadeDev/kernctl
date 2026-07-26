using Kernctl.Broker.Protocol;

namespace Kernctl.Broker;

public sealed record BrokerLaunchOptions(
    string PipeName,
    Guid SessionId,
    BrokerProcessIdentity ExpectedClient,
    string ExpectedClientSha256,
    string RequestedCapability,
    DateTimeOffset ExpiresAtUtc)
{
    public const string DiagnosticCapability = "diagnostics";

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out BrokerLaunchOptions? options,
        out string safeError)
    {
        options = null;
        safeError = "The administrator broker received invalid launch arguments.";
        if (arguments.Count == 0 || arguments.Count > 20 || arguments.Count % 2 != 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            if (!key.StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, arguments[index + 1]))
            {
                return false;
            }
        }

        var expectedKeys = new[]
        {
            "--pipe",
            "--session",
            "--client-pid",
            "--client-start-utc-ticks",
            "--client-session",
            "--client-path",
            "--client-sid",
            "--client-sha256",
            "--capability",
            "--expires-utc-ticks",
        };
        if (values.Count != expectedKeys.Length || expectedKeys.Any(key => !values.ContainsKey(key)))
        {
            return false;
        }

        if (!IsPipeName(values["--pipe"])
            || !Guid.TryParseExact(values["--session"], "N", out var sessionId)
            || sessionId == Guid.Empty
            || !int.TryParse(values["--client-pid"], out var clientPid)
            || clientPid <= 0
            || !long.TryParse(values["--client-start-utc-ticks"], out var clientStartTicks)
            || clientStartTicks <= 0
            || !int.TryParse(values["--client-session"], out var clientSession)
            || clientSession < 0
            || !IsAbsoluteFilePath(values["--client-path"])
            || !BrokerProtocolValidation.IsBounded(values["--client-sid"], 184)
            || !IsSha256(values["--client-sha256"])
            || values["--capability"] != DiagnosticCapability
            || !long.TryParse(values["--expires-utc-ticks"], out var expiryTicks))
        {
            return false;
        }

        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = new DateTimeOffset(expiryTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (expiresAtUtc <= nowUtc - BrokerProtocol.MaximumClockSkew
            || expiresAtUtc > nowUtc + BrokerProtocol.MaximumSessionLifetime)
        {
            safeError = "The administrator request has expired.";
            return false;
        }

        options = new(
            values["--pipe"],
            sessionId,
            new(
                clientPid,
                clientStartTicks,
                clientSession,
                Path.GetFullPath(values["--client-path"]),
                values["--client-sid"]),
            values["--client-sha256"].ToUpperInvariant(),
            values["--capability"],
            expiresAtUtc);
        return true;
    }

    private static bool IsPipeName(string value) =>
        value.Length is >= 48 and <= 96
        && value.StartsWith("kernctl-", StringComparison.Ordinal)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsAbsoluteFilePath(string value) =>
        BrokerProtocolValidation.IsBounded(value, BrokerProtocol.MaximumStringLength)
        && Path.IsPathFullyQualified(value)
        && !value.StartsWith(@"\\", StringComparison.Ordinal)
        && string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
