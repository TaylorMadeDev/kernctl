using System.Globalization;

namespace Kernctl.Core.Themes;

public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static bool TryParse(string? value, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#')
        {
            return false;
        }

        var digits = value.AsSpan(1);
        if (digits.Length is not (6 or 8)
            || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        color = digits.Length == 6
            ? new ThemeColor(255, (byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed)
            : new ThemeColor((byte)(parsed >> 24), (byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed);
        return true;
    }

    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
