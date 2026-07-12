using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DiagnosticPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldCollect_FollowsDebugMode(bool debugMode, bool expected)
    {
        Assert.Equal(expected, DiagnosticPolicy.ShouldCollect(debugMode));
    }
}
