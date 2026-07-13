using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class CardProgressionFormatterTests
{
    [Fact]
    public void Format_UsesLevelForStandardMonsters()
    {
        string result = CardProgressionFormatter.Format(7, 0, 0);

        Assert.Equal("Level 7", result);
    }

    [Fact]
    public void Format_UsesRankForXyzMonsters()
    {
        string result = CardProgressionFormatter.Format(0, 4, 0);

        Assert.Equal("Rank 4", result);
    }

    [Fact]
    public void Format_UsesLinkRatingForLinkMonsters()
    {
        string result = CardProgressionFormatter.Format(0, 0, 3);

        Assert.Equal("Link 3", result);
    }

    [Fact]
    public void FormatCombatStats_OmitsDefenseForLinkMonsters()
    {
        string result = CardProgressionFormatter.FormatCombatStats(
            2500, 0, 3);

        Assert.Equal("ATK 2500", result);
    }

    [Fact]
    public void FormatCombatStats_IncludesDefenseForOtherMonsters()
    {
        string result = CardProgressionFormatter.FormatCombatStats(
            2500, 2100, 0);

        Assert.Equal("ATK 2500 DEF 2100", result);
    }
}
