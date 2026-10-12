using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class CardProgressionFormatterTests
{
    [Fact]
    public void Format_UsesLevelForStandardMonsters()
    {
        string result = CardProgressionFormatter.Format(7, 0, 0);

        Assert.Equal("等级 7", result);
    }

    [Fact]
    public void Format_UsesRankForXyzMonsters()
    {
        string result = CardProgressionFormatter.Format(0, 4, 0);

        Assert.Equal("阶级 4", result);
    }

    [Fact]
    public void Format_UsesLinkRatingForLinkMonsters()
    {
        string result = CardProgressionFormatter.Format(0, 0, 3);

        Assert.Equal("连接标记 3", result);
    }

    [Fact]
    public void FormatCombatStats_OmitsDefenseForLinkMonsters()
    {
        string result = CardProgressionFormatter.FormatCombatStats(
            2500, 0, 3);

        Assert.Equal("攻击力 2500", result);
    }

    [Fact]
    public void FormatCombatStats_IncludesDefenseForOtherMonsters()
    {
        string result = CardProgressionFormatter.FormatCombatStats(
            2500, 2100, 0);

        Assert.Equal("攻击力 2500 守备力 2100", result);
    }
}
