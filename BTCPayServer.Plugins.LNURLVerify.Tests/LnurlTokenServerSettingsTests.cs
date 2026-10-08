using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlTokenServerSettingsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    public void A_project_id_is_32_hexadecimal_characters(string? id, bool valid) =>
        Assert.Equal(valid, LnurlTokenServerSettings.IsValidProjectId(id));
}
