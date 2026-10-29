using System.Linq;
using Lingarr.Server.Services.Translation.Batch;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation.Batch;

public class BatchNumberedListParserTests
{
    [Fact]
    public void TryParse_NewlineList_ReadsPositionsAndLines()
    {
        const string text = """
            1. 某个人
            2. 你刚才看见我了吗？
            12. [喃喃声]
            13. [喃喃声]
            """;

        Assert.True(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Equal(4, items.Count);
        Assert.Equal(1, items[0].Position);
        Assert.Equal("某个人", items[0].Line);
        Assert.Equal(2, items[1].Position);
        Assert.Equal("你刚才看见我了吗？", items[1].Line);
        Assert.Equal(12, items[2].Position);
        Assert.Equal("[喃喃声]", items[2].Line);
        Assert.Equal(13, items[3].Position);
        Assert.Equal("[喃喃声]", items[3].Line);
    }

    [Fact]
    public void TryParse_InlineList_SplitsPackedItems()
    {
        const string text = "1. 某个人 2. 你刚才看见我了吗？ 3. 在水里呼吸时，用鼻子呼气。";

        Assert.True(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Equal(3, items.Count);
        Assert.Equal("某个人", items[0].Line);
        Assert.Equal("你刚才看见我了吗？", items[1].Line);
        Assert.Equal("在水里呼吸时，用鼻子呼气。", items[2].Line);
    }

    [Fact]
    public void TryParse_ChineseEnumerationComma_IsAccepted()
    {
        const string text = "1、你好\n2、世界";

        Assert.True(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Equal(["你好", "世界"], items.Select(item => item.Line));
    }

    [Fact]
    public void TryParse_ContinuationLines_JoinWithNewline()
    {
        const string text = """
            1. Hello
            world
            2. Foo
            """;

        Assert.True(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Equal(2, items.Count);
        Assert.Equal("Hello\nworld", items[0].Line);
        Assert.Equal("Foo", items[1].Line);
    }

    [Fact]
    public void TryParse_Preamble_IsIgnored()
    {
        const string text = """
            Here are the translations:
            1. Hola
            2. Mundo
            """;

        Assert.True(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Equal(2, items.Count);
        Assert.Equal("Hola", items[0].Line);
    }

    [Fact]
    public void TryParse_DecimalNumber_IsNotAnItem()
    {
        const string text = "The score was 1.5 overall.";

        Assert.False(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Empty(items);
    }

    [Fact]
    public void TryParse_UnnumberedProse_ReturnsFalse()
    {
        const string text = "这位是全智惠。 希望你们俩能成为好朋友。 你们俩同岁，要好好相处。";

        Assert.False(BatchNumberedListParser.TryParse(text, out var items));
        Assert.Empty(items);
    }

    [Fact]
    public void TryParse_MalformedJsonArray_ReturnsFalse()
    {
        const string text =
            """[{"position":1,"line":"Hola"},{"position":2,"line":"Él dijo "adiós" y se fue"}]""";

        Assert.False(BatchNumberedListParser.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.False(BatchNumberedListParser.TryParse("   ", out _));
        Assert.False(BatchNumberedListParser.TryParse("", out _));
    }
}
