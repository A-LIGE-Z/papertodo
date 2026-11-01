using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaperTodo;

public static class ColorSchemes
{
    public const string SiYuan = "siyuan";
    public const string Warm = "warm";
    public const string Ink = "ink";
    public const string Forest = "forest";
    public const string Rose = "rose";

    public static readonly string[] All = { SiYuan, Warm, Ink, Forest, Rose };

    public static bool IsValid(string? id) => id is SiYuan or Warm or Ink or Forest or Rose;

    public static string Normalize(string? id) => IsValid(id) ? id! : SiYuan;
}

public static class Theme
{
    // 一套主题的全部基色。半透明叠加色（hover / 拖放 / 标题栏 / 删除区）
    // 不进表，统一用 Tint / Danger 在基色上派生，保证一处定义、整体一致。
    private sealed class Palette
    {
        public Color Paper;        // 纸面背景
        public Color PaperBorder;  // 纸面描边
        public Color Text;         // 正文
        public Color WeakText;     // 弱化文字（完成项、提示、次要信息）
        public Color Active;       // 强调（置顶、选中、勾选）
        public Color Code;         // 行内代码 / 代码块背景
        public Color QuoteBorder;  // 引用左轨
        public Color Link;         // 超链接
        public Color CheckBox;     // 待办勾选框描边
        public Color Tint;         // 暖色叠加基（hover、拖放、标题栏底纹的来源）
        public Color Danger;       // 删除 / 警示
    }

    private static readonly Dictionary<string, (Palette Light, Palette Dark)> Schemes = BuildSchemes();

    // 颜色 → frozen 画刷的全局缓存。颜色到画刷是恒定映射，跨主题切换无需清空。
    private static readonly Dictionary<uint, SolidColorBrush> BrushCache = new();

    private static bool? _isDarkCache;
    private static string? _schemeCache;
    private static Palette? _paletteCache;

    /// <summary>主题或配色族变化后调用，使下一次取色重新解析。</summary>
    public static void Invalidate()
    {
        _isDarkCache = null;
        _schemeCache = null;
        _paletteCache = null;
        if (System.Windows.Application.Current is { } app)
        {
            app.Resources["PaperScrollThumbBrush"] = ScrollThumbBrush;
            app.Resources["PaperScrollThumbHoverBrush"] = ScrollThumbBrush;
        }
    }

    public static bool IsDark
    {
        get
        {
            if (_isDarkCache is bool cached)
            {
                return cached;
            }

            var theme = AppController.Current?.State?.Theme;
            bool dark = theme == "system" ? IsSystemDark() : theme == "dark";
            _isDarkCache = dark;
            return dark;
        }
    }

    private static string CurrentScheme => _schemeCache ??= ColorSchemes.Normalize(AppController.Current?.State?.ColorScheme);

    private static Palette Current
    {
        get
        {
            if (_paletteCache != null)
            {
                return _paletteCache;
            }

            var pair = Schemes.TryGetValue(CurrentScheme, out var s) ? s : Schemes[ColorSchemes.SiYuan];
            _paletteCache = ApplyCustomAccent(Clone(IsDark ? pair.Dark : pair.Light));
            return _paletteCache;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null && key.GetValue("AppsUseLightTheme") is int i)
            {
                return i == 0;
            }
        }
        catch
        {
            // Fallback to light
        }
        return false;
    }

    // ---- 基色画刷 ----
    public static Brush PaperBrush => Solid(Current.Paper);
    public static Brush PaperBorderBrush => Solid(Current.PaperBorder);
    public static Brush TextBrush => Solid(Current.Text);
    public static Brush WeakTextBrush => Solid(Current.WeakText);
    public static Brush BrightWeakTextBrush => Solid(IsDark ? Lighten(Current.WeakText, 0.22) : Current.WeakText);
    public static Brush ActiveBrush => Solid(Current.Active);
    public static Brush CodeBrush => Solid(Current.Code);
    public static Brush QuoteBorderBrush => Solid(Current.QuoteBorder);
    public static Brush LinkBrush => Solid(Current.Link);
    public static Brush CheckBoxBorderBrush => Solid(Current.CheckBox);
    public static Brush DangerBrush => Solid(Current.Danger);

    // SiYuan's daylight / midnight semantic surfaces. Existing paper palettes and
    // custom colors retain their stored identities and derive the same hierarchy.
    private static bool UsesSiYuanColors => CurrentScheme == ColorSchemes.SiYuan &&
        string.IsNullOrWhiteSpace(AppController.Current?.State?.CustomThemeColorHex);
    public static Brush SurfaceBrush => Solid(UsesSiYuanColors
        ? (IsDark ? Color.FromRgb(44, 44, 44) : Color.FromRgb(246, 246, 246))
        : Mix(Current.Paper, Current.Text, IsDark ? 0.055 : 0.035));
    public static Brush WorkspaceBrush => Solid(UsesSiYuanColors
        ? (IsDark ? Color.FromRgb(46, 50, 54) : Color.FromRgb(235, 236, 240))
        : Mix(Current.Paper, Current.Text, 0.075));
    public static Brush OnActiveBrush => Solid(Colors.White);
    public static Brush SelectionBrush => Solid(WithAlpha(Current.Active, (byte)(IsDark ? 61 : 31)));
    public static Brush HoverBrush => Solid(Color.FromArgb((byte)(IsDark ? 26 : 19),
        (byte)(IsDark ? 255 : 0), (byte)(IsDark ? 255 : 0), (byte)(IsDark ? 255 : 0)));
    public static Brush ScrollThumbBrush => Solid(IsDark ? Color.FromRgb(154, 160, 166) : Color.FromRgb(95, 99, 104));
    public static Brush CapsuleFocusBorderBrush => Solid(Mix(Current.Active, Current.Text, IsDark ? 0.38 : 0.08));

    // ---- 派生画刷 ----
    /// <summary>在当前主题的暖色叠加基上按 alpha 取一层半透明画刷。</summary>
    public static Brush Tint(byte alpha) => Solid(WithAlpha(Current.Tint, alpha));

    /// <summary>在当前主题的警示色上按 alpha 取一层半透明画刷。</summary>
    public static Brush Danger(byte alpha) => Solid(WithAlpha(Current.Danger, alpha));

    /// <summary>警示按钮的悬停态：在警示色上提亮，作为实色悬停反馈。</summary>
    public static Brush DangerHoverBrush => Solid(Lighten(Current.Danger, 0.14));

    /// <summary>Markdown 浏览态淡化标记用的弱前景（正文色的低透明版）。</summary>
    public static Brush SyntaxFadeBrush => Solid(WithAlpha(Current.Text, (byte)(IsDark ? 78 : 72)));

    // 勾选框三态：hover 描边朝正文色靠（浅色变深、深色变亮，方向自适应）；
    // active hover 在强调色上压暗；未选 hover 底用极淡叠加。
    public static Brush CheckBoxHoverBorderBrush => Solid(Mix(Current.CheckBox, Current.Text, 0.35));
    public static Brush CheckBoxActiveHoverBrush => Solid(Darken(Current.Active, 0.12));
    public static Brush CheckBoxUncheckedHoverBgBrush => Tint(20);

    private static SolidColorBrush Solid(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(c);
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color Darken(Color c, double t) => Mix(c, Color.FromRgb(0, 0, 0), t);

    private static Color Lighten(Color c, double t) => Mix(c, Color.FromRgb(255, 255, 255), t);

    public static string NormalizeCustomThemeColorHex(string? value)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (!text.StartsWith("#", StringComparison.Ordinal))
        {
            text = "#" + text;
        }

        return TryParseHexColor(text, out _) ? text.ToUpperInvariant() : "";
    }

    public static bool TryParseHexColor(string? value, out Color color)
    {
        color = default;
        var text = (value ?? "").Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        if (text.Length != 6 ||
            !byte.TryParse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static Palette Clone(Palette source)
    {
        return new Palette
        {
            Paper = source.Paper,
            PaperBorder = source.PaperBorder,
            Text = source.Text,
            WeakText = source.WeakText,
            Active = source.Active,
            Code = source.Code,
            QuoteBorder = source.QuoteBorder,
            Link = source.Link,
            CheckBox = source.CheckBox,
            Tint = source.Tint,
            Danger = source.Danger
        };
    }

    private static Palette ApplyCustomAccent(Palette palette)
    {
        if (!TryParseHexColor(AppController.Current?.State?.CustomThemeColorHex, out var accent))
        {
            return palette;
        }

        var active = IsDark
            ? (RelativeLuminance(accent) < 0.26 ? Lighten(accent, 0.36) : Lighten(accent, 0.12))
            : (RelativeLuminance(accent) > 0.78 ? Darken(accent, 0.30) : accent);

        if (IsDark)
        {
            palette.Paper = Mix(accent, Color.FromRgb(0, 0, 0), 0.82);
            palette.Text = Mix(accent, Color.FromRgb(255, 255, 255), 0.82);
            palette.PaperBorder = Mix(palette.Paper, palette.Text, 0.17);
            palette.WeakText = Mix(palette.Text, palette.Paper, 0.46);
            palette.Code = Mix(accent, Color.FromRgb(0, 0, 0), 0.74);
            palette.Tint = Lighten(active, 0.50);
        }
        else
        {
            palette.Paper = Mix(accent, Color.FromRgb(255, 255, 255), 0.90);
            palette.Text = Mix(accent, Color.FromRgb(0, 0, 0), 0.72);
            palette.PaperBorder = Mix(palette.Paper, palette.Text, 0.16);
            palette.WeakText = Mix(palette.Text, palette.Paper, 0.46);
            palette.Code = Mix(accent, Color.FromRgb(255, 255, 255), 0.82);
            palette.Tint = Darken(active, 0.10);
        }

        palette.Active = active;
        palette.Link = Mix(active, palette.Text, IsDark ? 0.10 : 0.12);
        palette.CheckBox = Mix(active, palette.PaperBorder, IsDark ? 0.30 : 0.34);
        palette.QuoteBorder = Mix(active, palette.PaperBorder, IsDark ? 0.34 : 0.40);
        return palette;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.03928
                ? normalized / 12.92
                : System.Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static Dictionary<string, (Palette Light, Palette Dark)> BuildSchemes()
    {
        return new Dictionary<string, (Palette, Palette)>
        {
            // Official default tokens: siyuan/app/appearance/themes/{daylight,midnight}/theme.css.
            [ColorSchemes.SiYuan] = (
                new Palette
                {
                    Paper = Color.FromRgb(255, 255, 255),
                    PaperBorder = Color.FromRgb(224, 224, 224),
                    Text = Color.FromRgb(34, 34, 34),
                    WeakText = Color.FromRgb(95, 99, 104),
                    Active = Color.FromRgb(53, 117, 240),
                    Code = Color.FromRgb(244, 244, 244),
                    QuoteBorder = Color.FromRgb(224, 224, 224),
                    Link = Color.FromRgb(66, 133, 244),
                    CheckBox = Color.FromRgb(95, 99, 104),
                    Tint = Color.FromRgb(53, 117, 240),
                    Danger = Color.FromRgb(210, 63, 49),
                },
                new Palette
                {
                    Paper = Color.FromRgb(30, 30, 30),
                    PaperBorder = Color.FromRgb(72, 72, 72),
                    Text = Color.FromRgb(218, 218, 218),
                    WeakText = Color.FromRgb(154, 160, 166),
                    Active = Color.FromRgb(53, 117, 240),
                    Code = Color.FromRgb(42, 42, 42),
                    QuoteBorder = Color.FromRgb(72, 72, 72),
                    Link = Color.FromRgb(138, 180, 248),
                    CheckBox = Color.FromRgb(154, 160, 166),
                    Tint = Color.FromRgb(53, 117, 240),
                    Danger = Color.FromRgb(242, 139, 130),
                }),
            // 暖纸 — 经典奶白纸张焕新：去掉旧版偏冷的紫蓝链接，换暖陶土；弱文字略降饱和。
            [ColorSchemes.Warm] = (
                new Palette
                {
                    Paper = Color.FromRgb(255, 249, 234),
                    PaperBorder = Color.FromRgb(224, 206, 167),
                    Text = Color.FromRgb(51, 41, 30),
                    WeakText = Color.FromRgb(138, 122, 99),
                    Active = Color.FromRgb(140, 115, 80),
                    Code = Color.FromRgb(247, 237, 210),
                    QuoteBorder = Color.FromRgb(212, 190, 146),
                    Link = Color.FromRgb(176, 98, 66),
                    CheckBox = Color.FromRgb(180, 160, 120),
                    Tint = Color.FromRgb(120, 92, 48),
                    Danger = Color.FromRgb(176, 90, 70),
                },
                new Palette
                {
                    Paper = Color.FromRgb(33, 31, 28),
                    PaperBorder = Color.FromRgb(76, 69, 61),
                    Text = Color.FromRgb(231, 224, 212),
                    WeakText = Color.FromRgb(146, 137, 123),
                    Active = Color.FromRgb(168, 142, 106),
                    Code = Color.FromRgb(45, 42, 38),
                    QuoteBorder = Color.FromRgb(94, 86, 75),
                    Link = Color.FromRgb(214, 150, 120),
                    CheckBox = Color.FromRgb(110, 100, 85),
                    Tint = Color.FromRgb(230, 223, 211),
                    Danger = Color.FromRgb(230, 110, 90),
                }),

            // 墨 — 冷调中性灰白，沉静专业，链接用经典墨蓝。
            [ColorSchemes.Ink] = (
                new Palette
                {
                    Paper = Color.FromRgb(246, 247, 249),
                    PaperBorder = Color.FromRgb(208, 214, 222),
                    Text = Color.FromRgb(38, 44, 54),
                    WeakText = Color.FromRgb(118, 126, 138),
                    Active = Color.FromRgb(90, 108, 134),
                    Code = Color.FromRgb(236, 239, 243),
                    QuoteBorder = Color.FromRgb(198, 206, 216),
                    Link = Color.FromRgb(66, 104, 156),
                    CheckBox = Color.FromRgb(170, 180, 194),
                    Tint = Color.FromRgb(70, 90, 120),
                    Danger = Color.FromRgb(188, 84, 80),
                },
                new Palette
                {
                    Paper = Color.FromRgb(26, 28, 32),
                    PaperBorder = Color.FromRgb(60, 66, 76),
                    Text = Color.FromRgb(222, 227, 234),
                    WeakText = Color.FromRgb(138, 146, 158),
                    Active = Color.FromRgb(132, 156, 188),
                    Code = Color.FromRgb(38, 41, 47),
                    QuoteBorder = Color.FromRgb(78, 86, 98),
                    Link = Color.FromRgb(132, 170, 214),
                    CheckBox = Color.FromRgb(96, 106, 120),
                    Tint = Color.FromRgb(180, 200, 228),
                    Danger = Color.FromRgb(224, 116, 108),
                }),

            // 林 — 柔和草木绿，纸面带极淡绿调，链接用森绿。
            [ColorSchemes.Forest] = (
                new Palette
                {
                    Paper = Color.FromRgb(243, 248, 241),
                    PaperBorder = Color.FromRgb(200, 218, 198),
                    Text = Color.FromRgb(38, 50, 42),
                    WeakText = Color.FromRgb(110, 128, 112),
                    Active = Color.FromRgb(88, 130, 96),
                    Code = Color.FromRgb(233, 242, 231),
                    QuoteBorder = Color.FromRgb(192, 214, 192),
                    Link = Color.FromRgb(60, 130, 96),
                    CheckBox = Color.FromRgb(168, 192, 168),
                    Tint = Color.FromRgb(70, 110, 80),
                    Danger = Color.FromRgb(188, 96, 76),
                },
                new Palette
                {
                    Paper = Color.FromRgb(26, 30, 27),
                    PaperBorder = Color.FromRgb(58, 70, 60),
                    Text = Color.FromRgb(220, 228, 220),
                    WeakText = Color.FromRgb(134, 148, 136),
                    Active = Color.FromRgb(124, 168, 134),
                    Code = Color.FromRgb(37, 42, 38),
                    QuoteBorder = Color.FromRgb(74, 90, 76),
                    Link = Color.FromRgb(128, 190, 150),
                    CheckBox = Color.FromRgb(92, 110, 94),
                    Tint = Color.FromRgb(180, 208, 186),
                    Danger = Color.FromRgb(222, 124, 104),
                }),

            // 霞 — 暖玫瑰胭脂，纸面带血色，链接用玫瑰红。
            [ColorSchemes.Rose] = (
                new Palette
                {
                    Paper = Color.FromRgb(253, 245, 246),
                    PaperBorder = Color.FromRgb(228, 205, 210),
                    Text = Color.FromRgb(54, 38, 42),
                    WeakText = Color.FromRgb(140, 114, 120),
                    Active = Color.FromRgb(158, 104, 118),
                    Code = Color.FromRgb(248, 236, 238),
                    QuoteBorder = Color.FromRgb(224, 198, 204),
                    Link = Color.FromRgb(178, 84, 110),
                    CheckBox = Color.FromRgb(216, 184, 192),
                    Tint = Color.FromRgb(150, 80, 96),
                    Danger = Color.FromRgb(188, 82, 78),
                },
                new Palette
                {
                    Paper = Color.FromRgb(33, 28, 30),
                    PaperBorder = Color.FromRgb(78, 64, 68),
                    Text = Color.FromRgb(232, 220, 223),
                    WeakText = Color.FromRgb(152, 132, 137),
                    Active = Color.FromRgb(190, 134, 148),
                    Code = Color.FromRgb(44, 38, 40),
                    QuoteBorder = Color.FromRgb(92, 76, 80),
                    Link = Color.FromRgb(224, 148, 170),
                    CheckBox = Color.FromRgb(96, 78, 82),
                    Tint = Color.FromRgb(224, 180, 190),
                    Danger = Color.FromRgb(230, 114, 100),
                }),
        };
    }
}
