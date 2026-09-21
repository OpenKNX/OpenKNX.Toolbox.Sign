using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign.Tests;

public class TranslationTemplateTests
{
    [Theory]
    [InlineData(" von 30", " of 30")]
    [InlineData(" von 16", " of 16")]
    [InlineData(" von 3", " of 3")]
    [InlineData(" von 10", " of 10")]
    [InlineData(" von 6", " of 6")]
    [InlineData(" von 8", " of 8")]
    public void ExpandedSuffix_PreservesCount(string source, string expected)
    {
        Assert.Equal(expected, Translate(source, new() { [" von %N%"] = " of %N%" }, "SuffixText"));
    }

    [Theory]
    [InlineData("Kanal 8: {{0:-}} / 50%", "Kanal %C%: {{0:-}} / 50%", "Channel %C%: {{0:-}} / 50%", "Channel 8: {{0:-}} / 50%")]
    [InlineData("ADC Input 3: {{0:-}}", "ADC Input %C%: {{0:-}}", "ADC Input %C%: {{0:-}}", "ADC Input 3: {{0:-}}")]
    [InlineData("BE A, Ausgang", "BE %Z%, Ausgang", "BI %Z%, Output", "BI A, Output")]
    [InlineData("Eingang: {{0:B}}", "Eingang: {{0:%Z%}}", "Input: {{0:%Z%}}", "Input: {{0:B}}")]
    [InlineData("Kanal 2 von 30, Kanal 2", "Kanal %C% von %N%, Kanal %C%", "Of %N%: channel %C%, channel %C%", "Of 30: channel 2, channel 2")]
    [InlineData("Kanal (2).", "Kanal (%C%).", "Channel (%C%).", "Channel (2).")]
    public void ExpandedTemplate_PreservesValuesAndRuntimeSyntax(string source, string key, string value, string expected)
    {
        Assert.Equal(expected, Translate(source, new() { [key] = value }));
    }

    [Fact]
    public void ExactMatch_TakesPrecedenceOverAmbiguousTemplates()
    {
        Assert.Equal("Exact", Translate("Kanal 8", new()
        {
            ["Kanal %N%"] = "Count %N%",
            ["Kanal %C%"] = "Channel %C%",
            ["Kanal 8"] = "Exact"
        }));
    }

    [Theory]
    [InlineData(" von acht")]
    [InlineData(" von 0")]
    [InlineData(" von -8")]
    [InlineData(" von 08")]
    [InlineData(" von 2147483648")]
    [InlineData(" von 8.5")]
    [InlineData(" von \u0668")]
    [InlineData("extra von 8")]
    [InlineData(" von 8 extra")]
    [InlineData(" von 8\n")]
    [InlineData(" von {{0:8}}")]
    public void NonCanonicalOrUnrelatedText_DoesNotMatch(string source)
    {
        Assert.Null(Translate(source, new() { [" von %N%"] = " of %N%" }));
    }

    [Theory]
    [InlineData("Kanal 2 und 3", "Kanal %C% und %C%", "Channel %C% and %C%")]
    [InlineData("Kanal x2y", "Kanal (%C%).", "Channel (%C%).")]
    [InlineData("Kanal 2: {{1:-}}", "Kanal %C%: {{0:-}}", "Channel %C%: {{0:-}}")]
    [InlineData("8", "%N%", "%N%")]
    [InlineData("Paar 123", "Paar %N%%C%", "Pair %N%%C%")]
    [InlineData("Paar 11111", "Paar %N%1%C%", "Pair %N%1%C%")]
    [InlineData("Kanal 02", "Kanal %CC%", "Channel %CC%")]
    [InlineData("Kanal 3", "Kanal %C+1%", "Channel %C+1%")]
    [InlineData("Kanal AA", "Kanal %ZZ%", "Channel %ZZ%")]
    [InlineData("Modul 8", "Modul %TT%", "Module %TT%")]
    public void UnsupportedOrInconsistentTemplates_DoNotGuess(string source, string key, string value)
    {
        Assert.Null(Translate(source, new() { [key] = value }));
    }

    [Fact]
    public void UnsupportedMacros_StillAllowExactLookup()
    {
        Assert.Equal("Module %TT%", Translate("Modul %TT%", new() { ["Modul %TT%"] = "Module %TT%" }));
    }

    [Fact]
    public void AmbiguousTemplates_ThrowBeforeChangingDocument()
    {
        var doc = CreateXml("Kanal 8", "Text");
        var original = new XDocument(doc);

        var error = Assert.Throws<InvalidDataException>(() => TranslationHelper.InjectTranslations(doc, new()
        {
            ["Kanal %N%"] = "Count %N%",
            ["Kanal %C%"] = "Channel %C%"
        }));

        Assert.Contains("Kanal 8", error.Message);
        Assert.Contains("Kanal %N%", error.Message);
        Assert.Contains("Kanal %C%", error.Message);
        Assert.True(XNode.DeepEquals(original, doc));
    }

    [Theory]
    [InlineData("Channel")]
    [InlineData("Channel %N%")]
    [InlineData("Channel %C% %Unknown%")]
    public void LostOrInventedBuildPlaceholders_Throw(string target)
    {
        Assert.Throws<InvalidDataException>(() => Translate("Kanal 8", new() { ["Kanal %C%"] = target }));
    }

    private static string? Translate(string source, Dictionary<string, string> translations, string attribute = "Text")
    {
        var doc = CreateXml(source, attribute);
        var parameter = doc.Descendants().Single(e => e.Name.LocalName == "Parameter");
        TranslationHelper.InjectTranslations(doc, translations);
        Assert.Equal(source, parameter.Attribute(attribute)!.Value);
        var translation = doc.Descendants().SingleOrDefault(e => e.Name.LocalName == "Translation");
        if (translation == null)
            return null;
        Assert.Equal(attribute, translation.Attribute("AttributeName")!.Value);
        Assert.Equal("P-1", translation.Parent!.Attribute("RefId")!.Value);
        Assert.Equal("APP-1", translation.Parent.Parent!.Attribute("RefId")!.Value);
        return translation.Attribute("Text")!.Value;
    }

    private static XDocument CreateXml(string source, string attribute)
    {
        XNamespace ns = "http://knx.org/xml/project/20";
        return new XDocument(new XElement(ns + "KNX",
            new XElement(ns + "ManufacturerData",
                new XElement(ns + "Manufacturer",
                    new XElement(ns + "ApplicationProgram", new XAttribute("Id", "APP-1"),
                        new XElement(ns + "Parameter", new XAttribute("Id", "P-1"), new XAttribute(attribute, source)))))));
    }
}
