# OpenKNX.Toolbox.Sign

Signs and packages KNX product XML. Optional English translations can be passed
as `translationsPath` to `SignHelper.ExportKnxprod` or `ExportKnxprodAsync`.

## Translation input

Omit `translationsPath` (or pass `null`/`""`) to retain generation without
translation injection. A supplied path must identify a JSON file or a directory
containing JSON files. Directories are scanned recursively; files are merged in
ordinal path order, with later files overriding earlier values. Bundle only the
intended target language: `_meta` does not select a language automatically.
The default injected language is `en-US`.

```json
{
  "translations": {
    "Temperatur": "Temperature",
    " von %N%": " of %N%"
  }
}
```

An explicitly requested missing/invalid path, directory without JSON files,
malformed JSON, missing/null `translations` object, or non-string value fails
generation. JSON/content errors include the filename in an
`InvalidDataException`; missing paths throw `FileNotFoundException`, and I/O
errors propagate. Async exports fault as well, so callers must report the
exception rather than success. Empty objects are valid, and empty string values
are intentionally skipped as untranslated entries.

## Expanded templates

Exact dictionary keys always take precedence. Otherwise, injection supports
these producer substitutions in complete source strings:

| Token | Accepted expanded value |
| --- | --- |
| `%N%` | Positive channel count, canonical decimal `Int32` |
| `%C%` | Positive channel number, canonical decimal `Int32` |
| `%Z%` | Single uppercase channel letter `A` through `Z` |

For example, ` von 30` matches ` von %N%` and becomes ` of 30`. Matching is
case-sensitive, anchored to the entire value, and preserves whitespace and
punctuation. Numeric captures cannot contain signs, leading zeroes, or decimal
points. A template must contain literal alphabetic text outside its placeholders.
Between placeholders there must be a literal character outside `0-9` and `A-Z`
to make the capture boundary unambiguous.

Repeated source placeholders must capture identical values. Target placeholders
may be repeated or reordered but must use exactly the same set of tokens.
Captured values are copied, not recalculated. ETS tokens such as `{{0:-}}` and
literal percent signs are not wildcards; their syntax is retained. A build token
inside an ETS default, such as `{{0:%Z%}}`, is substituted without changing the
surrounding ETS syntax.

No match leaves the attribute untranslated. Multiple matching templates fail
with the source text and conflicting keys in the error, even if they would
produce identical output. Mismatched source/target token sets also fail.
Neither failure partially mutates the document's language entries.

Other producer macros (padded channels, arithmetic, module types, named config
substitutions), placeholder-only keys, and templates without safe separators
are **exact-only**. Their expanded text needs concrete dictionary entries; the
signer does not infer missing build context. Bundling can retain supported
templates unchanged and should resolve dictionary conflicts with explicit
application overrides before calling the signer.

## XML behavior

The attribute allowlist remains `Text`, `Name`, `SuffixText`, and `FunctionText`.
Supported elements are `Enumeration`, `Parameter`, `ParameterRef`, `ComObject`,
`ComObjectRef`, `Channel`, `ChannelIndependentBlock`, `CatalogSection`,
`CatalogItem`, `Product`, `Message`, `ParameterBlock`, `ParameterSeparator`,
`Button`, `Row`, `Column`, `ApplicationProgram`, and `Hardware`. Elements need an
`Id` or `RefId` and an enclosing application, hardware, or catalog translation
unit. This does not translate arbitrary attributes or technical XML structures.

Existing translated attributes and other languages are preserved. Injection
occurs in memory before splitting; original German definitions and the cached
release XML are unchanged. No intermediate translated XML file is created or
included in the archive.

Regression tests cover loading, template matching, failure propagation, and
application/hardware/catalog split routing. These checks do not establish KNX
schema validity, successful signing, or correct ETS import/display; those need
end-to-end verification with ETS.
