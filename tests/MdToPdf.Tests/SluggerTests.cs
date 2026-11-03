namespace MdToPdf.Tests;

public sealed class SluggerTests
{
    private static readonly string[] Repeats = ["Intro", "Intro", "Intro", "Other"];
    private static readonly string[] Clashing = ["a", "a", "a 1"];

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("C# & .NET!", "c--net")]
    [InlineData("snake_case-name", "snake_case-name")]
    [InlineData("Åäö Ÿ", "åäö-ÿ")]
    public void Slug(string text, string slug) => Assert.Equal(slug, Slugger.Slug(text));

    [Fact]
    public void RepeatsGetNumbered()
    {
        var slugger = new Slugger();
        Assert.Equal(["intro", "intro-1", "intro-2", "other"], Repeats.Select(slugger.Unique));
    }

    [Fact]
    public void NumberedSlugDoesNotClashWithAHeadingOfThatName()
    {
        var slugger = new Slugger();
        var slugs = Clashing.Select(slugger.Unique).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }
}
