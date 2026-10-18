using System.Globalization;
using OpenVideoSmartShare.Localization;

namespace OpenVideoSmartShare.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("it-IT", "it")]
    [InlineData("it-CH", "it")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("nb-NO", "nb")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("ar-SA", "en")]
    [InlineData("vi-VN", "en")]
    public void DetectsSystemLanguageWithEnglishFallback(string culture, string expected)
    {
        Assert.Equal(expected, L.Resolve("auto", CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void ExplicitChoiceWins()
    {
        Assert.Equal("fr", L.Resolve("fr", CultureInfo.GetCultureInfo("it-IT")));
    }

    [Theory]
    [InlineData("it", "en-US", "it", "18/10/2026")]
    [InlineData("it", "it-CH", "it-CH", "18.10.2026")]
    [InlineData("en", "it-IT", "en", "10/18/2026")]
    [InlineData("de", "en-GB", "de", "18.10.2026")]
    public void DatesFollowTheUiLanguage(string code, string regional, string expected, string date)
    {
        var culture = L.CultureFor(code, CultureInfo.GetCultureInfo(regional));
        Assert.Equal(expected, culture.Name);
        Assert.Equal(date, new DateTime(2026, 10, 18).ToString("d", culture));
    }

    [Fact]
    public void EveryLanguageTranslatesEveryKey()
    {
        var english = L.Load("en");
        Assert.NotEmpty(english);
        foreach (var language in L.Languages)
        {
            var table = L.Load(language.Code);
            var missing = english.Keys.Where(k => !table.ContainsKey(k) || string.IsNullOrWhiteSpace(table[k])).ToList();
            Assert.True(missing.Count == 0, $"{language.Code} misses: {string.Join(", ", missing)}");
            foreach (var (key, value) in english)
            {
                // Composite format placeholders must survive translation.
                for (var i = 0; i < 3; i++)
                {
                    var placeholder = "{" + i + "}";
                    Assert.True(value.Contains(placeholder) == table[key].Contains(placeholder),
                        $"{language.Code}/{key}: placeholder {placeholder} mismatch");
                }
            }
        }
    }
}
