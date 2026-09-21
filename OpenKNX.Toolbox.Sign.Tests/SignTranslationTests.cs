using System.Reflection;
using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign.Tests;

public class SignTranslationTests : IDisposable
{
    private const string AppId = "M-00FA_A-0001-01-0000";
    private const string HardwareId = "M-00FA_H-1";
    private const string CatalogId = "M-00FA_CS-1";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "OpenKNX.SignTests." + Guid.NewGuid().ToString("N"));

    public SignTranslationTests() => Directory.CreateDirectory(tempDir);

    public void Dispose() => Directory.Delete(tempDir, true);

    [Fact]
    public void SplitXml_InjectsVisibleElementsAndRoutesUnitsWithoutChangingSource()
    {
        string source = WriteSource();
        byte[] original = File.ReadAllBytes(source);
        var translations = new Dictionary<string, string>
        {
            ["Temperatur"] = "Temperature",
            ["Erweitertes Speichern"] = "Advanced saving",
            ["Taste"] = "Button",
            ["Zeile"] = "Row",
            ["Spalte"] = "Column",
            ["Anwendung"] = "Application",
            ["Geraet"] = "Device",
            ["Produkt"] = "Product",
            ["Katalog"] = "Catalog",
            ["Eintrag"] = "Item",
            ["Schalten"] = "Switch",
            ["Funktion"] = "Function",
            [" von %N%"] = " of %N%"
        };
        var inMemory = XDocument.Load(source);
        TranslationHelper.InjectTranslations(inMemory, translations);
        AssertTranslation(inMemory, AppId, AppId + "_PB-23", "Text", "Temperature");
        AssertTranslation(inMemory, AppId, AppId + "_PS-9", "Text", "Advanced saving");

        string output = Path.Combine(tempDir, "split");
        Directory.CreateDirectory(Path.Combine(output, "M-00FA"));
        Split(source, output, translations);

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(3, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        var app = XDocument.Load(Path.Combine(output, "M-00FA", AppId + ".xml"));
        AssertTranslation(app, AppId, AppId, "Name", "Application");
        AssertTranslation(app, AppId, AppId + "_PB-23", "Text", "Temperature");
        AssertTranslation(app, AppId, AppId + "_PS-9", "Text", "Advanced saving");
        AssertTranslation(app, AppId, AppId + "_B-1", "Text", "Button");
        AssertTranslation(app, AppId, AppId + "_PB-23_R-1", "Text", "Row");
        AssertTranslation(app, AppId, AppId + "_PB-23_C-1", "Text", "Column");
        AssertTranslation(app, AppId, AppId + "_P-1", "SuffixText", " of 30");
        AssertTranslation(app, AppId, AppId + "_O-1", "Text", "Existing switch");
        AssertTranslation(app, AppId, AppId + "_O-1", "FunctionText", "Function");
        Assert.Equal("Temperatur", app.Descendants(Ns + "ParameterBlock").Single().Attribute("Text")!.Value);
        Assert.Equal("Erweitertes Speichern", app.Descendants(Ns + "ParameterSeparator").Single().Attribute("Text")!.Value);
        Assert.Single(app.Descendants(Ns + "Language").Where(e => (string?)e.Attribute("Identifier") == "de-DE"));
        Assert.All(app.Descendants(Ns + "TranslationUnit"), e => Assert.Equal(AppId, (string?)e.Attribute("RefId")));

        var hardware = XDocument.Load(Path.Combine(output, "M-00FA", "Hardware.xml"));
        AssertTranslation(hardware, HardwareId, HardwareId, "Name", "Device");
        AssertTranslation(hardware, HardwareId, HardwareId + "_P-1", "Text", "Product");
        Assert.All(hardware.Descendants(Ns + "TranslationUnit"), e => Assert.Equal(HardwareId, (string?)e.Attribute("RefId")));
        var catalog = XDocument.Load(Path.Combine(output, "M-00FA", "Catalog.xml"));
        AssertTranslation(catalog, CatalogId, CatalogId, "Name", "Catalog");
        AssertTranslation(catalog, CatalogId, HardwareId + "_CI-1", "Name", "Item");
        Assert.All(catalog.Descendants(Ns + "TranslationUnit"), e => Assert.Equal(CatalogId, (string?)e.Attribute("RefId")));
    }

    [Fact]
    public void SplitXml_WithoutOptionalTranslations_PreservesExistingLanguages()
    {
        string source = WriteSource();
        byte[] original = File.ReadAllBytes(source);
        string output = Path.Combine(tempDir, "untranslated");
        Directory.CreateDirectory(Path.Combine(output, "M-00FA"));

        Split(source, output, null);

        Assert.Equal(original, File.ReadAllBytes(source));
        var app = XDocument.Load(Path.Combine(output, "M-00FA", AppId + ".xml"));
        AssertTranslation(app, AppId, AppId + "_O-1", "Text", "Existing switch");
        Assert.Equal(2, app.Descendants(Ns + "Translation").Count());
        Assert.Equal(" von 30", app.Descendants(Ns + "Parameter").Single().Attribute("SuffixText")!.Value);
    }

    [Fact]
    public void SplitXml_SaveFailure_LeavesSourceUnchanged()
    {
        string source = WriteSource();
        byte[] original = File.ReadAllBytes(source);

        var error = Assert.Throws<TargetInvocationException>(() =>
            Split(source, Path.Combine(tempDir, "missing-output"), new() { ["Temperatur"] = "Temperature" }));

        Assert.IsType<DirectoryNotFoundException>(error.InnerException);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("NOT JSON")]
    [InlineData("{\"translations\":{\"Valid\":\"English\",\"Invalid\":17}}")]
    [InlineData("{}")]
    [InlineData("{\"translations\":null}")]
    public async Task ExportKnxprod_InvalidTranslations_FaultsSyncAndAsyncWithoutOutput(string json)
    {
        string translations = Path.Combine(tempDir, "en.json");
        File.WriteAllText(translations, json);
        string output = Path.Combine(tempDir, "product.knxprod");
        string source = WriteSource();
        byte[] original = File.ReadAllBytes(source);

        var syncError = Assert.Throws<InvalidDataException>(() =>
            SignHelper.ExportKnxprod(tempDir, output, source, "", false, false, "", translations));
        var asyncError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SignHelper.ExportKnxprodAsync(tempDir, output, source, "", false, false, translationsPath: translations));

        Assert.Contains(translations, syncError.Message);
        Assert.Contains(translations, asyncError.Message);
        Assert.False(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task ExportKnxprod_MissingRequestedTranslations_FaultsWithoutOutput()
    {
        string output = Path.Combine(tempDir, "product.knxprod");
        string path = Path.Combine(tempDir, "missing");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            SignHelper.ExportKnxprodAsync(tempDir, output, WriteSource(), "", false, false, translationsPath: path));

        Assert.False(File.Exists(output));
    }

    private static void Split(string source, string output, Dictionary<string, string>? translations)
    {
        var split = typeof(SignHelper).GetMethod("SplitXml", BindingFlags.NonPublic | BindingFlags.Static)!;
        split.Invoke(null, new object?[] { source, output, translations });
    }

    private static void AssertTranslation(XDocument doc, string unitId, string elementId, string attribute, string text)
    {
        var language = doc.Descendants(Ns + "Language").Single(e => (string?)e.Attribute("Identifier") == "en-US");
        var unit = language.Elements(Ns + "TranslationUnit").Single(e => (string?)e.Attribute("RefId") == unitId);
        var element = unit.Elements(Ns + "TranslationElement").Single(e => (string?)e.Attribute("RefId") == elementId);
        var translation = element.Elements(Ns + "Translation").Single(e => (string?)e.Attribute("AttributeName") == attribute);
        Assert.Equal(text, (string?)translation.Attribute("Text"));
    }

    private string WriteSource()
    {
        string source = Path.Combine(tempDir, "source.xml");
        File.WriteAllText(source, $"""
            <KNX xmlns="{Ns}">
              <ManufacturerData>
                <Manufacturer RefId="M-00FA">
                  <Catalog>
                    <CatalogSection Id="{CatalogId}" Name="Katalog">
                      <CatalogItem Id="{HardwareId}_CI-1" Name="Eintrag" />
                    </CatalogSection>
                  </Catalog>
                  <Hardware>
                    <Hardware Id="{HardwareId}" Name="Geraet">
                      <Products><Product Id="{HardwareId}_P-1" Text="Produkt" /></Products>
                    </Hardware>
                  </Hardware>
                  <ApplicationPrograms>
                    <ApplicationProgram Id="{AppId}" Name="Anwendung">
                      <Static>
                        <Parameters><Parameter Id="{AppId}_P-1" Text="Unmatched" SuffixText=" von 30" /></Parameters>
                        <ComObjectTable><ComObject Id="{AppId}_O-1" Text="Schalten" FunctionText="Funktion" /></ComObjectTable>
                      </Static>
                      <Dynamic>
                        <ChannelIndependentBlock>
                          <ParameterBlock Id="{AppId}_PB-23" Text="Temperatur">
                            <Rows><Row Id="{AppId}_PB-23_R-1" Text="Zeile" /></Rows>
                            <Columns><Column Id="{AppId}_PB-23_C-1" Text="Spalte" /></Columns>
                            <ParameterSeparator Id="{AppId}_PS-9" Text="Erweitertes Speichern" />
                            <Button Id="{AppId}_B-1" Text="Taste" />
                          </ParameterBlock>
                        </ChannelIndependentBlock>
                      </Dynamic>
                    </ApplicationProgram>
                  </ApplicationPrograms>
                  <Languages>
                    <Language Identifier="en-US">
                      <TranslationUnit RefId="{AppId}">
                        <TranslationElement RefId="{AppId}_O-1"><Translation AttributeName="Text" Text="Existing switch" /></TranslationElement>
                      </TranslationUnit>
                    </Language>
                    <Language Identifier="de-DE">
                      <TranslationUnit RefId="{AppId}">
                        <TranslationElement RefId="{AppId}_O-1"><Translation AttributeName="Text" Text="Schalten" /></TranslationElement>
                      </TranslationUnit>
                    </Language>
                  </Languages>
                </Manufacturer>
              </ManufacturerData>
            </KNX>
            """);
        return source;
    }
}
