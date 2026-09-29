using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class ImagineCommandTests
{
    [Theory]
    [InlineData("a cat", "a cat", "1024x1024")]
    [InlineData("  a cat  ", "a cat", "1024x1024")]
    [InlineData("large a cat", "a cat", "1792x1024")]
    [InlineData("tall a cat", "a cat", "1024x1792")]
    [InlineData("  LARGE   a cat  ", "a cat", "1792x1024")]
    [InlineData("Tall a cat", "a cat", "1024x1792")]
    [InlineData("largely blue", "largely blue", "1024x1024")]
    [InlineData("tallest building", "tallest building", "1024x1024")]
    [InlineData("large tall tree", "tall tree", "1792x1024")]
    [InlineData("large", "large", "1024x1024")]
    [InlineData("tall", "tall", "1024x1024")]
    [InlineData("large   ", "", "1792x1024")]
    [InlineData("tall   ", "", "1024x1792")]
    [InlineData("   ", "", "1024x1024")]
    [InlineData("", "", "1024x1024")]
    [InlineData(null, "", "1024x1024")]
    public void ParsePrompt_PreservesSizeModifiersAndTrimsContent(string? input, string expectedPrompt, string expectedSize)
    {
        var (prompt, size) = ImagineCommand.ParsePrompt(input!);

        Assert.Equal(expectedPrompt, prompt);
        Assert.Equal(expectedSize, size.ToString());
    }
}
