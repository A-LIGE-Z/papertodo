using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class AppTypography
{
    private const string SymbolFallback = "Segoe UI Symbol, Segoe UI Emoji";
    private const string DefaultContentFontFamilyName = "Microsoft YaHei UI, Segoe UI, Microsoft YaHei, Segoe UI Symbol, Segoe UI Emoji";
    private const string DefaultCodeFontFamilyName = "Cascadia Mono, Consolas, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji";

    private static string _preset = UiFontPresets.Default;
    private static string _systemFontFamilyName = "";
    private static FontFamily? _customFontFamily;

    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);

    public static IReadOnlyList<string> SystemFontFamilyNames => LoadSystemFontFamilyNames();

    public static FontFamily UiFontFamily => ResolveSystemFontFamily() ?? _customFontFamily ?? ResolveUiFontFamily();

    public static FontFamily ContentFontFamily => ResolveSystemFontFamily() ?? _customFontFamily ?? ResolveContentFontFamily();

    public static FontFamily CodeFontFamily => new(DefaultCodeFontFamilyName);

    public static FontFamily SymbolFontFamily { get; } = new(SymbolFallback);

    public static TextFormattingMode TextFormattingMode => TextFormattingMode.Display;
    public static TextRenderingMode TextRenderingMode => TextRenderingMode.ClearType;
    public static TextHintingMode TextHintingMode => TextHintingMode.Fixed;

    public static bool HasCustomFont => _customFontFamily != null;

    public static void Configure(string? preset, string? systemFontFamilyName = null)
    {
        _preset = UiFontPresets.Normalize(preset);
        _systemFontFamilyName = NormalizeSystemFontFamilyName(systemFontFamilyName);
        _customFontFamily = string.IsNullOrWhiteSpace(_systemFontFamilyName)
            ? TryLoadCustomFontFamily()
            : null;
    }

    public static string NormalizeSystemFontFamilyName(string? value)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return SystemFontFamilyNames.FirstOrDefault(
            name => string.Equals(name, text, StringComparison.CurrentCultureIgnoreCase)) ?? "";
    }

    private static IReadOnlyList<string> LoadSystemFontFamilyNames()
    {
        return Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Concat(RegistryFontFamilyNames())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> RegistryFontFamilyNames()
    {
        const string fontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(fontsKeyPath);
            if (key == null)
            {
                continue;
            }

            foreach (var valueName in key.GetValueNames())
            {
                foreach (var familyName in SplitRegistryFontFamilyName(valueName))
                {
                    yield return familyName;
                }
            }
        }
    }

    private static IEnumerable<string> SplitRegistryFontFamilyName(string valueName)
    {
        var text = valueName.Trim();
        var typeMarker = text.IndexOf(" (", StringComparison.Ordinal);
        if (typeMarker > 0)
        {
            text = text[..typeMarker];
        }

        foreach (var name in text.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static FontFamily? ResolveSystemFontFamily()
    {
        return string.IsNullOrWhiteSpace(_systemFontFamilyName)
            ? null
            : new FontFamily(_systemFontFamilyName);
    }

    public static void ApplyTextRendering(DependencyObject target)
    {
        TextOptions.SetTextFormattingMode(target, TextFormattingMode);
        TextOptions.SetTextRenderingMode(target, TextRenderingMode);
        TextOptions.SetTextHintingMode(target, TextHintingMode);
    }

    private static FontFamily ResolveUiFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            UiFontPresets.DengXian => new FontFamily($"Segoe UI, DengXian, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            _ => DefaultUiFontFamily()
        };
    }

    private static FontFamily ResolveContentFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            UiFontPresets.DengXian => new FontFamily($"Segoe UI, DengXian, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            _ => new FontFamily(DefaultContentFontFamilyName)
        };
    }

    private static FontFamily DefaultUiFontFamily()
    {
        var cultureName = CultureInfo.CurrentUICulture.Name;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        return language switch
        {
            "zh" when cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
                => new FontFamily($"Segoe UI, Microsoft JhengHei UI, Microsoft JhengHei, Microsoft YaHei UI, Microsoft YaHei, {SymbolFallback}"),
            "zh" => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, {SymbolFallback}"),
            "ja" => new FontFamily($"Segoe UI, Yu Gothic UI, Meiryo, {SymbolFallback}"),
            "ko" => new FontFamily($"Segoe UI, Malgun Gothic, {SymbolFallback}"),
            _ => new FontFamily($"Segoe UI, {SymbolFallback}")
        };
    }

    private static FontFamily? TryLoadCustomFontFamily()
    {
        foreach (var path in CustomFontCandidates())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var fontUri = new Uri(path, UriKind.Absolute);
                var glyphTypeface = new GlyphTypeface(fontUri);
                var familyName = PreferredFamilyName(glyphTypeface);
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                var baseUri = new Uri(AppendDirectorySeparator(directory), UriKind.Absolute);
                return new FontFamily(baseUri, $"./#{familyName}");
            }
            catch
            {
                // Invalid or unsupported custom fonts must not affect startup.
            }
        }

        return null;
    }

    private static IEnumerable<string> CustomFontCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.ttf");
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.otf");
    }

    private static string PreferredFamilyName(GlyphTypeface glyphTypeface)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (glyphTypeface.Win32FamilyNames.TryGetValue(culture, out var localized))
        {
            return localized;
        }

        var neutral = culture.TwoLetterISOLanguageName;
        foreach (var pair in glyphTypeface.Win32FamilyNames)
        {
            if (pair.Key.TwoLetterISOLanguageName == neutral)
            {
                return pair.Value;
            }
        }

        if (glyphTypeface.Win32FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-us"), out var english))
        {
            return english;
        }

        return glyphTypeface.Win32FamilyNames.Values.FirstOrDefault() ?? "";
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
