using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class ScreenClassificationPolicyTests
{
    [Theory]
    [InlineData("DuelClient", false, GameScreen.Duel)]
    [InlineData("DuelCommonDialog", false, GameScreen.Dialog)]
    [InlineData("UnmappedRewardView", true, GameScreen.Dialog)]
    [InlineData("TutorialArrowPart", false, GameScreen.Other)]
    [InlineData("TutorialArrowPart", true, GameScreen.Dialog)]
    [InlineData("HtjsonPage", false, GameScreen.Notices)]
    [InlineData("Standby", false, GameScreen.Notices)]
    [InlineData("ShopCardDetail", false, GameScreen.CardDetail)]
    [InlineData("DeckCardDetail", false, GameScreen.CardDetail)]
    [InlineData("ShopLineup", false, GameScreen.Shop)]
    [InlineData(null, false, GameScreen.Other)]
    public void Classify_UsesSpecificNamesBeforeBroadCategories(
        string name, bool fromDialogManager, GameScreen expected)
    {
        Assert.Equal(expected,
            ScreenClassificationPolicy.Classify(name, fromDialogManager));
    }
}
