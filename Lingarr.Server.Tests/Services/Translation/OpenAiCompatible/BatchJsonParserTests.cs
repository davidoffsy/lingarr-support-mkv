using Lingarr.Server.Exceptions;
using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation.OpenAiCompatible;

public class BatchJsonParserTests
{
    private readonly ILogger _logger = Mock.Of<ILogger>();

    [Fact]
    public void ParseStructuredWrapper_TranslationsObject_ReadsLines()
    {
        const string json = """{"translations":[{"position":1,"line":"Hola"},{"position":2,"line":"Mundo"}]}""";

        var result = BatchJsonParser.ParseStructuredWrapper(json, _logger);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
    }

    [Fact]
    public void ParseStructuredWrapper_NumberedList_RecoversWithoutThrowing()
    {
        const string text = "1. Hola\n2. Mundo";

        var result = BatchJsonParser.ParseStructuredWrapper(text, _logger);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
    }

    [Fact]
    public void ParseStructuredWrapper_MarkdownFence_StripsAndReads()
    {
        const string text = """
            ```json
            {"translations":[{"position":1,"line":"Hola"}]}
            ```
            """;

        var result = BatchJsonParser.ParseStructuredWrapper(text, _logger);

        Assert.Equal("Hola", result[1]);
    }

    [Fact]
    public void ParseStructuredWrapper_BareArray_StillFailsSoPolicyCanFallback()
    {
        const string json = """[{"position":1,"line":"Hola"}]""";

        var exception = Assert.Throws<TranslationParseException>(
            () => BatchJsonParser.ParseStructuredWrapper(json, _logger));

        Assert.Contains("translations", exception.Message);
    }

    [Fact]
    public void ParseStructuredWrapper_UnnumberedProse_Throws()
    {
        Assert.Throws<TranslationParseException>(
            () => BatchJsonParser.ParseStructuredWrapper("这位是全智惠。希望你们俩能成为好朋友。", _logger));
    }

    [Fact]
    public void ParseJsonArray_Array_ReadsLines()
    {
        const string json = """[{"position":1,"line":"Hola"},{"position":2,"line":"Mundo"}]""";

        var result = BatchJsonParser.ParseJsonArray(json, _logger);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
    }

    [Fact]
    public void ParseJsonArray_NumberedListWithSoundCues_IgnoresBrackets()
    {
        const string text = """
            1. 某个人
            2. 你刚才看见我了吗？
            12. [喃喃声]
            13. [喃喃声]
            """;

        var result = BatchJsonParser.ParseJsonArray(text, _logger);

        Assert.Equal("某个人", result[1]);
        Assert.Equal("你刚才看见我了吗？", result[2]);
        Assert.Equal("[喃喃声]", result[12]);
        Assert.Equal("[喃喃声]", result[13]);
    }

    [Fact]
    public void ParseJsonArray_InlineNumberedList_SplitsPackedItems()
    {
        const string text = "1. Hola 2. Mundo";

        var result = BatchJsonParser.ParseJsonArray(text, _logger);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
    }

    [Fact]
    public void ParseJsonArray_ChattyArray_ExtractsFirstArray()
    {
        const string text = """Here you go: [{"position":1,"line":"Hola"}] thanks.""";

        var result = BatchJsonParser.ParseJsonArray(text, _logger);

        Assert.Equal("Hola", result[1]);
    }

    [Fact]
    public void ParseJsonArray_UnnumberedProse_Throws()
    {
        Assert.Throws<TranslationParseException>(
            () => BatchJsonParser.ParseJsonArray("这位是全智惠。希望你们俩能成为好朋友。", _logger));
    }
}
