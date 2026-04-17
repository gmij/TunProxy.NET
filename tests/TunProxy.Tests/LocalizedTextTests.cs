using TunProxy.Core.Localization;

namespace TunProxy.Tests;

public class LocalizedTextTests
{
    [Theory]
    [InlineData("zh-CN", "状态")]
    [InlineData("zh-TW", "状态")]
    [InlineData("en", "Status")]
    [InlineData("fr-FR", "Status")]
    public void Get_ReturnsExpectedTranslation(string culture, string expected)
    {
        var value = LocalizedText.Get("Nav.Status", culture);

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData(null, "en")]
    public void NormalizeCultureName_ReturnsSupportedCulture(string? culture, string expected)
    {
        var normalized = LocalizedText.NormalizeCultureName(culture);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void GetFrontendCatalog_ContainsLocalizedValues()
    {
        var catalog = LocalizedText.GetFrontendCatalog("zh-CN");

        Assert.Equal("保存配置", catalog["Page.Config.Save"]);
        Assert.Equal("日志", catalog["Nav.Logs"]);
        Assert.Contains("Page.Status.Title", catalog.Keys);
    }
}
