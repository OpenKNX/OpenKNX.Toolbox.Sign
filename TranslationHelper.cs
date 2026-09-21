using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign;

public class TranslationFile
{
    [JsonPropertyName("_meta")]
    public TranslationMeta? Meta { get; set; }

    [JsonPropertyName("translations")]
    public Dictionary<string, string>? Translations { get; set; }
}

public class TranslationMeta
{
    [JsonPropertyName("module")]
    public string? Module { get; set; }

    [JsonPropertyName("source_language")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public static class TranslationHelper
{
    private static readonly HashSet<string> TranslatableElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enumeration", "Parameter", "ParameterRef", "ComObject", "ComObjectRef",
        "Channel", "ChannelIndependentBlock", "CatalogSection", "CatalogItem",
        "Product", "Message", "ParameterBlock", "ParameterSeparator", "Button",
        "Row", "Column", "ApplicationProgram", "Hardware"
    };

    private static readonly HashSet<string> TranslatableAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Name", "SuffixText", "FunctionText"
    };

    /// <summary>
    /// Loads translation dictionaries from a JSON file or directory of JSON files.
    /// Each JSON file should have a "translations" object mapping source text to target text.
    /// Files are merged in ordinal path order, with later files overriding earlier ones.
    /// Explicitly requested missing or invalid inputs throw; empty string values are skipped.
    /// </summary>
    public static Dictionary<string, string> LoadTranslations(string translationsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationsPath);
        var merged = new Dictionary<string, string>();
        var files = new List<string>();

        if (File.Exists(translationsPath))
        {
            files.Add(translationsPath);
        }
        else if (Directory.Exists(translationsPath))
        {
            files.AddRange(Directory.GetFiles(translationsPath, "*.json", SearchOption.AllDirectories));
            if (files.Count == 0)
                throw new InvalidDataException($"No translation JSON files found in '{translationsPath}'.");
        }
        else
            throw new FileNotFoundException($"Translation path does not exist: '{translationsPath}'.", translationsPath);

        foreach (string file in files.OrderBy(file => file, StringComparer.Ordinal))
        {
            TranslationFile? translationFile;
            try
            {
                string json = File.ReadAllText(file);
                translationFile = JsonSerializer.Deserialize<TranslationFile>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid translation JSON in '{file}': {ex.Message}", ex);
            }

            if (translationFile?.Translations == null)
                throw new InvalidDataException($"Translation file '{file}' must contain a 'translations' object.");

            foreach (var (key, value) in translationFile.Translations)
            {
                if (value == null)
                    throw new InvalidDataException($"Translation for '{key}' in '{file}' must be a string, not null.");
                if (value.Length > 0)
                    merged[key] = value;
            }
        }
        return merged;
    }

    /// <summary>
    /// Injects translation elements into the XML document's Languages block.
    /// Operates on the XDocument in memory — does not modify any file on disk.
    /// Exact keys take precedence over full-string %N%, %C%, and %Z% templates.
    /// Templates require literal text and unambiguous separators, preserve captured
    /// values and ETS runtime syntax, and throw on ambiguous matches or mismatched tokens.
    /// Other producer macros are exact-only; no build context is inferred.
    /// </summary>
    public static void InjectTranslations(XDocument xdoc, Dictionary<string, string> translations, string languageIdentifier = "en-US")
    {
        if (xdoc.Root == null) return;

        string ns = xdoc.Root.Name.NamespaceName;
        XName xnLanguages = XName.Get("Languages", ns);
        XName xnLanguage = XName.Get("Language", ns);
        XName xnTranslationUnit = XName.Get("TranslationUnit", ns);
        XName xnTranslationElement = XName.Get("TranslationElement", ns);
        XName xnTranslation = XName.Get("Translation", ns);

        XElement? manufacturer = xdoc.Root
            .Element(XName.Get("ManufacturerData", ns))?
            .Element(XName.Get("Manufacturer", ns));

        if (manufacturer == null)
        {
            Console.WriteLine("Warning: No Manufacturer element found, skipping translation injection.");
            return;
        }

        // Collect all translatable elements grouped by TranslationUnit RefId
        var translationUnits = new Dictionary<string, List<(string refId, string attrName, string text)>>();
        int count = 0;

        var matcher = new TranslationMatcher(translations);
        CollectTranslatableElements(xdoc.Root, matcher, translationUnits, ref count);

        if (count == 0)
        {
            Console.WriteLine("No matching translations found.");
            return;
        }

        // Find or create <Languages>
        XElement? languages = manufacturer.Element(xnLanguages);
        if (languages == null)
        {
            languages = new XElement(xnLanguages);
            manufacturer.Add(languages);
        }

        // Find or create <Language Identifier="en-US">
        XElement? language = languages.Elements(xnLanguage)
            .FirstOrDefault(e => e.Attribute("Identifier")?.Value == languageIdentifier);
        if (language == null)
        {
            language = new XElement(xnLanguage, new XAttribute("Identifier", languageIdentifier));
            languages.Add(language);
        }

        // Add TranslationUnits, merging with any existing ones
        foreach (var unit in translationUnits)
        {
            XElement? existingUnit = language.Elements(xnTranslationUnit)
                .FirstOrDefault(e => e.Attribute("RefId")?.Value == unit.Key);

            XElement transUnit = existingUnit ?? new XElement(xnTranslationUnit, new XAttribute("RefId", unit.Key));
            if (existingUnit == null)
                language.Add(transUnit);

            // Track existing TranslationElement RefIds and their attributes to avoid duplicates
            var existingElements = new Dictionary<string, XElement>();
            foreach (var te in transUnit.Elements(xnTranslationElement))
            {
                string? teRefId = te.Attribute("RefId")?.Value;
                if (teRefId != null) existingElements[teRefId] = te;
            }

            foreach (var (refId, attrName, text) in unit.Value)
            {
                if (!existingElements.TryGetValue(refId, out XElement? transElem))
                {
                    transElem = new XElement(xnTranslationElement, new XAttribute("RefId", refId));
                    transUnit.Add(transElem);
                    existingElements[refId] = transElem;
                }

                // Only add if this attribute isn't already translated
                bool attrExists = transElem.Elements(xnTranslation)
                    .Any(t => t.Attribute("AttributeName")?.Value == attrName);
                if (!attrExists)
                {
                    transElem.Add(new XElement(xnTranslation,
                        new XAttribute("AttributeName", attrName),
                        new XAttribute("Text", text)));
                }
            }
        }

        Console.WriteLine("Injected {0} translations for language '{1}' ({2} translation units).",
            count, languageIdentifier, translationUnits.Count);
    }

    private static void CollectTranslatableElements(XElement element,
        TranslationMatcher matcher,
        Dictionary<string, List<(string refId, string attrName, string text)>> translationUnits,
        ref int count)
    {
        if (TranslatableElements.Contains(element.Name.LocalName))
        {
            string? id = element.Attribute("Id")?.Value ?? element.Attribute("RefId")?.Value;
            if (!string.IsNullOrEmpty(id))
            {
                foreach (XAttribute attr in element.Attributes())
                {
                    if (TranslatableAttributes.Contains(attr.Name.LocalName) && !string.IsNullOrEmpty(attr.Value))
                    {
                        string? translatedText = matcher.Translate(attr.Value);
                        if (translatedText != null)
                        {
                            string? unitRefId = GetTranslationUnitRefId(element);
                            if (!string.IsNullOrEmpty(unitRefId))
                            {
                                if (!translationUnits.ContainsKey(unitRefId))
                                    translationUnits[unitRefId] = new List<(string, string, string)>();
                                translationUnits[unitRefId].Add((id, attr.Name.LocalName, translatedText));
                                count++;
                            }
                        }
                    }
                }
            }
        }

        foreach (XElement child in element.Elements())
            CollectTranslatableElements(child, matcher, translationUnits, ref count);
    }

    private sealed class TranslationMatcher
    {
        private static readonly Regex Placeholder = new(@"%[A-Za-z][A-Za-z0-9_+\-*]*%",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private readonly Dictionary<string, string> translations;
        private readonly List<(string source, string target, Regex pattern, string[] tokens)> templates = new();
        private readonly Dictionary<string, string?> cache = new(StringComparer.Ordinal);

        public TranslationMatcher(Dictionary<string, string> translations)
        {
            this.translations = translations;
            foreach (var (source, target) in translations)
            {
                var matches = Placeholder.Matches(source).Cast<Match>().ToArray();
                if (matches.Length == 0 || matches.Any(m => m.Value is not ("%N%" or "%C%" or "%Z%")))
                    continue;

                // Numeric/letter captures alone are too broad. Require literal prose and
                // separators between placeholders; all other producer macros stay exact-only.
                if (!Placeholder.Replace(source, "").Any(char.IsLetter) ||
                    matches.Zip(matches.Skip(1)).Any(pair =>
                        !source[(pair.First.Index + pair.First.Length)..pair.Second.Index]
                            .Any(c => !char.IsAsciiDigit(c) && c is not (>= 'A' and <= 'Z'))))
                    continue;

                string[] tokens = matches.Select(m => m.Value).Distinct().ToArray();
                var targetTokens = Placeholder.Matches(target).Select(m => m.Value).ToHashSet();
                if (!targetTokens.SetEquals(tokens))
                    throw new InvalidDataException($"Translation template '{source}' must preserve its build placeholders.");

                var pattern = new StringBuilder(@"\A");
                var seen = new HashSet<string>();
                int offset = 0;
                foreach (var match in matches)
                {
                    pattern.Append(Regex.Escape(source[offset..match.Index]));
                    string group = match.Value[1..^1];
                    pattern.Append(seen.Add(group)
                        ? $"(?<{group}>{(group == "Z" ? "[A-Z]" : "[1-9][0-9]{0,9}")})"
                        : $@"\k<{group}>");
                    offset = match.Index + match.Length;
                }
                pattern.Append(Regex.Escape(source[offset..])).Append(@"\z");
                templates.Add((source, target, new Regex(pattern.ToString(),
                    RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)), tokens));
            }
        }

        public string? Translate(string source)
        {
            if (translations.TryGetValue(source, out string? exact))
                return exact;
            if (cache.TryGetValue(source, out string? cached))
                return cached;

            string? result = null;
            string? matchedTemplate = null;
            foreach (var template in templates)
            {
                Match match = template.pattern.Match(source);
                if (!match.Success || template.tokens.Any(token =>
                    token != "%Z%" && !int.TryParse(match.Groups[token[1..^1]].Value, out _)))
                    continue;

                if (matchedTemplate != null)
                    throw new InvalidDataException(
                        $"Ambiguous translation for '{source}': templates '{matchedTemplate}' and '{template.source}'.");

                matchedTemplate = template.source;
                result = Placeholder.Replace(template.target, token => match.Groups[token.Value[1..^1]].Value);
            }
            cache[source] = result;
            return result;
        }
    }

    /// <summary>
    /// Walks up the XML tree to find the appropriate TranslationUnit container RefId.
    /// </summary>
    private static string? GetTranslationUnitRefId(XElement element)
    {
        XElement? current = element;
        while (current != null)
        {
            string name = current.Name.LocalName;
            if (name is "ApplicationProgram" or "Hardware" or "CatalogSection")
                return current.Attribute("Id")?.Value;

            if (name == "Catalog")
            {
                // For elements directly under Catalog, use first CatalogSection
                XElement? firstSection = current.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "CatalogSection");
                return firstSection?.Attribute("Id")?.Value;
            }

            if (name == "Product")
            {
                // Products are under Hardware — walk up to find it
                XElement? hw = current.Parent;
                while (hw != null && hw.Name.LocalName != "Hardware") hw = hw.Parent;
                return hw?.Attribute("Id")?.Value;
            }

            current = current.Parent;
        }
        return null;
    }
}
