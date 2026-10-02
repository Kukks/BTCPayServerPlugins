using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TokenAmountTests
{
    [Theory]
    [InlineData("63360000", 6, "63.36")]
    [InlineData("1", 6, "0.000001")]
    [InlineData("1000000", 6, "1")]
    [InlineData("0012", 0, "12")]
    [InlineData("123456789012345678901234567890", 18, "123456789012.34567890123456789")]
    public void Base_units_format_as_exact_decimals(string baseUnits, int decimals, string expected) =>
        Assert.Equal(expected, TokenAmount.Format(baseUnits, decimals));

    [Theory]
    [InlineData("63360000", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("63.36", false)]
    [InlineData("6.336e7", false)]
    [InlineData("-1", false)]
    [InlineData(" 1", false)]
    public void Only_positive_integers_are_base_units(string? value, bool expected) =>
        Assert.Equal(expected, TokenAmount.IsBaseUnits(value));

    [Fact]
    public void Base_units_fit_a_uint256()
    {
        Assert.True(TokenAmount.IsBaseUnits(new string('9', 78)));
        Assert.False(TokenAmount.IsBaseUnits(new string('9', 79)));
    }
}
