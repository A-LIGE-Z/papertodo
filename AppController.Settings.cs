using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string AuthorName = "Designed by trigger";
    private const string AuthorGithubUrl = "https://github.com/snownico0722";
    private readonly Dictionary<string, Control> _settingsControls = new(StringComparer.Ordinal);
    private readonly Dictionary<int, double> _settingsScrollOffsets = new();
    private ScrollViewer? _settingsContentScrollViewer;
    private int _settingsContentTabIndex;

    private void RegisterSettingsControl(Control control, string key)
    {
        _settingsControls.Add(key, control);
        System.Windows.Automation.AutomationProperties.SetAutomationId(control, key);
    }

    private void SetTheme(string theme)
    {
        State.Theme = theme;
        Theme.Invalidate();
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();
        _outlineWindow?.RefreshThemeAndContent();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateThemeSegmentSelector()
    {
        var segments = new[]
        {
            ("system", Strings.Get("ThemeSystem")),
            ("light", Strings.Get("ThemeLight")),
            ("dark", Strings.Get("ThemeDark"))
        };

        return CreateSegmentSelector(segments, State.Theme, SetTheme);
    }

    private UIElement CreateWindowDisplayModeSelector()
    {
        var segments = new[]
        {
            (WindowDisplayModes.Cards, Strings.Get("DisplayModeCards")),
            (WindowDisplayModes.Outline, Strings.Get("DisplayModeOutline"))
        };

        return CreateSegmentSelector(segments, WindowDisplayModes.Normalize(State.WindowDisplayMode), SetWindowDisplayMode);
    }

    private void SetColorScheme(string scheme)
    {
        if (!ColorSchemes.IsValid(scheme))
        {
            return;
        }

        State.ColorScheme = scheme;
        State.CustomThemeColorHex = "";
        Theme.Invalidate();
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();
        _outlineWindow?.RefreshThemeAndContent();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateColorSchemeSegmentSelector()
    {
        var segments = new[]
        {
            (ColorSchemes.SiYuan, Strings.Get("ColorSchemeSiYuan")),
            (ColorSchemes.Warm, Strings.Get("ColorSchemeWarm")),
            (ColorSchemes.Ink, Strings.Get("ColorSchemeInk")),
            (ColorSchemes.Forest, Strings.Get("ColorSchemeForest")),
            (ColorSchemes.Rose, Strings.Get("ColorSchemeRose"))
        };

        var selected = string.IsNullOrWhiteSpace(State.CustomThemeColorHex)
            ? ColorSchemes.Normalize(State.ColorScheme)
            : "";
        return CreateSegmentSelector(segments, selected, SetColorScheme);
    }

    private void SetCustomThemeColor(string hex)
    {
        var normalized = Theme.NormalizeCustomThemeColorHex(hex);
        if (State.CustomThemeColorHex == normalized)
        {
            return;
        }

        State.CustomThemeColorHex = normalized;
        Theme.Invalidate();
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();
        _outlineWindow?.RefreshThemeAndContent();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateCustomThemeColorEditor()
    {
        var root = new Grid { Margin = new Thickness(0, 5, 0, 12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var swatch = new Button
        {
            Width = 58,
            Height = 42,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = TrayBorderBrush,
            Background = CustomThemeSwatchBrush(),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Strings.Get("SettingsCustomThemeColorPick")
        };
        swatch.Click += (_, _) => ShowCustomThemeColorDialog();
        Grid.SetColumn(swatch, 0);
        root.Children.Add(swatch);

        var detail = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var current = new TextBlock
        {
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Text = string.IsNullOrWhiteSpace(State.CustomThemeColorHex)
                ? Strings.Get("SettingsCustomThemeColorDefault")
                : State.CustomThemeColorHex
        };
        detail.Children.Add(current);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0)
        };

        var pick = new Button
        {
            Content = Strings.Get("SettingsCustomThemeColorPick"),
            MinWidth = 76,
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(0),
            Background = Theme.ActiveBrush,
            Foreground = Theme.OnActiveBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        pick.Click += (_, _) => ShowCustomThemeColorDialog();
        actions.Children.Add(pick);

        var clear = new Button
        {
            Content = Strings.Get("SettingsCustomThemeColorClear"),
            MinWidth = 82,
            Height = 27,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(0),
            Background = TrayHoverBrush,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        clear.Click += (_, _) => SetCustomThemeColor("");
        RegisterSettingsControl(swatch, "CustomThemeColorSwatch");
        RegisterSettingsControl(pick, "CustomThemeColorPick");
        RegisterSettingsControl(clear, "CustomThemeColorClear");
        actions.Children.Add(clear);

        detail.Children.Add(actions);

        Grid.SetColumn(detail, 1);
        root.Children.Add(detail);

        return root;
    }

    private void ShowCustomThemeColorDialog()
    {
        var current = Theme.TryParseHexColor(State.CustomThemeColorHex, out var color)
            ? color
            : BrushToColor(Theme.ActiveBrush);

        var owner = _settingsWindow is null ? IntPtr.Zero : new WindowInteropHelper(_settingsWindow).Handle;
        if (!TryChooseCustomThemeColor(owner, current, out var selected))
        {
            return;
        }

        SetCustomThemeColor($"#{selected.R:X2}{selected.G:X2}{selected.B:X2}");
    }

    private static bool TryChooseCustomThemeColor(IntPtr owner, Color current, out Color selected)
    {
        var customColors = new int[16];
        customColors[0] = ToColorRef(current);

        var handle = GCHandle.Alloc(customColors, GCHandleType.Pinned);
        try
        {
            var chooseColor = new ChooseColor
            {
                lStructSize = Marshal.SizeOf<ChooseColor>(),
                hwndOwner = owner,
                rgbResult = ToColorRef(current),
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = ChooseColorFlags.CC_ANYCOLOR | ChooseColorFlags.CC_FULLOPEN | ChooseColorFlags.CC_RGBINIT
            };

            if (!ChooseColorW(ref chooseColor))
            {
                selected = default;
                return false;
            }

            selected = FromColorRef(chooseColor.rgbResult);
            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    private static Color BrushToColor(Brush brush)
    {
        return brush is SolidColorBrush solid
            ? solid.Color
            : Color.FromRgb(53, 117, 240);
    }

    private static Brush CustomThemeSwatchBrush()
    {
        return Theme.TryParseHexColor(AppController.Current?.State?.CustomThemeColorHex, out var color)
            ? new SolidColorBrush(color)
            : Theme.ActiveBrush;
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static Color FromColorRef(int colorRef) => Color.FromRgb(
        (byte)(colorRef & 0xFF),
        (byte)((colorRef >> 8) & 0xFF),
        (byte)((colorRef >> 16) & 0xFF));

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChooseColorW")]
    private static extern bool ChooseColorW(ref ChooseColor lpcc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ChooseColor
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public ChooseColorFlags Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [Flags]
    private enum ChooseColorFlags
    {
        CC_RGBINIT = 0x00000001,
        CC_FULLOPEN = 0x00000002,
        CC_ANYCOLOR = 0x00000100
    }

    private void SetUiFontPreset(string preset)
    {
        var normalized = UiFontPresets.Normalize(preset);
        if (State.UiFontPreset == normalized && string.IsNullOrWhiteSpace(State.SystemFontFamilyName))
        {
            return;
        }

        State.UiFontPreset = normalized;
        State.SystemFontFamilyName = "";
        AppTypography.Configure(State.UiFontPreset, State.SystemFontFamilyName);
        SaveNow();
        RefreshTypography();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateUiFontPresetSegmentSelector()
    {
        var segments = new[]
        {
            (UiFontPresets.Default, Strings.Get("UiFontDefault")),
            (UiFontPresets.YaHei, Strings.Get("UiFontYaHei")),
            (UiFontPresets.DengXian, Strings.Get("UiFontDengXian"))
        };

        return CreateSegmentSelector(segments, UiFontPresets.Normalize(State.UiFontPreset), SetUiFontPreset);
    }

    private void SetSystemFontFamily(string? fontFamilyName)
    {
        var normalized = AppTypography.NormalizeSystemFontFamilyName(fontFamilyName);
        if (State.SystemFontFamilyName == normalized &&
            State.UiFontPreset == UiFontPresets.Default)
        {
            return;
        }

        State.SystemFontFamilyName = normalized;
        State.UiFontPreset = UiFontPresets.Default;
        AppTypography.Configure(State.UiFontPreset, State.SystemFontFamilyName);
        SaveNow();
        RefreshTypography();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateSystemFontSelector()
    {
        var combo = new ComboBox
        {
            Height = 30,
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(8, 3, 8, 3),
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12.5,
            IsTextSearchEnabled = true,
            MaxDropDownHeight = 320,
            Style = BuildSettingsComboBoxStyle(),
            ItemContainerStyle = BuildSettingsComboBoxItemStyle()
        };

        combo.Items.Add(new ComboBoxItem
        {
            Content = Strings.Get("UiFontDefault"),
            Tag = "",
            FontFamily = AppTypography.UiFontFamily
        });

        foreach (var name in AppTypography.SystemFontFamilyNames)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = name,
                Tag = name,
                FontFamily = new FontFamily(name)
            });
        }

        var selected = State.SystemFontFamilyName;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag as string ?? "", selected, StringComparison.CurrentCultureIgnoreCase))
            {
                combo.SelectedItem = item;
                break;
            }
        }

        combo.SelectedIndex = combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex;
        RegisterSettingsControl(combo, nameof(CreateSystemFontSelector));

        var initialized = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (!initialized || combo.SelectedItem is not ComboBoxItem item)
            {
                return;
            }

            SetSystemFontFamily(item.Tag as string);
        };
        initialized = true;

        return combo;
    }

    private void SetMarkdownRenderMode(string mode)
    {
        if (!MarkdownRenderModes.IsValid(mode))
        {
            return;
        }

        State.MarkdownRenderMode = mode;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateMarkdownRenderMode();
        }

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateMarkdownRenderSegmentSelector()
    {
        var segments = new[]
        {
            (MarkdownRenderModes.Off, Strings.Get("MarkdownRenderOff")),
            (MarkdownRenderModes.Basic, Strings.Get("MarkdownRenderBasic")),
            (MarkdownRenderModes.Enhanced, Strings.Get("MarkdownRenderEnhanced"))
        };

        return CreateSegmentSelector(segments, State.MarkdownRenderMode, SetMarkdownRenderMode);
    }

    private void SetFullscreenTopmostMode(string mode)
    {
        var normalized = FullscreenTopmostModes.Normalize(mode);
        if (State.FullscreenTopmostMode == normalized)
        {
            return;
        }

        State.FullscreenTopmostMode = normalized;
        RefreshTopmostForForegroundWindow();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateFullscreenTopmostModeSegmentSelector()
    {
        var segments = new[]
        {
            (FullscreenTopmostModes.Avoid, Strings.Get("FullscreenTopmostModeAvoid")),
            (FullscreenTopmostModes.StayOnTop, Strings.Get("FullscreenTopmostModeStayOnTop"))
        };

        return CreateSegmentSelector(segments, FullscreenTopmostModes.Normalize(State.FullscreenTopmostMode), SetFullscreenTopmostMode);
    }

    private void SetTodoVisualSize(string size)
    {
        var normalized = TodoVisualSizes.Normalize(size);
        if (State.TodoVisualSize == normalized)
        {
            return;
        }

        State.TodoVisualSize = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoVisualSize();
        }

        RefreshSettingsWindowContent();
    }

    private UIElement CreateTodoVisualSizeSegmentSelector()
    {
        var segments = new[]
        {
            (TodoVisualSizes.Small, Strings.Get("TodoVisualSizeSmall")),
            (TodoVisualSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
            (TodoVisualSizes.Large, Strings.Get("TodoVisualSizeLarge")),
            (TodoVisualSizes.ExtraLarge, Strings.Get("TodoVisualSizeExtraLarge"))
        };

        return CreateSegmentSelector(segments, TodoVisualSizes.Normalize(State.TodoVisualSize), SetTodoVisualSize);
    }

    private void SetTodoDueYearDisplayMode(string mode)
    {
        var normalized = TodoDueYearDisplayModes.Normalize(mode);
        if (State.TodoDueYearDisplayMode == normalized)
        {
            return;
        }

        State.TodoDueYearDisplayMode = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        RefreshSettingsWindowContent();
    }

    private UIElement CreateTodoDueYearDisplayModeSelector()
    {
        var segments = new[]
        {
            (TodoDueYearDisplayModes.None, Strings.Get("TodoDueYearDisplayNone")),
            (TodoDueYearDisplayModes.Short, Strings.Get("TodoDueYearDisplayShort")),
            (TodoDueYearDisplayModes.Full, Strings.Get("TodoDueYearDisplayFull"))
        };

        return CreateSegmentSelector(segments, TodoDueYearDisplayModes.Normalize(State.TodoDueYearDisplayMode), SetTodoDueYearDisplayMode);
    }

    private UIElement CreateLineSpacingEditor(string paperType)
    {
        var isNote = paperType == PaperTypes.Note;
        var textBox = new TextBox
        {
            Text = CurrentLineSpacing(isNote).ToString("0.##", CultureInfo.InvariantCulture),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };

        var root = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textBox, 0);
        root.Children.Add(textBox);
        RegisterSettingsControl(textBox, $"LineSpacing:{paperType}");

        void Commit()
        {
            var value = double.TryParse(textBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : CurrentLineSpacing(isNote);
            SetLineSpacing(isNote, value);
            textBox.Text = CurrentLineSpacing(isNote).ToString("0.##", CultureInfo.InvariantCulture);
            textBox.CaretIndex = textBox.Text.Length;
        }

        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.LostKeyboardFocus += (_, _) => Commit();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.Text = CurrentLineSpacing(isNote).ToString("0.##", CultureInfo.InvariantCulture);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };

        var reset = new Button
        {
            Content = Strings.Get("SettingsLineSpacingReset"),
            MinWidth = 58,
            Height = 26,
            Margin = new Thickness(8, 1, 0, 1),
            BorderThickness = new Thickness(0),
            Background = TrayHoverBrush,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        reset.Click += (_, _) =>
        {
            SetLineSpacing(isNote, 1.0);
            textBox.Text = "1";
        };
        RegisterSettingsControl(reset, $"LineSpacingReset:{paperType}");
        Grid.SetColumn(reset, 1);
        root.Children.Add(reset);

        return root;
    }

    private double CurrentLineSpacing(bool isNote)
        => isNote ? NormalizeLineSpacing(State.NoteLineSpacing) : NormalizeLineSpacing(State.TodoLineSpacing);

    private void SetLineSpacing(bool isNote, double value)
    {
        var normalized = NormalizeLineSpacing(value);
        if (isNote)
        {
            if (Math.Abs(State.NoteLineSpacing - normalized) < 0.001)
            {
                return;
            }
            State.NoteLineSpacing = normalized;
        }
        else
        {
            if (Math.Abs(State.TodoLineSpacing - normalized) < 0.001)
            {
                return;
            }
            State.TodoLineSpacing = normalized;
        }

        SaveNow();
        foreach (var window in _windows.Values)
        {
            if (isNote)
            {
                window.UpdateNoteLineSpacing();
            }
            else
            {
                window.UpdateTodoVisualSize();
            }
        }
    }

    private static double NormalizeLineSpacing(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(Math.Round(value, 2), 0.8, 5.0);
    }

    private void SetTodoReminderIntervalUnit(string unit)
    {
        var normalized = TodoReminderIntervalUnits.Normalize(unit);
        if (State.TodoReminderIntervalUnit == normalized)
        {
            return;
        }

        State.TodoReminderIntervalUnit = normalized;
        _lastTodoReminderShownAt.Clear();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateTodoReminderIntervalUnitSelector()
    {
        var segments = new[]
        {
            (TodoReminderIntervalUnits.Minutes, Strings.Get("TodoReminderIntervalUnitMinutes")),
            (TodoReminderIntervalUnits.Hours, Strings.Get("TodoReminderIntervalUnitHours"))
        };

        return CreateSegmentSelector(segments, TodoReminderIntervalUnits.Normalize(State.TodoReminderIntervalUnit), SetTodoReminderIntervalUnit);
    }

    private void SetTodoReminderScope(string scope)
    {
        var normalized = TodoReminderScopes.Normalize(scope);
        if (State.TodoReminderScope == normalized)
        {
            return;
        }

        State.TodoReminderScope = normalized;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateTodoReminderScopeSegmentSelector()
    {
        var segments = new[]
        {
            (TodoReminderScopes.Nearest, Strings.Get("TodoReminderScopeNearest")),
            (TodoReminderScopes.All, Strings.Get("TodoReminderScopeAll"))
        };

        return CreateSegmentSelector(segments, TodoReminderScopes.Normalize(State.TodoReminderScope), SetTodoReminderScope);
    }

    private UIElement CreateTodoReminderIntervalStepper()
    {
        var textBox = new TextBox
        {
            Text = State.TodoReminderIntervalValue.ToString(CultureInfo.InvariantCulture),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 10),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };
        RegisterSettingsControl(textBox, nameof(CreateTodoReminderIntervalStepper));

        void Commit()
        {
            var value = int.TryParse(textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : State.TodoReminderIntervalValue;
            SetTodoReminderIntervalValue(value);
            textBox.Text = State.TodoReminderIntervalValue.ToString(CultureInfo.InvariantCulture);
            textBox.CaretIndex = textBox.Text.Length;
        }

        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.LostKeyboardFocus += (_, _) => Commit();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.Text = State.TodoReminderIntervalValue.ToString(CultureInfo.InvariantCulture);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };

        return textBox;
    }

    private void SetTodoReminderIntervalValue(int value)
    {
        var normalized = Math.Clamp(value <= 0 ? 1 : value, 1, 240);
        if (State.TodoReminderIntervalValue == normalized)
        {
            return;
        }

        State.TodoReminderIntervalValue = normalized;
        _lastTodoReminderShownAt.Clear();
        SaveNow();
    }

    private UIElement CreateTodoReminderBubbleDurationEditor()
    {
        var textBox = new TextBox
        {
            Text = State.TodoReminderBubbleDurationSeconds.ToString(CultureInfo.InvariantCulture),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 10),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };
        RegisterSettingsControl(textBox, nameof(CreateTodoReminderBubbleDurationEditor));

        void Commit()
        {
            var value = int.TryParse(textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : State.TodoReminderBubbleDurationSeconds;
            SetTodoReminderBubbleDurationSeconds(value);
            textBox.Text = State.TodoReminderBubbleDurationSeconds.ToString(CultureInfo.InvariantCulture);
            textBox.CaretIndex = textBox.Text.Length;
        }

        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.LostKeyboardFocus += (_, _) => Commit();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.Text = State.TodoReminderBubbleDurationSeconds.ToString(CultureInfo.InvariantCulture);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };

        return textBox;
    }

    private void SetTodoReminderBubbleDurationSeconds(int value)
    {
        var normalized = Math.Clamp(value <= 0 ? 5 : value, 1, 600);
        if (State.TodoReminderBubbleDurationSeconds == normalized)
        {
            return;
        }

        State.TodoReminderBubbleDurationSeconds = normalized;
        SaveNow();
    }

    private UIElement CreateExternalMarkdownExtensionEditor()
    {
        var textBox = new TextBox
        {
            Text = ExternalMarkdownFileExtensions.Normalize(State.ExternalMarkdownExtension),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };

        _settingsExternalMarkdownTextBox = textBox;
        RegisterSettingsControl(textBox, nameof(CreateExternalMarkdownExtensionEditor));
        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitExternalMarkdownExtension(textBox);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.Text = ExternalMarkdownFileExtensions.Normalize(State.ExternalMarkdownExtension);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        textBox.LostKeyboardFocus += (_, _) => CommitExternalMarkdownExtension(textBox);

        return textBox;
    }

    private UIElement CreatePinnedPaperHotKeyEditor(string paperType)
    {
        var root = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = paperType == PaperTypes.Note ? State.PinnedNoteHotKey : State.PinnedTodoHotKey,
            IsReadOnly = true,
            Foreground = TrayTextBrush,
            CaretBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 13,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };
        RegisterSettingsControl(textBox, $"PinnedPaperHotKey:{paperType}");
        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            {
                return;
            }

            e.Handled = true;
            if (e.Key is Key.Escape or Key.Back or Key.Delete)
            {
                SetPinnedPaperHotKey(paperType, "");
                return;
            }

            var hotKey = HotKeyTextFromKeyEvent(e);
            if (string.IsNullOrWhiteSpace(hotKey))
            {
                return;
            }

            SetPinnedPaperHotKey(paperType, hotKey);
        };

        Grid.SetColumn(textBox, 0);
        root.Children.Add(textBox);

        var clear = new Button
        {
            Content = Strings.Get("SettingsHotKeyClear"),
            MinWidth = 52,
            Height = 26,
            Margin = new Thickness(8, 1, 0, 1),
            BorderThickness = new Thickness(0),
            Background = TrayHoverBrush,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        clear.Click += (_, _) => SetPinnedPaperHotKey(paperType, "");
        RegisterSettingsControl(clear, $"PinnedPaperHotKeyClear:{paperType}");
        Grid.SetColumn(clear, 1);
        root.Children.Add(clear);

        return root;
    }

    private static string HotKeyTextFromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System
            ? e.SystemKey
            : e.Key == Key.ImeProcessed
                ? e.ImeProcessedKey
                : e.Key;

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return "";
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            return "";
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? key.ToString());
        return string.Join("+", parts);
    }

    private void CommitSettingsExternalMarkdownEditor()
    {
        if (_settingsExternalMarkdownTextBox != null)
        {
            CommitExternalMarkdownExtension(_settingsExternalMarkdownTextBox);
        }
    }

    private void CommitExternalMarkdownExtension(TextBox textBox)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(textBox.Text);
        if (textBox.Text != normalized)
        {
            textBox.Text = normalized;
            textBox.CaretIndex = textBox.Text.Length;
        }

        SetExternalMarkdownExtension(normalized);
    }

    private void SetExternalMarkdownExtension(string extension)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(extension);
        if (State.ExternalMarkdownExtension == normalized)
        {
            return;
        }

        State.ExternalMarkdownExtension = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateExternalMarkdownExtension();
        }
    }

    private UIElement CreateSegmentSelector((string Key, string Label)[] segments, string activeKey, Action<string> onSelect)
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = AppUi.ControlRadius,
            Background = Theme.SurfaceBrush,
            Margin = new Thickness(0, 6, 0, 16),
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        for (var i = 0; i < segments.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < segments.Length; i++)
        {
            var key = segments[i].Key;
            var label = segments[i].Label;
            var isActive = activeKey == key;

            var segmentBorder = new Button
            {
                Margin = new Thickness(1),
                Padding = new Thickness(4, 0, 4, 0),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = isActive ? Theme.SelectionBrush : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = TrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            segmentBorder.Content = textBlock;
            RegisterSettingsControl(segmentBorder, $"{onSelect.Method.Name}:{key}");
            System.Windows.Automation.AutomationProperties.SetName(segmentBorder, label);
            segmentBorder.Click += (_, _) =>
            {
                if (activeKey == key)
                {
                    return;
                }

                onSelect(key);
            };

            Grid.SetColumn(segmentBorder, i);
            grid.Children.Add(segmentBorder);
        }

        container.Child = grid;
        return container;
    }

    private UIElement CreateMaxTitleLengthStepper()
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = AppUi.ControlRadius,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 10),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Text = State.MaxTitleLength.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Button StepButton(string glyph, string labelKey, int column, Action onClick)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = AppTypography.SymbolFontFamily,
                FontSize = 15,
                Foreground = TrayTextBrush
            };
            var button = new Button
            {
                Width = 34,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = glyphText,
                ToolTip = Strings.Get(labelKey)
            };
            RegisterSettingsControl(button, labelKey);
            System.Windows.Automation.AutomationProperties.SetName(button, Strings.Get(labelKey));
            button.Click += (_, e) =>
            {
                onClick();
                valueText.Text = State.MaxTitleLength.ToString(CultureInfo.InvariantCulture);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton("−", "SettingsDecreaseTitleLength", 0, () => SetMaxTitleLength(State.MaxTitleLength - 1)));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton("＋", "SettingsIncreaseTitleLength", 2, () => SetMaxTitleLength(State.MaxTitleLength + 1)));

        container.Child = grid;
        return container;
    }

    private void SetMaxTitleLength(int value)
    {
        var normalized = PaperTitles.NormalizeMaxTitleLength(value);
        if (State.MaxTitleLength == normalized)
        {
            return;
        }

        State.MaxTitleLength = normalized;

        // Re-clamp existing custom titles to the new limit and refresh everything that shows them.
        foreach (var paper in State.Papers)
        {
            paper.Title = PaperTitles.CleanCustomTitle(paper.Title, normalized);
        }

        foreach (var window in _windows.Values)
        {
            window.RefreshPaperTitle();
        }

        ArrangeDeepCapsules(animate: true);
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    public void ShowSettingsWindow()
    {
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }

        if (_settingsWindow != null)
        {
            RefreshSettingsWindowContent();
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        var window = new Window
        {
            Title = Strings.Get("TraySettings"),
            Width = SettingsWindowWidth(),
            Height = SettingsWindowDefaultHeight(),
            MinWidth = 640,
            MinHeight = 360,
            SizeToContent = SizeToContent.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        window.PreviewMouseDown += (_, e) =>
        {
            if (_settingsExternalMarkdownTextBox is not { IsKeyboardFocusWithin: true } textBox ||
                IsWithinElement(e.OriginalSource as DependencyObject, textBox))
            {
                return;
            }

            CommitExternalMarkdownExtension(textBox);
            Keyboard.ClearFocus();
        };
        window.Deactivated += (_, _) => CommitSettingsExternalMarkdownEditor();
        window.Closed += (_, _) =>
        {
            CommitSettingsExternalMarkdownEditor();
            _settingsExternalMarkdownTextBox = null;
            _settingsCapsuleModeCheckBox = null;
            _settingsDeepCapsuleModeCheckBox = null;
            _settingsDeepCapsuleExpandedSlotCheckBox = null;
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = null;
            _settingsCapsuleCollapseAllCheckBox = null;
            _settingsWindow = null;
            _settingsContentScrollViewer = null;
            _settingsControls.Clear();
            _settingsScrollOffsets.Clear();
        };
        _settingsWindow = window;
        RefreshSettingsWindowContent();
        window.Show();
        CenterSettingsWindow(window);
        window.Activate();
        window.Dispatcher.BeginInvoke(() => CenterSettingsWindow(window), DispatcherPriority.Loaded);
    }

    private void RefreshSettingsWindowContent()
    {
        if (_settingsWindow == null)
        {
            return;
        }

        var window = _settingsWindow;
        if (_settingsContentScrollViewer is { } previousScroll)
        {
            _settingsScrollOffsets[_settingsContentTabIndex] = previousScroll.VerticalOffset;
        }

        var focusedEntry = _settingsControls.FirstOrDefault(entry => entry.Value.IsKeyboardFocusWithin);
        var hadKeyboardFocus = window.IsKeyboardFocusWithin;
        var focusedTextBox = focusedEntry.Value as TextBox;
        var selectionStart = focusedTextBox?.SelectionStart ?? 0;
        var selectionLength = focusedTextBox?.SelectionLength ?? 0;
        if (focusedTextBox != null)
        {
            // Commit the old editor before constructing its replacement from State.
            Keyboard.ClearFocus();
        }

        _settingsControls.Clear();
        var content = BuildSettingsWindowContent(window);
        window.Content = content;
        window.FontFamily = AppTypography.UiFontFamily;
        window.Language = AppTypography.Language;
        ApplyToolTipSetting(window);

        var focusAfterRebuild = Keyboard.FocusedElement;
        var scroll = _settingsContentScrollViewer;
        var offset = _settingsScrollOffsets.GetValueOrDefault(_settingsSelectedTabIndex);
        window.Dispatcher.BeginInvoke(() =>
        {
            // A later refresh, close or user input supersedes this restoration.
            if (_settingsWindow != window || !ReferenceEquals(window.Content, content))
            {
                return;
            }

            if (hadKeyboardFocus && window.IsActive &&
                ReferenceEquals(Keyboard.FocusedElement, focusAfterRebuild) &&
                focusedEntry.Key != null && _settingsControls.TryGetValue(focusedEntry.Key, out var target) &&
                target.IsVisible && target.IsEnabled)
            {
                target.Focus();
                if (target is TextBox textBox)
                {
                    var start = Math.Min(selectionStart, textBox.Text.Length);
                    textBox.Select(start, Math.Min(selectionLength, textBox.Text.Length - start));
                }
            }

            scroll?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private void RefreshTypography()
    {
        RebuildTrayMenu();

        foreach (var window in _windows.Values)
        {
            window.UpdateTypography();
        }

        foreach (var masterCapsule in _masterCapsules.Values)
        {
            masterCapsule.UpdateTypography();
        }
    }

    private UIElement BuildSettingsWindowContent(Window window)
    {
        var root = new DockPanel
        {
            LastChildFill = true
        };
        root.Resources[typeof(Button)] = AppUi.ButtonStyle();
        root.Resources["PaperScrollThumbBrush"] = Theme.ScrollThumbBrush;
        root.Resources["PaperScrollThumbHoverBrush"] = Theme.WeakTextBrush;

        var titleRow = new Grid
        {
            Height = 48,
            Margin = new Thickness(16, 0, 12, 0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { window.DragMove(); } catch { }
            }
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = Strings.Get("TraySettings"),
            Foreground = TrayTextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(title);

        var versionInfo = typeof(AppController).Assembly.GetName().Version;
        var versionStr = versionInfo != null
            ? (versionInfo.Build > 0 ? $"v{versionInfo.Major}.{versionInfo.Minor}.{versionInfo.Build}" : $"v{versionInfo.Major}.{versionInfo.Minor}")
            : "v0.0.3";
        var versionBadge = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = versionStr,
                Foreground = TrayWeakTextBrush,
                FontSize = 11,
                FontWeight = FontWeights.Medium
            }
        };
        titleStack.Children.Add(versionBadge);

        Grid.SetColumn(titleStack, 0);
        titleRow.Children.Add(titleStack);

        var closeButton = new Button
        {
            Content = "×",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = 16,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Style = BuildSettingsCloseButtonStyle()
        };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 1);
        titleRow.Children.Add(closeButton);

        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        var displayPanel = SettingsTabPanel();
        var todoNotePanel = SettingsTabPanel();
        var capsulePanel = SettingsTabPanel();
        var generalPanel = SettingsTabPanel();

        displayPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsDisplay")));
        displayPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsWindowDisplayMode")), "TipWindowDisplayMode"),
            CreateWindowDisplayModeSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("TrayThemeMode")), "TipThemeMode"),
            CreateThemeSegmentSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsColorScheme")), "TipColorScheme"),
            CreateColorSchemeSegmentSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsCustomThemeColor")), "TipCustomThemeColor"),
            CreateCustomThemeColorEditor(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsSystemFont")), "TipSystemFont"),
            CreateSystemFontSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("TrayMarkdownRenderMode")), "TipMarkdownRender"),
            CreateMarkdownRenderSegmentSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsFullscreenTopmostMode")), "TipFullscreenTopmostMode"),
            CreateFullscreenTopmostModeSegmentSelector()
        ));

        displayPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoVisualSize")));
        displayPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoVisualSize")), "TipTodoVisualSize"),
            CreateTodoVisualSizeSegmentSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoLineSpacing")), "TipTodoLineSpacing"),
            CreateLineSpacingEditor(PaperTypes.Todo),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsNoteLineSpacing")), "TipNoteLineSpacing"),
            CreateLineSpacingEditor(PaperTypes.Note)
        ));

        displayPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTopBarButtons")));
        displayPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewTodoButton"), State.ShowTopBarNewTodoButton, ToggleTopBarNewTodoButton), "TipNewTodoButton"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewNoteButton"), State.ShowTopBarNewNoteButton, ToggleTopBarNewNoteButton), "TipNewNoteButton"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarExternalOpenButton"), State.ShowTopBarExternalOpenButton, ToggleTopBarExternalOpenButton), "TipExternalOpenButton")
        ));

        generalPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));
        generalPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup), "TipStartup"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsHidePapersFromWindowSwitcher"), State.HidePapersFromWindowSwitcher, ToggleHidePapersFromWindowSwitcher), "TipHidePapersFromWindowSwitcher"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableToolTips"), State.EnableToolTips, ToggleToolTips), "TipEnableToolTips"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableAnimations"), State.EnableAnimations, ToggleAnimations), "TipEnableAnimations")
        ));

        generalPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsPinnedTodoHotKey")));
        generalPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsPinnedTodoHotKey")), "TipPinnedTodoHotKey"),
            CreatePinnedPaperHotKeyEditor(PaperTypes.Todo),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsPinnedNoteHotKey"), topMargin: 6), "TipPinnedNoteHotKey"),
            CreatePinnedPaperHotKeyEditor(PaperTypes.Note)
        ));

        generalPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsExternalOpen")));
        generalPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsExternalMarkdownExtension")), "TipExternalExtension"),
            CreateExternalMarkdownExtensionEditor()
        ));

        generalPanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsScriptCapsule")));
        generalPanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsToggle(Strings.Get("SettingsPersistentPowerShellProcess"), State.UsePersistentPowerShellProcess, TogglePersistentPowerShellProcess), "TipPersistentPowerShellProcess"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsPreferPowerShell7"), State.PreferPowerShell7, TogglePreferPowerShell7), "TipPreferPowerShell7"),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsHideScriptRunWindow"), State.HideScriptRunWindow, ToggleHideScriptRunWindow), "TipHideScriptRunWindow")
        ));

        todoNotePanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoNote")));
        var showLinkedNoteNameToggle = SettingsToggle(Strings.Get("SettingsShowLinkedNoteName"), State.ShowLinkedNoteName, ToggleLinkedNoteNameDisplay);
        showLinkedNoteNameToggle.IsEnabled = State.EnableTodoNoteLinks;
        var allowLongLinkedNoteTitlesToggle = SettingsToggle(Strings.Get("SettingsAllowLongLinkedNoteTitles"), State.AllowLongLinkedNoteTitles, ToggleLongLinkedNoteTitles);
        allowLongLinkedNoteTitlesToggle.IsEnabled = State.EnableTodoNoteLinks && State.ShowLinkedNoteName;
        var hideLinkedNotesFromCapsulesToggle = SettingsToggle(Strings.Get("SettingsHideLinkedNotesFromCapsules"), State.HideLinkedNotesFromCapsules, ToggleHideLinkedNotesFromCapsules);
        hideLinkedNotesFromCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;
        var runLinkedScriptCapsulesToggle = SettingsToggle(Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"), State.RunLinkedScriptCapsulesOnClick, ToggleRunLinkedScriptCapsulesOnClick);
        runLinkedScriptCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;

        todoNotePanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableTodoNoteLinks"), State.EnableTodoNoteLinks, ToggleTodoNoteLinks), "TipEnableTodoNoteLinks"),
            WrapWithHint(showLinkedNoteNameToggle, "TipShowLinkedNoteName"),
            WrapWithHint(allowLongLinkedNoteTitlesToggle, "TipAllowLongLinkedNoteTitles"),
            WrapWithHint(hideLinkedNotesFromCapsulesToggle, "TipHideLinkedNotesFromCapsules"),
            WrapWithHint(runLinkedScriptCapsulesToggle, "TipRunLinkedScriptCapsulesOnClick")
        ));

        todoNotePanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoDueYearDisplay")));
        todoNotePanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTodoDueRelativeTime"), State.ShowTodoDueRelativeTime, ToggleTodoDueRelativeTime), "TipShowTodoDueRelativeTime"),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoDueYearDisplay"), topMargin: 4), "TipTodoDueYearDisplay"),
            CreateTodoDueYearDisplayModeSelector(),
            WrapWithHint(SettingsToggle(Strings.Get("SettingsUseTodoReminderInterval"), State.UseTodoReminderInterval, ToggleTodoReminderInterval), "TipUseTodoReminderInterval"),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoReminderInterval"), topMargin: 6), "TipTodoReminderInterval"),
            CreateTodoReminderIntervalStepper(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoReminderIntervalUnit"), topMargin: 4), "TipTodoReminderIntervalUnit"),
            CreateTodoReminderIntervalUnitSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoReminderScope"), topMargin: 4), "TipTodoReminderScope"),
            CreateTodoReminderScopeSegmentSelector(),
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoReminderBubbleDuration"), topMargin: 6), "TipTodoReminderBubbleDuration"),
            CreateTodoReminderBubbleDurationEditor()
        ));

        capsulePanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsCapsule")));
        _settingsCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayCapsuleMode"), State.UseCapsuleMode, ToggleCapsuleMode);
        _settingsDeepCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayDeepCapsuleMode"), State.UseDeepCapsuleMode, ToggleDeepCapsuleMode);
        _settingsDeepCapsuleExpandedSlotCheckBox = SettingsToggle(Strings.Get("SettingsShowDeepCapsuleWhileExpanded"), State.ShowDeepCapsuleWhileExpanded, ToggleDeepCapsuleExpandedSlot);
        _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = SettingsToggle(Strings.Get("SettingsCollapseExpandedDeepCapsuleOnClick"), State.CollapseExpandedDeepCapsuleOnClick, ToggleCollapseExpandedDeepCapsuleOnClick);
        _settingsHideDeepCapsulesWhenCoveredCheckBox = SettingsToggle(Strings.Get("SettingsHideDeepCapsulesWhenCovered"), State.HideDeepCapsulesWhenCovered, ToggleHideDeepCapsulesWhenCovered);
        _settingsCapsuleCollapseAllCheckBox = SettingsToggle(Strings.Get("SettingsCapsuleCollapseAll"), State.UseCapsuleCollapseAll, ToggleCapsuleCollapseAll);
        RefreshSettingsCapsuleToggleStates();

        capsulePanel.Children.Add(CreateSettingsCard(
            WrapWithHint(_settingsCapsuleModeCheckBox, "TipCapsuleMode"),
            WrapWithHint(_settingsDeepCapsuleModeCheckBox, "TipDeepCapsuleMode"),
            WrapWithHint(_settingsDeepCapsuleExpandedSlotCheckBox, "TipShowDeepCapsuleWhileExpanded"),
            WrapWithHint(_settingsCollapseExpandedDeepCapsuleOnClickCheckBox, "TipCollapseExpandedDeepCapsuleOnClick"),
            WrapWithHint(_settingsHideDeepCapsulesWhenCoveredCheckBox, "TipHideDeepCapsulesWhenCovered"),
            WrapWithHint(_settingsCapsuleCollapseAllCheckBox, "TipCapsuleCollapseAll")
        ));

        capsulePanel.Children.Add(SettingsSectionLabel(Strings.Get("SettingsMaxTitleLength")));
        capsulePanel.Children.Add(CreateSettingsCard(
            WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsMaxTitleLength")), "TipMaxTitleLength"),
            CreateMaxTitleLengthStepper()
        ));

        var syncPanel = SettingsTabPanel();
        syncPanel.Children.Add(SettingsSectionLabel(Strings.Get("SyncTitle")));
        syncPanel.Children.Add(CreateSettingsCard(
            SettingsToggle(Strings.Get("SyncEnabled"), State.SyncEnabled, ToggleSyncEnabled)
        ));

        if (State.SyncEnabled)
        {
            syncPanel.Children.Add(SettingsSectionLabel(Strings.Get("SyncProvider")));
            syncPanel.Children.Add(CreateSettingsCard(
                CreateSyncProviderSegmentSelector()
            ));

            if (State.SyncProvider == SyncProviders.WebDav)
            {
                syncPanel.Children.Add(SettingsSectionLabel("WebDAV 配置"));
                syncPanel.Children.Add(CreateSettingsCard(
                    SettingsFieldLabel(Strings.Get("SyncWebDavUrl")),
                    CreateSyncTextBox(State.SyncWebDavUrl, "https://dav.jianguoyun.com/dav/", val => State.SyncWebDavUrl = val),
                    SettingsFieldLabel(Strings.Get("SyncWebDavUsername"), topMargin: 6),
                    CreateSyncTextBox(State.SyncWebDavUsername, "username@example.com", val => State.SyncWebDavUsername = val),
                    SettingsFieldLabel(Strings.Get("SyncWebDavPassword"), topMargin: 6),
                    CreateSyncPasswordBox(State.SyncWebDavPassword, val => State.SyncWebDavPassword = val),
                    SettingsFieldLabel(Strings.Get("SyncWebDavRemotePath"), topMargin: 6),
                    CreateSyncTextBox(State.SyncWebDavRemotePath, "/papertodo/data.json", val => State.SyncWebDavRemotePath = val)
                ));
            }
            else if (State.SyncProvider == SyncProviders.S3)
            {
                syncPanel.Children.Add(SettingsSectionLabel("S3 对象存储配置"));
                syncPanel.Children.Add(CreateSettingsCard(
                    SettingsFieldLabel(Strings.Get("SyncS3Endpoint")),
                    CreateSyncTextBox(State.SyncS3Endpoint, "https://<account_id>.r2.cloudflarestorage.com", val => State.SyncS3Endpoint = val),
                    SettingsFieldLabel(Strings.Get("SyncS3Bucket"), topMargin: 6),
                    CreateSyncTextBox(State.SyncS3Bucket, "papertodo", val => State.SyncS3Bucket = val),
                    SettingsFieldLabel(Strings.Get("SyncS3AccessKey"), topMargin: 6),
                    CreateSyncTextBox(State.SyncS3AccessKey, "", val => State.SyncS3AccessKey = val),
                    SettingsFieldLabel(Strings.Get("SyncS3SecretKey"), topMargin: 6),
                    CreateSyncPasswordBox(State.SyncS3SecretKey, val => State.SyncS3SecretKey = val),
                    SettingsFieldLabel(Strings.Get("SyncS3Region"), topMargin: 6),
                    CreateSyncTextBox(State.SyncS3Region, "us-east-1", val => State.SyncS3Region = val)
                ));
            }
            else
            {
                syncPanel.Children.Add(SettingsSectionLabel("自建服务配置"));
                syncPanel.Children.Add(CreateSettingsCard(
                    SettingsFieldLabel(Strings.Get("SyncCustomServerUrl")),
                    CreateSyncTextBox(State.SyncCustomServerUrl, "http://127.0.0.1:8080", val => State.SyncCustomServerUrl = val),
                    SettingsFieldLabel(Strings.Get("SyncCustomToken"), topMargin: 6),
                    CreateSyncPasswordBox(State.SyncCustomToken, val => State.SyncCustomToken = val)
                ));
            }

            syncPanel.Children.Add(SettingsSectionLabel(Strings.Get("SyncStatus")));
            syncPanel.Children.Add(CreateSettingsCard(CreateSyncActionsAndStatusCard()));
        }

        var pages = new (string Icon, string Header, UIElement Content)[]
        {
            ("M2,3 H18 V14 H2 Z M7,18 H13 M10,14 V18", Strings.Get("SettingsTabDisplay"), displayPanel),
            ("M4,2 H12 L17,7 V18 H4 Z M12,2 V7 H17 M7,11 H14 M7,14 H12", Strings.Get("SettingsTabTodoNote"), todoNotePanel),
            ("M5,3 C8,0 12,0 15,3 C18,6 18,10 15,13 L13,15 C10,18 6,18 3,15 C0,12 0,8 3,5 Z M5,5 L13,13", Strings.Get("SettingsTabCapsule"), capsulePanel),
            ("M3,4 H17 M3,10 H17 M3,16 H17 M7,2 V6 M13,8 V12 M7,14 V18", Strings.Get("SettingsTabGeneral"), generalPanel),
            ("M3,8 A7,7 0 0 1 16,5 L18,7 M18,2 V7 H13 M17,12 A7,7 0 0 1 4,15 L2,13 M2,18 V13 H7", Strings.Get("SyncTitle"), syncPanel)
        };
        _settingsSelectedTabIndex = Math.Clamp(_settingsSelectedTabIndex, 0, pages.Length - 1);

        var navigationLayout = new Grid();
        navigationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        navigationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navigationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new StackPanel
        {
            Margin = new Thickness(8, 12, 8, 12)
        };
        for (var i = 0; i < pages.Length; i++)
        {
            nav.Children.Add(SettingsNavigationItem(pages[i].Icon, pages[i].Header, i, i == _settingsSelectedTabIndex));
        }
        var navHost = new Border { Background = Theme.SurfaceBrush, Child = nav };
        Grid.SetColumn(navHost, 0);
        navigationLayout.Children.Add(navHost);

        var separator = new Border
        {
            Width = 1,
            Background = TrayBorderBrush
        };
        Grid.SetColumn(separator, 1);
        navigationLayout.Children.Add(separator);

        var contentScroll = new ScrollViewer
        {
            Content = pages[_settingsSelectedTabIndex].Content,
            Margin = new Thickness(24, 16, 20, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        };
        _settingsContentScrollViewer = contentScroll;
        _settingsContentTabIndex = _settingsSelectedTabIndex;
        Grid.SetColumn(contentScroll, 2);
        navigationLayout.Children.Add(contentScroll);

        var footer = BuildSettingsFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(navigationLayout);

        return new Border
        {
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = AppUi.PanelRadius,
            Margin = new Thickness(12),
            Child = root,
            Effect = AppUi.SettingsShadow()
        };
    }

    private UIElement BuildSettingsFooter()
    {
        var signatureText = new TextBlock
        {
            Text = AuthorName,
            Foreground = TrayWeakTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };

        var signature = new Border
        {
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 16, 10),
            Padding = new Thickness(6, 2, 0, 0),
            Child = signatureText,
            ToolTip = AuthorGithubUrl
        };
        ToolTipService.SetInitialShowDelay(signature, 300);
        ToolTipService.SetShowDuration(signature, 12000);
        signature.MouseEnter += (_, _) => signatureText.Foreground = TrayTextBrush;
        signature.MouseLeave += (_, _) => signatureText.Foreground = TrayWeakTextBrush;
        signature.MouseLeftButtonUp += (_, e) =>
        {
            OpenAuthorGithub();
            e.Handled = true;
        };

        return signature;
    }

    private static StackPanel SettingsTabPanel()
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 0)
        };
    }

    private Button SettingsNavigationItem(string icon, string text, int index, bool isSelected)
    {
        var iconBlock = AppUi.LineIcon(icon, isSelected ? Theme.ActiveBrush : TrayWeakTextBrush);
        iconBlock.Margin = new Thickness(0, 0, 10, 0);

        var label = new TextBlock
        {
            Text = text,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 14,
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(iconBlock);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var item = new Button
        {
            Height = 36,
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isSelected ? Theme.SelectionBrush : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = content,
            ToolTip = text
        };
        System.Windows.Automation.AutomationProperties.SetName(item, text);
        RegisterSettingsControl(item, $"SettingsTab:{index}");
        item.Click += (_, e) =>
        {
            if (_settingsSelectedTabIndex != index)
            {
                _settingsSelectedTabIndex = index;
                RefreshSettingsWindowContent();
            }
            e.Handled = true;
        };

        return item;
    }

    private static Border CreateSettingsCard(params UIElement[] children)
    {
        var panel = new StackPanel();
        for (var i = 0; i < children.Length; i++)
        {
            panel.Children.Add(children[i]);
            if (i < children.Length - 1 && (children[i] is CheckBox ||
                children[i] is Grid row && row.Children.OfType<CheckBox>().Any()))
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = TrayBorderBrush,
                    Margin = new Thickness(0, 10, 0, 10)
                });
            }
        }

        return new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 8, 0, 16),
            Margin = new Thickness(0, 0, 0, 18),
            Child = panel
        };
    }

    private static void OpenAuthorGithub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AuthorGithubUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening an external browser should not affect settings interaction.
        }
    }

    private static double SettingsWindowWidth()
    {
        return SettingsContentWidth() + 32;
    }

    private static double SettingsWindowDefaultHeight()
    {
        return Math.Clamp(SystemParameters.WorkArea.Height * 0.72, 520, 720);
    }

    private static double SettingsContentWidth()
    {
        return Math.Clamp(SystemParameters.WorkArea.Width - 96, 680, 800);
    }

    private static double SettingsWindowMaxHeight()
    {
        return Math.Max(260, SystemParameters.WorkArea.Height - 48);
    }

    private static double SettingsOptionsMaxHeight()
    {
        const double verticalPadding = 26;
        const double titleRowHeight = 34;
        const double footerHeight = 24;
        return Math.Max(180, SettingsWindowMaxHeight() - verticalPadding - titleRowHeight - footerHeight);
    }

    private static TextBlock SettingsSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayTextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static TextBlock SettingsFieldLabel(string text, double topMargin = 0)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayTextBrush,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, topMargin, 0, 2)
        };
    }

    private CheckBox SettingsToggle(string text, bool isChecked, Action onToggle)
    {
        var checkBox = new CheckBox
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            IsChecked = isChecked,
            Foreground = TrayTextBrush,
            FontSize = 14,
            MinHeight = 28,
            Margin = new Thickness(0, 4, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = BuildSettingsCheckBoxStyle()
        };

        checkBox.Click += (_, _) => onToggle();
        RegisterSettingsControl(checkBox, onToggle.Method.Name);
        System.Windows.Automation.AutomationProperties.SetName(checkBox, text);
        return checkBox;
    }

    private UIElement WrapWithHint(FrameworkElement option, string tipKey)
    {
        if (option is CheckBox checkBox)
        {
            AttachHintToToggle(checkBox, tipKey);
            return checkBox;
        }

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        option.Margin = new Thickness(0);
        option.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(option, 0);
        grid.Children.Add(option);

        var hint = CreateHintBadge(tipKey);
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        return grid;
    }

    private UIElement CreateHintBadge(string tipKey)
    {
        var hintGlyph = new TextBlock
        {
            Text = "ⓘ",
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hint = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Help,
            Child = hintGlyph,
            ToolTip = BuildSettingsHintTooltip(Strings.Get(tipKey)),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipPreferences.SetAlwaysEnabled(hint, true);
        ToolTipService.SetInitialShowDelay(hint, 200);
        ToolTipService.SetShowDuration(hint, 20000);
        ToolTipService.SetBetweenShowDelay(hint, 0);
        hint.MouseEnter += (_, _) => hintGlyph.Foreground = TrayTextBrush;
        hint.MouseLeave += (_, _) => hintGlyph.Foreground = TrayWeakTextBrush;

        return hint;
    }

    private void AttachHintToToggle(CheckBox checkBox, string tipKey)
    {
        var originalContent = checkBox.Content;
        checkBox.Content = null;
        var panel = new Grid { VerticalAlignment = VerticalAlignment.Center };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (originalContent is UIElement uiElem)
        {
            panel.Children.Add(uiElem);
        }
        else if (originalContent is string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = TrayTextBrush,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var hint = CreateHintBadge(tipKey);
        Grid.SetColumn(hint, 1);
        panel.Children.Add(hint);
        checkBox.Content = panel;
    }

    private ToolTip BuildSettingsHintTooltip(string text)
    {
        return new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = TrayTextBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240
            },
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            HasDropShadow = true
        };
    }

    private void RefreshSettingsCapsuleToggleStates()
    {
        if (_settingsCapsuleModeCheckBox != null)
        {
            _settingsCapsuleModeCheckBox.IsChecked = State.UseCapsuleMode;
        }
        if (_settingsDeepCapsuleModeCheckBox != null)
        {
            _settingsDeepCapsuleModeCheckBox.IsChecked = State.UseDeepCapsuleMode;
            _settingsDeepCapsuleModeCheckBox.IsEnabled = State.UseCapsuleMode;
        }
        if (_settingsDeepCapsuleExpandedSlotCheckBox != null)
        {
            _settingsDeepCapsuleExpandedSlotCheckBox.IsChecked = State.ShowDeepCapsuleWhileExpanded;
            _settingsDeepCapsuleExpandedSlotCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
        if (_settingsCollapseExpandedDeepCapsuleOnClickCheckBox != null)
        {
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox.IsChecked = State.CollapseExpandedDeepCapsuleOnClick;
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode &&
                State.ShowDeepCapsuleWhileExpanded;
        }
        if (_settingsHideDeepCapsulesWhenCoveredCheckBox != null)
        {
            _settingsHideDeepCapsulesWhenCoveredCheckBox.IsChecked = State.HideDeepCapsulesWhenCovered;
            _settingsHideDeepCapsulesWhenCoveredCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
        if (_settingsCapsuleCollapseAllCheckBox != null)
        {
            _settingsCapsuleCollapseAllCheckBox.IsChecked = State.UseCapsuleCollapseAll;
            _settingsCapsuleCollapseAllCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
    }

    private Style BuildSettingsTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, TrayBorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, AppUi.ControlRadius);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        contentHost.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(contentHost);

        var template = new ControlTemplate(typeof(TextBox))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, TrayWeakTextBrush, "Bd"));

        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.ActiveBrush, "Bd"));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(focusTrigger);
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Style BuildSettingsComboBoxStyle()
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, TrayPaperBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, TrayBorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 8, 4)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var root = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, AppUi.ControlRadius);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var contentDock = new FrameworkElementFactory(typeof(DockPanel));
        contentDock.SetValue(DockPanel.LastChildFillProperty, true);
        contentDock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var selected = new FrameworkElementFactory(typeof(ContentPresenter));
        selected.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        selected.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        selected.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        selected.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        selected.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        selected.SetValue(UIElement.IsHitTestVisibleProperty, false);

        var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        arrow.Name = "Arrow";
        arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0 0 L 5 5 L 10 0 Z"));
        arrow.SetValue(System.Windows.Shapes.Path.FillProperty, TrayWeakTextBrush);
        arrow.SetValue(FrameworkElement.WidthProperty, 10.0);
        arrow.SetValue(FrameworkElement.HeightProperty, 5.0);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 1, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(DockPanel.DockProperty, Dock.Right);

        contentDock.AppendChild(arrow);
        contentDock.AppendChild(selected);
        border.AppendChild(contentDock);
        root.AppendChild(border);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "DropDownToggle");
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ComboBox.IsDropDownOpen))
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay
        });

        var toggleChrome = new FrameworkElementFactory(typeof(Border));
        toggleChrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var toggleTemplate = new ControlTemplate(typeof(ToggleButton))
        {
            VisualTree = toggleChrome
        };
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        root.AppendChild(toggle);

        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetBinding(Popup.PlacementTargetProperty, new Binding
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ComboBox.IsDropDownOpen))
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay
        });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, TrayPaperBrush);
        popupBorder.SetValue(Border.BorderBrushProperty, TrayBorderBrush);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.CornerRadiusProperty, AppUi.BlockRadius);
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        popupBorder.SetValue(Border.EffectProperty, AppUi.FloatingShadow());
        popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth")
        {
            RelativeSource = RelativeSource.TemplatedParent
        });

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(FrameworkElement.MaxHeightProperty, new TemplateBindingExtension(ComboBox.MaxDropDownHeightProperty));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroll.AppendChild(items);
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(ComboBox))
        {
            VisualTree = root
        };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, TrayWeakTextBrush, "Bd"));
        hoverTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.FillProperty, TrayTextBrush, "Arrow"));

        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.ActiveBrush, "Bd"));

        var openTrigger = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
        openTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.ActiveBrush, "Bd"));
        openTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.FillProperty, Theme.ActiveBrush, "Arrow"));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(focusTrigger);
        template.Triggers.Add(openTrigger);
        template.Triggers.Add(disabledTrigger);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Style BuildSettingsComboBoxItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, AppUi.ControlRadius);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(ComboBoxItem))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, TrayHoverBrush));

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Tint((byte)(Theme.IsDark ? 46 : 28))));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(selectedTrigger);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static bool IsWithinElement(DependencyObject? current, DependencyObject ancestor)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetElementParent(current);
        }

        return false;
    }

    private static DependencyObject? GetElementParent(DependencyObject current)
    {
        if (current is FrameworkElement fe && fe.Parent is DependencyObject parent)
        {
            return parent;
        }

        if (current is FrameworkContentElement fce && fce.Parent is DependencyObject contentParent)
        {
            return contentParent;
        }

        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Style BuildSettingsCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, true));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        // Layout: [Content (Star)] [Switch Track (Auto)]
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        root.AppendChild(col0);
        root.AppendChild(col1);

        // Content on the left
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(Grid.ColumnProperty, 0);
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        root.AppendChild(content);

        // Switch Track on the right: 38px width, 20px height, fully rounded pill
        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "SwitchTrack";
        track.SetValue(Grid.ColumnProperty, 1);
        track.SetValue(FrameworkElement.WidthProperty, 38.0);
        track.SetValue(FrameworkElement.HeightProperty, 20.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(10.0));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetValue(Border.BorderBrushProperty, TrayWeakTextBrush);
        track.SetValue(Border.BackgroundProperty, Theme.SurfaceBrush);
        track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));

        // Switch Knob: 14px circle
        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.Name = "SwitchThumb";
        thumb.SetValue(FrameworkElement.WidthProperty, 14.0);
        thumb.SetValue(FrameworkElement.HeightProperty, 14.0);
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(7.0));
        thumb.SetValue(Border.BackgroundProperty, TrayWeakTextBrush);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
        track.AppendChild(thumb);

        root.AppendChild(track);

        var focusRing = new FrameworkElementFactory(typeof(Border), "SwitchFocusRing");
        focusRing.SetValue(Grid.ColumnProperty, 1);
        focusRing.SetValue(FrameworkElement.WidthProperty, 44.0);
        focusRing.SetValue(FrameworkElement.HeightProperty, 26.0);
        focusRing.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, -3, 0));
        focusRing.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        focusRing.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        focusRing.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        focusRing.SetValue(Border.BorderBrushProperty, Theme.ActiveBrush);
        focusRing.SetValue(UIElement.IsHitTestVisibleProperty, false);
        focusRing.SetValue(UIElement.OpacityProperty, 0.0);
        root.AppendChild(focusRing);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = root
        };

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Theme.ActiveBrush, "SwitchTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "SwitchTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.White, "SwitchThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "SwitchThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 0), "SwitchThumb"));

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, TrayWeakTextBrush, "SwitchTrack"));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));

        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "SwitchFocusRing"));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(focusTrigger);
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Style BuildSettingsCloseButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, AppUi.ControlRadius);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, TrayHoverBrush));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));

        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Theme.ActiveBrush));
        pressedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Theme.OnActiveBrush));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(pressedTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static void CenterSettingsWindow(Window? window)
    {
        if (window == null)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 1 ? window.ActualHeight : 280;
        var minLeft = area.Left + 16;
        var minTop = area.Top + 16;
        var maxLeft = area.Right - width - 16;
        var maxTop = area.Bottom - height - 16;
        var centeredLeft = area.Left + (area.Width - width) / 2;
        var centeredTop = area.Top + (area.Height - height) / 2;

        window.Left = ClampWindowCoordinate(centeredLeft, minLeft, maxLeft);
        window.Top = ClampWindowCoordinate(centeredTop, minTop, maxTop);
    }

    private static double ClampWindowCoordinate(double value, double min, double max)
    {
        return max < min ? min : Math.Clamp(value, min, max);
    }


    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            if (State.Theme == "system")
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Theme.Invalidate();
                    foreach (var window in _windows.Values)
                    {
                        window.UpdateTheme();
                    }
                    foreach (var m in _masterCapsules.Values) m.UpdateTheme();
                    _outlineWindow?.RefreshThemeAndContent();
                    RebuildTrayMenu();
                    RefreshSettingsWindowContent();
                }));
            }
        }
    }

    private void ToggleStartup()
    {
        var enabled = SystemSettingsHelper.IsStartupEnabled();
        if (!SystemSettingsHelper.ToggleStartup(!enabled))
        {
            _trayIcon?.ShowBalloonTip(
                Strings.Get("StartupFailureTitle"),
                Strings.Get("StartupFailureMessage"),
                BalloonIcon.Warning);
        }
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void ToggleAnimations()
    {
        State.EnableAnimations = !State.EnableAnimations;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHidePapersFromWindowSwitcher()
    {
        State.HidePapersFromWindowSwitcher = !State.HidePapersFromWindowSwitcher;
        SaveNow();
        RefreshPaperWindowSwitcherVisibility();
        RefreshSettingsWindowContent();
    }

    private void RefreshPaperWindowSwitcherVisibility()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateWindowSwitcherVisibility();
        }
    }

    private void TogglePersistentPowerShellProcess()
    {
        State.UsePersistentPowerShellProcess = !State.UsePersistentPowerShellProcess;
        if (!State.UsePersistentPowerShellProcess)
        {
            PaperWindow.StopPersistentScriptProcesses();
        }
        else
        {
            PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void TogglePreferPowerShell7()
    {
        State.PreferPowerShell7 = !State.PreferPowerShell7;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHideScriptRunWindow()
    {
        State.HideScriptRunWindow = !State.HideScriptRunWindow;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleToolTips()
    {
        State.EnableToolTips = !State.EnableToolTips;
        SaveNow();
        RefreshToolTipSetting();
        RefreshSettingsWindowContent();
    }

    private void RefreshToolTipSetting()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateToolTipSetting();
        }

        foreach (var m in _masterCapsules.Values) m.UpdateToolTipSetting();

        if (_settingsWindow != null)
        {
            ApplyToolTipSetting(_settingsWindow);
        }
    }

    private void ApplyToolTipSetting(Window window)
    {
        ToolTipPreferences.Apply(window, State.EnableToolTips);
    }

    private void ToggleCapsuleMode()
    {
        State.UseCapsuleMode = !State.UseCapsuleMode;

        if (!State.UseCapsuleMode)
        {
            State.UseDeepCapsuleMode = false;
            State.UseCapsuleCollapseAll = false;
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
            foreach (var paper in State.Papers)
            {
                paper.IsCollapsed = false;
            }
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateCapsuleMode();
        }

        ArrangeDeepCapsules();
        RestoreMissingVisiblePaperSurfaces();
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleTopBarNewTodoButton()
    {
        State.ShowTopBarNewTodoButton = !State.ShowTopBarNewTodoButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleTopBarNewNoteButton()
    {
        State.ShowTopBarNewNoteButton = !State.ShowTopBarNewNoteButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleTopBarExternalOpenButton()
    {
        State.ShowTopBarExternalOpenButton = !State.ShowTopBarExternalOpenButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleLinkedNoteNameDisplay()
    {
        State.ShowLinkedNoteName = !State.ShowLinkedNoteName;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleTodoDueRelativeTime()
    {
        State.ShowTodoDueRelativeTime = !State.ShowTodoDueRelativeTime;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleTodoReminderInterval()
    {
        State.UseTodoReminderInterval = !State.UseTodoReminderInterval;
        _shownTodoReminderKeys.Clear();
        _lastTodoReminderShownAt.Clear();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleLongLinkedNoteTitles()
    {
        State.AllowLongLinkedNoteTitles = !State.AllowLongLinkedNoteTitles;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHideLinkedNotesFromCapsules()
    {
        State.HideLinkedNotesFromCapsules = !State.HideLinkedNotesFromCapsules;
        RefreshCapsuleEligibilityForLinkedNotes();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleRunLinkedScriptCapsulesOnClick()
    {
        State.RunLinkedScriptCapsulesOnClick = !State.RunLinkedScriptCapsulesOnClick;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleTodoNoteLinks()
    {
        State.EnableTodoNoteLinks = !State.EnableTodoNoteLinks;
        ClearNoteLinkDropTarget();

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoLinkFeature();
        }

        RefreshCapsuleEligibilityForLinkedNotes();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void RefreshTopBarNewPaperButtonsSetting()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateTopBarNewPaperButtons();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleDeepCapsuleMode()
    {
        State.UseDeepCapsuleMode = !State.UseDeepCapsuleMode;

        if (State.UseDeepCapsuleMode && !State.UseCapsuleMode)
        {
            State.UseCapsuleMode = true;
            foreach (var window in _windows.Values)
            {
                window.UpdateCapsuleMode();
            }
        }
        else if (!State.UseDeepCapsuleMode)
        {
            State.UseCapsuleCollapseAll = false;
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateDeepCapsuleMode();
        }

        ArrangeDeepCapsules();
        RestoreMissingVisiblePaperSurfaces();
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleDeepCapsuleExpandedSlot()
    {
        State.ShowDeepCapsuleWhileExpanded = !State.ShowDeepCapsuleWhileExpanded;

        foreach (var window in _windows.Values)
        {
            window.UpdateDeepCapsuleExpandedSlotMode();
        }

        ArrangeDeepCapsules(animate: State.EnableAnimations);
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleCollapseExpandedDeepCapsuleOnClick()
    {
        State.CollapseExpandedDeepCapsuleOnClick = !State.CollapseExpandedDeepCapsuleOnClick;
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleHideDeepCapsulesWhenCovered()
    {
        State.HideDeepCapsulesWhenCovered = !State.HideDeepCapsulesWhenCovered;
        ArrangeDeepCapsules(animate: State.EnableAnimations);
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void RestoreMissingVisiblePaperSurfaces()
    {
        foreach (var paper in State.Papers.ToList())
        {
            if (!paper.IsVisible ||
                !_windows.TryGetValue(paper.Id, out var window) ||
                window.HasVisibleSurface)
            {
                continue;
            }

            RestoreExistingPaperWindowSurface(paper, window);
        }
    }

    private void RestoreExistingPaperWindowSurface(PaperData paper, PaperWindow window)
    {
        RescuePaperIfOffScreen(paper, State.Papers.IndexOf(paper));
        window.CancelPendingVisibilityTransitions();
        window.DetachFromDeepCapsuleStack(animate: false);

        window.Left = paper.X;
        window.Top = paper.Y;
        if (paper.IsCollapsed && State.UseCapsuleMode)
        {
            window.Width = window.DesiredCapsuleWindowWidth;
            window.Height = PaperLayoutDefaults.CapsuleHeight;
        }
        else
        {
            window.Width = paper.Width;
            window.Height = paper.Height;
        }

        window.Opacity = 1.0;
        if (!window.IsVisible)
        {
            window.Show();
        }
        window.RefreshEffectiveTopmost();
    }

    private void ToggleSyncEnabled()
    {
        State.SyncEnabled = !State.SyncEnabled;
        SaveNow();
        SyncWorker?.OnSyncConfigChanged();
        RefreshSettingsWindowContent();
    }

    private void SetSyncProvider(string provider)
    {
        State.SyncProvider = SyncProviders.Normalize(provider);
        SaveNow();
        SyncWorker?.OnSyncConfigChanged();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateSyncProviderSegmentSelector()
    {
        var segments = new[]
        {
            (SyncProviders.WebDav, "WebDAV (坚果云/NAS)"),
            (SyncProviders.S3, "S3 (Cloudflare R2/AWS)"),
            (SyncProviders.Custom, "自建服务 (Standalone)")
        };
        return CreateSegmentSelector(segments, State.SyncProvider, SetSyncProvider);
    }

    private UIElement CreateSyncTextBox(string initialValue, string placeholder, Action<string> onCommit)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };

        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.TextChanged += (_, _) =>
        {
            onCommit(textBox.Text.Trim());
            SyncWorker?.OnSyncConfigChanged();
            SaveNow();
        };
        return textBox;
    }

    private UIElement CreateSyncPasswordBox(string initialValue, Action<string> onCommit)
    {
        var box = new PasswordBox
        {
            Password = initialValue,
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 13,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.PasswordChanged += (_, _) =>
        {
            onCommit(box.Password);
            SyncWorker?.OnSyncConfigChanged();
            SaveNow();
        };
        return box;
    }

    private UIElement CreateSyncActionsAndStatusCard()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var testBtn = new Button
        {
            Content = Strings.Get("SyncTestButton"),
            MinWidth = 90,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(0),
            Background = TrayHoverBrush,
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var syncNowBtn = new Button
        {
            Content = Strings.Get("SyncNowButton"),
            MinWidth = 90,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(0),
            Background = Theme.ActiveBrush,
            Foreground = Theme.OnActiveBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        buttonRow.Children.Add(testBtn);
        buttonRow.Children.Add(syncNowBtn);
        panel.Children.Add(buttonRow);

        var statusBlock = new TextBlock
        {
            Text = GetSyncStatusSummaryText(),
            Foreground = TrayWeakTextBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        panel.Children.Add(statusBlock);

        testBtn.Click += async (_, _) =>
        {
            testBtn.IsEnabled = false;
            statusBlock.Text = Strings.Get("SyncInProgress");
            try
            {
                var provider = SyncProviderFactory.Create(State);
                var res = await provider.TestConnectionAsync();
                statusBlock.Text = res.Message;
                statusBlock.Foreground = res.Success ? Brushes.LimeGreen : Brushes.OrangeRed;
            }
            catch (Exception ex)
            {
                statusBlock.Text = ex.Message;
                statusBlock.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };

        syncNowBtn.Click += async (_, _) =>
        {
            syncNowBtn.IsEnabled = false;
            statusBlock.Text = Strings.Get("SyncInProgress");
            try
            {
                if (SyncWorker != null)
                {
                    var res = await SyncWorker.PerformSyncAsync(isSilent: false);
                    statusBlock.Text = GetSyncStatusSummaryText();
                    statusBlock.Foreground = res.Success ? Brushes.LimeGreen : Brushes.OrangeRed;
                }
            }
            finally
            {
                syncNowBtn.IsEnabled = true;
            }
        };

        return panel;
    }

    private string GetSyncStatusSummaryText()
    {
        var lastTime = State.LastSyncTimeUtc > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(State.LastSyncTimeUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : Strings.Get("SyncNever");
        var status = string.IsNullOrWhiteSpace(State.LastSyncStatus) ? "-" : State.LastSyncStatus;
        return $"{Strings.Get("SyncLastTime")}: {lastTime} | {Strings.Get("SyncStatus")}: {status}";
    }
}
