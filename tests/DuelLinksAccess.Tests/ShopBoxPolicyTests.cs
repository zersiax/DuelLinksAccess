using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class ShopBoxPolicyTests
{
    [Theory]
    [InlineData("主卡盒", true)]
    [InlineData("迷你卡盒", true)]
    [InlineData("超迷你卡盒", true)]
    [InlineData("主卡盒/快速决斗", true)]
    [InlineData("迷你卡盒/超速决斗", true)]
    [InlineData("Main BOX", true)]
    [InlineData("Mini BOX", true)]
    [InlineData("Super Mini BOX", true)]
    [InlineData("Selection BOX", true)]
    [InlineData("构组卡组", true)]
    [InlineData("预组卡组", true)]
    [InlineData("Structure Deck", true)]
    [InlineData("发布日期：2026年9月/快速决斗", true)]
    [InlineData("究极之龙", false)]
    [InlineData("THE ULTIMATE RISING", false)]
    [InlineData("闪耀赫赫之光的黑金暴龙！", false)]
    [InlineData("黑魔导之门", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGenericBoxCategory_IdentifiesCategories(string category, bool expected)
    {
        Assert.Equal(expected, ShopBoxPolicy.IsGenericBoxCategory(category));
    }
}
