#nullable enable
using System.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Token amounts as integer strings in base units: never floating point.</summary>
public static class TokenAmount
{
    /// <summary>A positive integer of at most 78 digits, the width of a uint256.</summary>
    public static bool IsBaseUnits(string? value) =>
        value is { Length: > 0 and <= 78 } && value.All(c => c is >= '0' and <= '9') && value.Any(c => c != '0');

    public static string Format(string baseUnits, int decimals)
    {
        var digits = baseUnits.TrimStart('0').PadLeft(decimals + 1, '0');
        var whole = digits.Substring(0, digits.Length - decimals);
        var fraction = digits.Substring(digits.Length - decimals).TrimEnd('0');
        return fraction.Length == 0 ? whole : whole + "." + fraction;
    }
}
