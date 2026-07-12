using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DialogMixComposerTests
{
    [Fact]
    public void Compose_SubstitutesInsertFragmentsInOrder()
    {
        DialogMixFragment[] fragments =
        {
            new(DialogMixFragmentKind.Text, "%s gained %d counters."),
            new(DialogMixFragmentKind.Insert, "Kuriboh"),
            new(DialogMixFragmentKind.Insert, "2"),
        };

        Assert.Equal("Kuriboh gained 2 counters.",
            DialogMixComposer.Compose(fragments));
    }

    [Fact]
    public void Compose_DoesNotReuseInsertAlreadyAppended()
    {
        DialogMixFragment[] fragments =
        {
            new(DialogMixFragmentKind.Insert, "Kuriboh"),
            new(DialogMixFragmentKind.Text, " was selected: %s"),
        };

        Assert.Equal("Kuriboh was selected:",
            DialogMixComposer.Compose(fragments));
    }

    [Fact]
    public void Compose_AppendsUnusedInsertExactlyOnce()
    {
        DialogMixFragment[] fragments =
        {
            new(DialogMixFragmentKind.Text, "Selected: "),
            new(DialogMixFragmentKind.Insert, "Kuriboh"),
        };

        Assert.Equal("Selected: Kuriboh",
            DialogMixComposer.Compose(fragments));
    }

    [Fact]
    public void Compose_HandlesBreaksFormattingAndEmptyFragments()
    {
        DialogMixFragment[] fragments =
        {
            new(DialogMixFragmentKind.Text, "@3<b>First</b>"),
            new(DialogMixFragmentKind.Break, null),
            new(DialogMixFragmentKind.Ignore, "ignored"),
            new(DialogMixFragmentKind.Text, "  second"),
        };

        Assert.Equal("First second", DialogMixComposer.Compose(fragments));
        Assert.Null(DialogMixComposer.Compose(System.Array.Empty<DialogMixFragment>()));
    }
}
