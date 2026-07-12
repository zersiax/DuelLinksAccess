using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DeckEditPolicyTests
{
    [Fact]
    public void UseFilteredCollection_HonorsEmptyActiveFilter()
    {
        Assert.True(DeckEditPolicy.UseFilteredCollection(true));
    }

    [Fact]
    public void ResolveAddedCount_UsesExtraDeckWhenExtraCountChanged()
    {
        var result = DeckEditPolicy.ResolveAddedCount(
            20, 5, 20, 6);

        Assert.True(result.IsExtraDeck);
        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void ResolveAddedCount_DefaultsToMainDeck()
    {
        var result = DeckEditPolicy.ResolveAddedCount(
            20, 5, 21, 5);

        Assert.False(result.IsExtraDeck);
        Assert.Equal(21, result.Count);
    }

    [Theory]
    [InlineData(false, 19)]
    [InlineData(true, 4)]
    public void ResolveRemovedCount_UsesSourceZone(
        bool removedFromExtraDeck, int expectedCount)
    {
        var result = DeckEditPolicy.ResolveRemovedCount(
            removedFromExtraDeck, 19, 4);

        Assert.Equal(removedFromExtraDeck, result.IsExtraDeck);
        Assert.Equal(expectedCount, result.Count);
    }
}
