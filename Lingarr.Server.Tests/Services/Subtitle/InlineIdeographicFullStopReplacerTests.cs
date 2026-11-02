using System.Collections.Generic;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

public class InlineIdeographicFullStopReplacerTests
{
    [Fact]
    public void Apply_NullOrEmpty_DoesNothing()
    {
        InlineIdeographicFullStopReplacer.Apply((IList<string>?)null);
        InlineIdeographicFullStopReplacer.Apply((IEnumerable<SubtitleItem>?)null);
        InlineIdeographicFullStopReplacer.Apply(new List<string>());
    }

    [Theory]
    [InlineData("你好。我回来了", "你好 我回来了")]
    [InlineData("你好。我回来了。", "你好 我回来了。")]
    [InlineData("你好。我。回来了", "你好 我 回来了")]
    [InlineData("A。B。C。", "A B C。")]
    [InlineData("我回来了。", "我回来了。")]
    [InlineData("我回来了", "我回来了")]
    [InlineData("Hello. World.", "Hello. World.")]
    [InlineData("你好？我回来了", "你好？我回来了")]
    [InlineData("你好！我回来了", "你好！我回来了")]
    [InlineData("你好。。。我回来了", "你好。。。我回来了")]
    [InlineData("你好。。我回来了", "你好。。我回来了")]
    [InlineData("你好……我回来了", "你好……我回来了")]
    [InlineData("。", "。")]
    [InlineData("。。", "。。")]
    [InlineData("你好。 我回来了", "你好 我回来了")]
    [InlineData("他说。“走吧”", "他说 “走吧”")]
    [InlineData("“你好。我回来了”", "“你好。我回来了”")]
    [InlineData("「你好。我回来了」", "「你好。我回来了」")]
    [InlineData("『你好。我回来了』", "『你好。我回来了』")]
    [InlineData("「你好。」他说", "「你好。」他说")]
    [InlineData("\"你好。我回来了\"", "\"你好。我回来了\"")]
    [InlineData("{\\i1}你好。{\\i0}我回来了", "{\\i1}你好 {\\i0}我回来了")]
    [InlineData("<i>你好。</i>我回来了", "<i>你好 </i>我回来了")]
    [InlineData("你好。\\N我回来了", "你好\\N我回来了")]
    [InlineData("你好。我回来了。。。", "你好 我回来了。。。")]
    public void Apply_Lines_ReplacesIsolatedMidCueFullStops(string input, string expected)
    {
        List<string> lines = [input];

        InlineIdeographicFullStopReplacer.Apply(lines);

        Assert.Equal(expected, Assert.Single(lines));
    }

    [Fact]
    public void Apply_MultiLineCue_DeletesPeriodBeforeTheNextLine()
    {
        List<string> lines = ["你好。", "我回来了"];

        InlineIdeographicFullStopReplacer.Apply(lines);

        Assert.Equal(["你好", "我回来了"], lines);
    }

    [Fact]
    public void Apply_MultiLineCue_KeepsTrailingPeriodOnLastLine()
    {
        List<string> lines = ["你好。", "我回来了。"];

        InlineIdeographicFullStopReplacer.Apply(lines);

        Assert.Equal(["你好", "我回来了。"], lines);
    }

    [Fact]
    public void Apply_UnclosedQuote_LeavesInnerPeriod()
    {
        List<string> lines = ["“你好。我回来了"];

        InlineIdeographicFullStopReplacer.Apply(lines);

        Assert.Equal("“你好。我回来了", Assert.Single(lines));
    }

    [Fact]
    public void Apply_SubtitleItems_OnlyMutatesTranslatedLines()
    {
        var item = new SubtitleItem
        {
            Lines = ["源。文"],
            PlaintextLines = ["源。文"],
            TranslatedLines = ["你好。我回来了"]
        };

        InlineIdeographicFullStopReplacer.Apply([item]);

        Assert.Equal(["源。文"], item.Lines);
        Assert.Equal(["源。文"], item.PlaintextLines);
        Assert.Equal(["你好 我回来了"], item.TranslatedLines);
    }
}
