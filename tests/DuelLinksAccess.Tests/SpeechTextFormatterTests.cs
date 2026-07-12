using System.Collections.Generic;
using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class SpeechTextFormatterTests
{
    [Fact]
    public void StripRichText_RemovesUnityFormattingTags()
    {
        string result = SpeechTextFormatter.StripRichText(
            "Tribute <color=#0000FF>2</color> monsters");

        Assert.Equal("Tribute 2 monsters", result);
    }

    [Fact]
    public void SubstituteDialogPlaceholders_ReplacesStringAndNumberInOrder()
    {
        var inserts = new Queue<string>(new[] { "Blue-Eyes", "2" });

        string result = SpeechTextFormatter.SubstituteDialogPlaceholders(
            "Choose %s, then Tribute %d monsters", inserts.Dequeue);

        Assert.Equal("Choose Blue-Eyes, then Tribute 2 monsters", result);
        Assert.Empty(inserts);
    }

    [Fact]
    public void SubstituteDialogPlaceholders_UsesEmptyTextWhenInsertIsMissing()
    {
        string result = SpeechTextFormatter.SubstituteDialogPlaceholders(
            "Choose %s", () => null);

        Assert.Equal("Choose ", result);
    }
}
