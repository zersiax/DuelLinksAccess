using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class TutorialArrowPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, TutorialArrowShape.ClickToContinue)]
    [InlineData(false, true, false, false, TutorialArrowShape.UISelectablePointer)]
    [InlineData(true, false, true, false, TutorialArrowShape.WorldColliderPointer)]
    [InlineData(true, false, true, true, TutorialArrowShape.UISelectablePointer)]
    [InlineData(true, false, false, false, TutorialArrowShape.UISelectablePointer)]
    public void Classify_UsesTargetAndCameraEvidence(
        bool hasTarget,
        bool hasIpclick,
        bool worldCamera,
        bool hasUiGraphic,
        TutorialArrowShape expected)
    {
        Assert.Equal(expected, TutorialArrowPolicy.Classify(
            hasTarget, hasIpclick, worldCamera, hasUiGraphic));
    }

    [Fact]
    public void SafeDefault_NeverAutoClicks()
    {
        Assert.Equal(TutorialArrowShape.UISelectablePointer,
            TutorialArrowPolicy.SafeDefault);
    }
}
