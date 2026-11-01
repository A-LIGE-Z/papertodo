using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

/// <summary>
/// 思源笔记（SiYuan Note）风格全便签大纲主窗口：
/// 严格采用思源经典的三栏式现代界面（最左侧活动栏 Activity Bar + 树形大纲侧栏 Sidebar + 右侧文档工作区 Document Workspace），
/// 具备面包屑导航、大标题、块级排版、分栏分割线、时间节点提醒与已完成项自动沉底逻辑，色调高度统一自洽。
/// </summary>
public sealed partial class OutlineWindow : Window
{
    private readonly AppController _controller;
    private Border _activityBarHost = new();
    private StackPanel _treePanel = new();
    private ScrollViewer _treeScrollViewer = new();
    private Border _mainEditorContainer = new();
    private TextBox _searchBox = new();
    private readonly HashSet<string> _collapsedPaperIds = new(StringComparer.Ordinal);
    private TextBlock _sidebarHeaderTitle = new();
    private string? _selectedPaperId;
    private string? _pendingHeadingScrollTarget;
    private string _searchFilter = "";
    private bool _allExpanded = true;
    private bool _notePreviewMode;
    private readonly Dictionary<string, Grid> _noteHosts = new(StringComparer.Ordinal);
    private bool _updatingNoteEditor;
    private string _activeActivityTab = "outline"; // "outline" | "papers"

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    public OutlineWindow(AppController controller)
    {
        _controller = controller;

        Title = Strings.Get("OutlineWindowTitle");
        Width = 1080;
        Height = 720;
        MinWidth = 840;
        MinHeight = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = SiYuanTheme.EditorBg;
        Foreground = SiYuanTheme.TextPrimary;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0)
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
        StateChanged += (_, _) => UpdateMaxButtonSymbol();

        BuildLayout();
        RefreshContent();
    }

    public void RefreshThemeAndContent()
    {
        Background = SiYuanTheme.EditorBg;
        Foreground = SiYuanTheme.TextPrimary;
        BuildLayout();
        RefreshContent();
    }

    private void BuildLayout()
    {
        // Rebuild theme-bound controls together; reusing children here leaves them
        // parented to the previous layout and accumulates search event handlers.
        _activityBarHost = new Border();
        _treePanel = new StackPanel();
        _treeScrollViewer = new ScrollViewer();
        _mainEditorContainer = new Border();
        _searchBox = new TextBox();
        SiYuanCheckBoxStyle = BuildSiYuanCheckBoxStyle();
        Resources["PaperScrollThumbBrush"] = Theme.ScrollThumbBrush;
        Resources["PaperScrollThumbHoverBrush"] = Theme.WeakTextBrush;
        var root = new Border
        {
            Background = SiYuanTheme.EditorBg,
            BorderBrush = SiYuanTheme.WindowBorder,
            BorderThickness = new Thickness(1)
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); // Top Titlebar
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Main 3-Column Content

        // 1. Top Titlebar (Frameless window bar with tabs, mode switch, and standard window buttons)
        var titlebar = BuildTopTitlebar();
        Grid.SetRow(titlebar, 0);
        mainGrid.Children.Add(titlebar);

        // 2. Main 3-Column Body (Activity Bar + Sidebar Outline Tree + Splitter + Document Workspace)
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); // Col 0: Activity Bar (strictly 40px)
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270), MinWidth = 200 }); // Col 1: Outline Tree Sidebar
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) }); // Col 2: Hairline Splitter
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 }); // Col 3: Document Workspace

        // Col 0: Activity Bar
        _activityBarHost.Child = BuildActivityBar();
        Grid.SetColumn(_activityBarHost, 0);
        bodyGrid.Children.Add(_activityBarHost);

        // Col 1: Outline Tree Sidebar
        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 1);
        bodyGrid.Children.Add(sidebar);

        // Col 2: Hairline Splitter
        var splitter = new GridSplitter
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = SiYuanTheme.Border,
            Cursor = Cursors.SizeWE
        };
        Grid.SetColumn(splitter, 2);
        bodyGrid.Children.Add(splitter);

        // Col 3: Document Workspace
        _mainEditorContainer.Background = SiYuanTheme.EditorBg;
        Grid.SetColumn(_mainEditorContainer, 3);
        bodyGrid.Children.Add(_mainEditorContainer);

        Grid.SetRow(bodyGrid, 1);
        mainGrid.Children.Add(bodyGrid);

        root.Child = mainGrid;
        Content = root;
    }

    private UIElement BuildTopTitlebar()
    {
        var titlebar = new Grid
        {
            Background = SiYuanTheme.TitlebarBg,
            Height = 38,
            Cursor = Cursors.SizeAll
        };
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Brand / Tab
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Middle drag area
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Mode switcher
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Window buttons (- [] X)

        titlebar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximize();
                }
                else
                {
                    try { DragMove(); } catch { }
                }
            }
        };

        // Left Brand & Tab: SiYuan clean tab aesthetic
        var tabContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(tabContainer, true);

        // Micro brand badge: subtle, low-saturation pill
        var brandTag = new Border
        {
            Background = SiYuanTheme.TreeSelectedBg,
            BorderBrush = SiYuanTheme.TreeSelectedBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = AppUi.ControlRadius,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var brandStack = new StackPanel { Orientation = Orientation.Horizontal };
        var brandDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = SiYuanTheme.Accent,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        brandStack.Children.Add(brandDot);
        brandStack.Children.Add(new TextBlock
        {
            Text = "PaperTodo",
            Foreground = SiYuanTheme.Accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        brandTag.Child = brandStack;
        tabContainer.Children.Add(brandTag);

        // SiYuan standard active tab: flush with window content
        var activeTab = new Border
        {
            Background = SiYuanTheme.EditorBg,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
            BorderBrush = SiYuanTheme.Border,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(0, 3, 0, 0)
        };
        var tabStack = new StackPanel { Orientation = Orientation.Horizontal };
        var tabIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2.5,2.5 L 11.5,2.5 C 12,2.5 12.5,3 12.5,3.5 L 12.5,13.5 C 12.5,14 12,14.5 11.5,14.5 L 2.5,14.5 C 2,14.5 1.5,14 1.5,13.5 L 1.5,3.5 C 1.5,3 2,2.5 2.5,2.5 Z M 4.5,5.5 L 9.5,5.5 M 4.5,8.5 L 9.5,8.5 M 4.5,11.5 L 7.5,11.5"),
            Stroke = SiYuanTheme.Accent,
            StrokeThickness = 1.2,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        tabStack.Children.Add(tabIcon);
        tabStack.Children.Add(new TextBlock
        {
            Text = Strings.Get("OutlineAllPapers"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = SiYuanTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        activeTab.Child = tabStack;
        tabContainer.Children.Add(activeTab);

        Grid.SetColumn(tabContainer, 0);
        titlebar.Children.Add(tabContainer);

        // Center-Right: Mode Switcher (SiYuan micro segment capsule)
        var modeSwitcher = BuildModeSelector();
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(modeSwitcher, true);
        Grid.SetColumn(modeSwitcher, 2);
        titlebar.Children.Add(modeSwitcher);

        // Rightmost: Standard Windows Titlebar Buttons (- [] X)
        var winButtons = BuildStandardWindowButtons();
        Grid.SetColumn(winButtons, 3);
        titlebar.Children.Add(winButtons);

        return titlebar;
    }

    private TextBlock? _maxButtonGlyph;

    private void UpdateMaxButtonSymbol()
    {
        if (_maxButtonGlyph != null)
        {
            _maxButtonGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "□";
        }
    }

    private UIElement BuildModeSelector()
    {
        var container = new Border
        {
            BorderBrush = SiYuanTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = SiYuanTheme.InputBg,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            Padding = new Thickness(2)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cardsBtn = BuildModeButton(
            Geometry.Parse("M 2.5,2.5 L 13.5,2.5 L 13.5,13.5 L 2.5,13.5 Z M 5,5 L 11,5 M 5,8 L 11,8 M 5,11 L 8.5,11"),
            Strings.Get("DisplayModeCards"),
            isSelected: false,
            () => _controller.SetWindowDisplayMode(WindowDisplayModes.Cards));
        Grid.SetColumn(cardsBtn, 0);
        grid.Children.Add(cardsBtn);

        var outlineBtn = BuildModeButton(
            Geometry.Parse("M 2.5,4 L 13.5,4 M 4.5,8 L 13.5,8 M 6.5,12 L 13.5,12"),
            Strings.Get("DisplayModeOutline"),
            isSelected: true,
            () => { });
        Grid.SetColumn(outlineBtn, 1);
        grid.Children.Add(outlineBtn);

        container.Child = grid;
        return container;
    }

    private static Border BuildModeButton(Geometry iconGeometry, string text, bool isSelected, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = AppUi.ControlRadius,
            Padding = new Thickness(8, 2, 8, 2),
            Background = isSelected ? Theme.SelectionBrush : Brushes.Transparent,
            Cursor = Cursors.Hand
        };


        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = iconGeometry,
            Width = 11,
            Height = 11,
            Stroke = isSelected ? SiYuanTheme.Accent : SiYuanTheme.TextMuted,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(iconPath);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isSelected ? (SiYuanTheme.IsDark ? Brushes.White : SiYuanTheme.TextPrimary) : SiYuanTheme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(tb);
        border.Child = stack;

        if (!isSelected)
        {
            border.MouseEnter += (_, _) =>
            {
                border.Background = SiYuanTheme.HoverBg;
                tb.Foreground = SiYuanTheme.TextPrimary;
                iconPath.Stroke = SiYuanTheme.TextPrimary;
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = Brushes.Transparent;
                tb.Foreground = SiYuanTheme.TextMuted;
                iconPath.Stroke = SiYuanTheme.TextMuted;
            };
        }

        border.MouseLeftButtonDown += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return border;
    }

    private UIElement BuildStandardWindowButtons()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(stack, true);

        var minBtn = WinButton("─", () => WindowState = WindowState.Minimized);
        stack.Children.Add(minBtn);

        var maxBtn = WinButton(WindowState == WindowState.Maximized ? "❐" : "□", ToggleMaximize);
        _maxButtonGlyph = new TextBlock
        {
            Text = WindowState == WindowState.Maximized ? "❐" : "□",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        maxBtn.Content = _maxButtonGlyph;
        stack.Children.Add(maxBtn);

        var closeBtn = WinButton("✕", () => _controller.SetWindowDisplayMode(WindowDisplayModes.Cards), isClose: true);
        stack.Children.Add(closeBtn);

        return stack;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static Button WinButton(string symbol, Action onClick, bool isClose = false)
    {
        var btn = new Button
        {
            Content = symbol,
            Width = 44,
            Height = 40,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = SiYuanTheme.TextMuted,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, isClose ? new SolidColorBrush(Color.FromRgb(220, 53, 69)) : SiYuanTheme.HoverBg));
        hover.Setters.Add(new Setter(ForegroundProperty, isClose ? Brushes.White : SiYuanTheme.TextPrimary));
        template.Triggers.Add(hover);

        style.Setters.Add(new Setter(TemplateProperty, template));
        btn.Style = style;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void RefreshActivityBar()
    {
        _activityBarHost.Child = BuildActivityBar();
    }

    private UIElement BuildActivityBar()
    {
        var bar = new Border
        {
            Width = 40,
            Background = SiYuanTheme.ActivityBarBg,
            BorderBrush = SiYuanTheme.Border,
            BorderThickness = new Thickness(0, 1, 1, 0)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Top icons
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Bottom icons

        var topStack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var outlineBtn = ActivityIconButton(
            Geometry.Parse("M 3,4.5 L 4.5,4.5 M 7,4.5 L 15,4.5 M 5,9 L 6.5,9 M 9,9 L 15,9 M 7,13.5 L 8.5,13.5 M 11,13.5 L 15,13.5"),
            Strings.Get("OutlineTreeView"),
            _activeActivityTab == "outline",
            () =>
            {
                _activeActivityTab = "outline";
                RefreshActivityBar();
                RefreshTree();
            });
        topStack.Children.Add(outlineBtn);

        var papersBtn = ActivityIconButton(
            Geometry.Parse("M 3.5,2.5 L 12.5,2.5 C 13.5,2.5 14.5,3.5 14.5,4.5 L 14.5,15.5 C 14.5,16.5 13.5,16.5 12.5,16.5 L 3.5,16.5 C 2.5,16.5 2.5,15.5 2.5,14.5 L 2.5,4.5 C 2.5,3.5 3.5,2.5 3.5,2.5 Z M 5.5,2.5 L 5.5,16.5 M 8,6 L 12,6 M 8,9 L 12,9"),
            Strings.Get("OutlinePaperList"),
            _activeActivityTab == "papers",
            () =>
            {
                _activeActivityTab = "papers";
                RefreshActivityBar();
                RefreshTree();
            });
        topStack.Children.Add(papersBtn);

        Grid.SetRow(topStack, 0);
        grid.Children.Add(topStack);

        var bottomStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var settingsBtn = ActivityIconButton(
            Geometry.Parse("M 9,2.5 L 10.2,2.5 L 10.6,4.2 C 11.1,4.5 11.6,4.8 12,5.2 L 13.6,4.5 L 14.5,5.4 L 13.8,7 C 14.2,7.4 14.5,7.9 14.8,8.4 L 16.5,8.8 L 16.5,10 L 14.8,10.4 C 14.5,10.9 14.2,11.4 13.8,11.8 L 14.5,13.4 L 13.6,14.3 L 12,13.6 C 11.6,14 11.1,14.3 10.6,14.6 L 10.2,16.3 L 9,16.3 L 8.6,14.6 C 8.1,14.3 7.6,14 7.2,13.6 L 5.6,14.3 L 4.7,13.4 L 5.4,11.8 C 5,11.4 4.7,10.9 4.4,10.4 L 2.7,10 L 2.7,8.8 L 4.4,8.4 C 4.7,7.9 5,7.4 5.4,7 L 4.7,5.4 L 5.6,4.5 L 7.2,5.2 C 7.6,4.8 8.1,4.5 8.6,4.2 Z M 9,7.5 C 8.2,7.5 7.5,8.2 7.5,9 C 7.5,9.8 8.2,10.5 9,10.5 C 9.8,10.5 10.5,9.8 10.5,9 C 10.5,8.2 9.8,7.5 9,7.5 Z"),
            Strings.Get("OutlineSettings"),
            isSelected: false,
            () =>
            {
                _controller.ShowSettingsWindow();
            });
        bottomStack.Children.Add(settingsBtn);

        var desktopBtn = ActivityIconButton(
            Geometry.Parse("M 2.5,3 L 15.5,3 C 16.1,3 16.5,3.4 16.5,4 L 16.5,12 C 16.5,12.6 16.1,13 15.5,13 L 2.5,13 C 1.9,13 1.5,12.6 1.5,12 L 1.5,4 C 1.5,3.4 1.9,3 2.5,3 Z M 6,16 L 12,16 M 9,13 L 9,16"),
            Strings.Get("OutlineBackToDesktop"),
            isSelected: false,
            () =>
            {
                _controller.SetWindowDisplayMode(WindowDisplayModes.Cards);
            });
        bottomStack.Children.Add(desktopBtn);

        Grid.SetRow(bottomStack, 1);
        grid.Children.Add(bottomStack);

        bar.Child = grid;
        return bar;
    }

    private Style SiYuanCheckBoxStyle { get; set; } = null!;

    private static Style BuildSiYuanCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(CheckBox));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(WidthProperty, 14.0);
        grid.SetValue(HeightProperty, 14.0);
        grid.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "BoxBorder";
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.5));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        box.SetValue(Border.BorderBrushProperty, SiYuanTheme.InputBorder);
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var checkPath = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        checkPath.Name = "CheckMark";
        checkPath.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 2.2,6.5 L 5.2,9.8 L 11.2,3.2"));
        checkPath.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
        checkPath.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.6);
        checkPath.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        checkPath.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        checkPath.SetValue(System.Windows.Shapes.Path.StrokeLineJoinProperty, PenLineJoin.Round);
        checkPath.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        checkPath.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        checkPath.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        box.AppendChild(checkPath);
        grid.AppendChild(box);
        template.VisualTree = grid;

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, SiYuanTheme.Accent, "BoxBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, SiYuanTheme.Accent, "BoxBorder"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, SiYuanTheme.Accent, "BoxBorder"));
        template.Triggers.Add(hoverTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Border ActivityIconButton(Geometry geometry, string tooltip, bool isSelected, Action onClick)
    {
        var border = new Border
        {
            Width = 40,
            Height = 40,
            BorderThickness = isSelected ? new Thickness(3, 0, 0, 0) : new Thickness(0),
            BorderBrush = SiYuanTheme.Accent,
            Background = isSelected ? SiYuanTheme.TreeSelectedBg : Brushes.Transparent,
            ToolTip = tooltip,
            Cursor = Cursors.Hand
        };

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = 18,
            Height = 18,
            Stroke = isSelected ? SiYuanTheme.Accent : SiYuanTheme.TextMuted,
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        border.Child = path;

        if (!isSelected)
        {
            border.MouseEnter += (_, _) =>
            {
                border.Background = SiYuanTheme.HoverBg;
                path.Stroke = SiYuanTheme.TextPrimary;
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = Brushes.Transparent;
                path.Stroke = SiYuanTheme.TextMuted;
            };
        }

        border.MouseLeftButtonDown += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return border;
    }

    private UIElement BuildSidebar()
    {
        var sidebar = new Border
        {
            Background = SiYuanTheme.SidebarBg,
            BorderBrush = SiYuanTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };

        var dock = new DockPanel { LastChildFill = true };

        // Top Sidebar Controls (Search Box + Tool icons)
        var topContainer = new StackPanel { Margin = new Thickness(10, 10, 10, 6) };

        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _sidebarHeaderTitle = new TextBlock
        {
            Text = _activeActivityTab == "papers" ? Strings.Get("OutlinePaperDocuments") : Strings.Get("OutlineStructure"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = SiYuanTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_sidebarHeaderTitle, 0);
        headerRow.Children.Add(_sidebarHeaderTitle);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var addBtn = SidebarSmallIconButton(
            Geometry.Parse("M 2,8 L 14,8 M 8,2 L 8,14"),
            Strings.Get("OutlineNewTodoPaper"),
            () =>
            {
                var p = _controller.CreatePaper(PaperTypes.Todo, show: false);
                if (p != null) { _selectedPaperId = p.Id; RefreshContent(); }
            });
        actions.Children.Add(addBtn);

        var addNoteBtn = SidebarSmallIconButton(
            Geometry.Parse("M 3,13 L 6.5,12.5 L 13.5,5.5 C 14,5 14,4 13.5,3.5 L 12.5,2.5 C 12,2 11,2 10.5,2.5 L 3.5,9.5 Z M 9.5,3.5 L 12.5,6.5"),
            Strings.Get("OutlineNewNotePaper"),
            () =>
            {
                var p = _controller.CreatePaper(PaperTypes.Note, show: false);
                if (p != null) { _selectedPaperId = p.Id; RefreshContent(); }
            });
        actions.Children.Add(addNoteBtn);

        var foldBtn = SidebarSmallIconButton(
            Geometry.Parse("M 4,6 L 8,2 L 12,6 M 4,10 L 8,14 L 12,10"),
            Strings.Get("OutlineToggleAll"),
            () =>
            {
                _allExpanded = !_allExpanded;
                if (_allExpanded) _collapsedPaperIds.Clear();
                else foreach (var p in _controller.State.Papers) _collapsedPaperIds.Add(p.Id);
                RefreshTree();
            });
        actions.Children.Add(foldBtn);

        Grid.SetColumn(actions, 1);
        headerRow.Children.Add(actions);
        topContainer.Children.Add(headerRow);

        // SiYuan Search Box: zero border, subtle background, micro magnifying glass
        var searchHost = new Border
        {
            Background = SiYuanTheme.InputBg,
            BorderThickness = new Thickness(0),
            CornerRadius = AppUi.ControlRadius,
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0)
        };

        var searchGrid = new Grid();
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var searchIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 4.8,2 C 6.5,2 7.8,3.3 7.8,5 C 7.8,6.7 6.5,8 4.8,8 C 3.1,8 1.8,6.7 1.8,5 C 1.8,3.3 3.1,2 4.8,2 Z M 7,7.2 L 10.8,11"),
            Stroke = SiYuanTheme.TextMuted,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(searchIcon, 0);
        searchGrid.Children.Add(searchIcon);

        _searchBox.BorderThickness = new Thickness(0);
        _searchBox.Background = Brushes.Transparent;
        _searchBox.Foreground = SiYuanTheme.TextPrimary;
        _searchBox.CaretBrush = SiYuanTheme.TextPrimary;
        _searchBox.FontSize = 11.5;
        _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        _searchBox.ToolTip = Strings.Get("OutlineSearch");
        _searchBox.TextChanged += (_, _) =>
        {
            _searchFilter = _searchBox.Text.Trim();
            RefreshTree();
        };
        Grid.SetColumn(_searchBox, 1);
        searchGrid.Children.Add(_searchBox);

        searchHost.Child = searchGrid;
        topContainer.Children.Add(searchHost);

        DockPanel.SetDock(topContainer, Dock.Top);
        dock.Children.Add(topContainer);

        // Tree Content
        _treeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _treeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _treeScrollViewer.Content = _treePanel;
        _treePanel.Margin = new Thickness(8, 2, 8, 12);

        dock.Children.Add(_treeScrollViewer);
        sidebar.Child = dock;
        return sidebar;
    }

    private static Border SidebarSmallIconButton(Geometry geometry, string tooltip, Action onClick)
    {
        var border = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = AppUi.ControlRadius,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Margin = new Thickness(1, 0, 1, 0)
        };

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = 12,
            Height = 12,
            Stroke = SiYuanTheme.TextMuted,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        border.Child = path;

        border.MouseEnter += (_, _) =>
        {
            border.Background = SiYuanTheme.HoverBg;
            path.Stroke = SiYuanTheme.TextPrimary;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            path.Stroke = SiYuanTheme.TextMuted;
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return border;
    }

    public void RefreshContent()
    {
        var noteIds = _controller.State.Papers.Where(p => p.Type == PaperTypes.Note).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _noteHosts.Keys.Where(id => !noteIds.Contains(id)).ToList()) _noteHosts.Remove(id);
        RefreshTree();
        RefreshDetail();
    }

    public void RefreshAfterSync()
    {
        RefreshTree();
        if (!_mainEditorContainer.IsKeyboardFocusWithin)
        {
            RefreshDetail();
            return;
        }
        // Live paper/item identities stay valid while a user is editing.
        _mainEditorContainer.LostKeyboardFocus -= RefreshDetailAfterEditing;
        _mainEditorContainer.LostKeyboardFocus += RefreshDetailAfterEditing;
    }

    private void RefreshDetailAfterEditing(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainEditorContainer.IsKeyboardFocusWithin) return;
            _mainEditorContainer.LostKeyboardFocus -= RefreshDetailAfterEditing;
            RefreshDetail();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshTree()
    {
        _treePanel.Children.Clear();
        _sidebarHeaderTitle.Text = _activeActivityTab == "papers" ? Strings.Get("OutlinePaperDocuments") : Strings.Get("OutlineStructure");

        var papers = _controller.State.Papers.ToList();
        if (!string.IsNullOrEmpty(_searchFilter))
        {
            papers = papers.Where(p =>
                (_controller.PaperTitleText(p)).Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.Content ?? "").Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Items.Any(i => i.Text.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                                (i.TodoExtraColumns != null && i.TodoExtraColumns.Any(c => c.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))))
            ).ToList();
        }

        if (papers.Count == 0)
        {
            _treePanel.Children.Add(new TextBlock
            {
                Text = Strings.Get("OutlineNoResults"),
                Foreground = SiYuanTheme.TextMuted,
                FontSize = 12,
                Margin = new Thickness(12, 32, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        var todoPapers = papers.Where(p => p.Type == PaperTypes.Todo).ToList();
        var notePapers = papers.Where(p => p.Type == PaperTypes.Note).ToList();

        // 1. Todo Lists Section
        if (todoPapers.Count > 0)
        {
            var totalTasks = todoPapers.Sum(p => p.Items.Count);
            var completedTasks = todoPapers.Sum(p => p.Items.Count(i => i.Done));
            _treePanel.Children.Add(BuildSectionHeader(Strings.Get("OutlineTodoPapers"), $"{completedTasks}/{totalTasks}"));

            foreach (var paper in todoPapers)
            {
                _treePanel.Children.Add(BuildPaperNode(paper));
            }
        }

        // 2. Note Documents Section
        if (notePapers.Count > 0)
        {
            _treePanel.Children.Add(BuildSectionHeader(Strings.Get("OutlineNotes"), Strings.Format("OutlineNoteCount", notePapers.Count)));

            foreach (var paper in notePapers)
            {
                _treePanel.Children.Add(BuildPaperNode(paper));
            }
        }

        // Auto select first
        if (string.IsNullOrEmpty(_selectedPaperId) || papers.All(p => p.Id != _selectedPaperId))
        {
            _selectedPaperId = papers.FirstOrDefault()?.Id;
            RefreshDetail();
        }
    }

    private static UIElement BuildSectionHeader(string title, string countText)
    {
        var panel = new Grid { Margin = new Thickness(4, 10, 4, 4) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = SiYuanTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
        panel.Children.Add(text);

        var badge = new Border
        {
            Background = SiYuanTheme.BlockBg,
            BorderBrush = SiYuanTheme.BlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = countText,
                FontSize = 9.5,
                Foreground = SiYuanTheme.TextMuted
            }
        };
        Grid.SetColumn(badge, 1);
        panel.Children.Add(badge);

        return panel;
    }

    private UIElement BuildPaperNode(PaperData paper)
    {
        var isSelected = paper.Id == _selectedPaperId;
        var isCollapsed = _collapsedPaperIds.Contains(paper.Id);

        var nodeContainer = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };

        // Card row with 28px height, rounded corner and hover highlight
        var row = new Border
        {
            Height = 28,
            CornerRadius = AppUi.ControlRadius,
            Padding = new Thickness(6, 0, 6, 0),
            Background = isSelected ? SiYuanTheme.TreeSelectedBg : Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        if (!isSelected)
        {
            row.MouseEnter += (_, _) => row.Background = SiYuanTheme.HoverBg;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); // Arrow
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Icon
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Badge

        // Micro vector Chevron for fold/unfold
        var arrowBox = new Border
        {
            Width = 16,
            Height = 16,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var arrowPath = new System.Windows.Shapes.Path
        {
            Data = isCollapsed
                ? Geometry.Parse("M 5,3.5 L 9.5,8 L 5,12.5")
                : Geometry.Parse("M 3.5,5 L 8,9.5 L 12.5,5"),
            Stroke = SiYuanTheme.TextMuted,
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        arrowBox.Child = arrowPath;
        arrowBox.MouseEnter += (_, _) => arrowPath.Stroke = SiYuanTheme.TextPrimary;
        arrowBox.MouseLeave += (_, _) => arrowPath.Stroke = SiYuanTheme.TextMuted;
        arrowBox.MouseLeftButtonDown += (_, e) =>
        {
            if (_collapsedPaperIds.Contains(paper.Id)) _collapsedPaperIds.Remove(paper.Id);
            else _collapsedPaperIds.Add(paper.Id);
            RefreshTree();
            e.Handled = true;
        };
        Grid.SetColumn(arrowBox, 0);
        rowGrid.Children.Add(arrowBox);

        var icon = new System.Windows.Shapes.Path
        {
            Data = paper.Type == PaperTypes.Todo
                ? Geometry.Parse("M 2,4.5 C 2,3.5 2.5,3 3.5,3 L 11.5,3 C 12.5,3 13,3.5 13,4.5 L 13,11.5 C 13,12.5 12.5,13 11.5,13 L 3.5,13 C 2.5,13 2,12.5 2,11.5 Z M 4.5,7.5 L 6.5,9.5 L 10.5,5.5")
                : Geometry.Parse("M 3,2.5 L 9.5,2.5 L 13,6 L 13,13.5 C 13,14 12.5,14.5 12,14.5 L 3,14.5 C 2.5,14.5 2,14 2,13.5 L 2,3.5 C 2,3 2.5,2.5 3,2.5 Z M 9.5,2.5 L 9.5,6 L 13,6"),
            Stroke = isSelected ? SiYuanTheme.Accent : SiYuanTheme.TextMuted,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(icon, 1);
        rowGrid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = _controller.PaperTitleText(paper),
            FontSize = 12,
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isSelected ? (SiYuanTheme.IsDark ? Brushes.White : SiYuanTheme.Accent) : SiYuanTheme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 2);
        rowGrid.Children.Add(title);

        var badgeText = paper.Type == PaperTypes.Todo
            ? $"{paper.Items.Count(i => i.Done)}/{paper.Items.Count}"
            : Strings.Format("OutlineCharCount", (paper.Content ?? "").Count(c => !char.IsWhiteSpace(c)));

        var badge = new TextBlock
        {
            Text = badgeText,
            FontSize = 10,
            Foreground = SiYuanTheme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(badge, 3);
        rowGrid.Children.Add(badge);

        row.Child = rowGrid;
        row.MouseLeftButtonDown += (_, e) =>
        {
            _selectedPaperId = paper.Id;
            RefreshTree();
            RefreshDetail();
            e.Handled = true;
        };

        // SiYuan Right-click context menu on tree paper node
        var paperMenu = CreateSiYuanContextMenu();
        if (paper.Type == PaperTypes.Todo)
        {
            paperMenu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineAddItem"), () =>
            {
                var win = _controller.GetOrCreatePaperWindow(paper);
                var lastUncompleted = paper.Items.OrderBy(i => i.Order).LastOrDefault(i => !i.Done);
                win.AddItemAfter(lastUncompleted, "");
                _selectedPaperId = paper.Id;
                RefreshContent();
            }));
            paperMenu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineClearDone"), () =>
            {
                var win = _controller.GetOrCreatePaperWindow(paper);
                win.ClearDoneItems();
                RefreshContent();
            }));
        }
        paperMenu.Items.Add(CreateSiYuanMenuSeparator());
        paperMenu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeletePaper"), () =>
        {
            if (MessageBox.Show(Strings.Format("OutlineDeleteConfirm", _controller.PaperTitleText(paper)), Strings.Get("OutlineDeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _controller.DeletePaper(paper);
                _selectedPaperId = null;
                RefreshContent();
            }
        }));
        row.ContextMenu = paperMenu;

        nodeContainer.Children.Add(row);

        // Child Outline Items (Indented with continuous 16px Guideline)
        if (!isCollapsed && _activeActivityTab != "papers")
        {
            var childContainer = new Border
            {
                Margin = new Thickness(16, 0, 0, 0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = SiYuanTheme.Guideline,
                Padding = new Thickness(8, 0, 0, 0)
            };

            var childStack = new StackPanel();

            if (paper.Type == PaperTypes.Todo)
            {
                var ordered = paper.Items.OrderBy(i => i.Order).ToList();
                foreach (var item in ordered)
                {
                    childStack.Children.Add(BuildTodoTreeLeaf(paper, item));
                }
            }
            else
            {
                var headings = ExtractHeadings(paper.Content ?? "");
                if (headings.Count > 0)
                {
                    foreach (var h in headings)
                    {
                        childStack.Children.Add(BuildNoteHeadingTreeLeaf(paper, h));
                    }
                }
            }

            childContainer.Child = childStack;
            nodeContainer.Children.Add(childContainer);
        }

        return nodeContainer;
    }

    private UIElement BuildTodoTreeLeaf(PaperData paper, PaperItem item)
    {
        var row = new Grid { Height = 28, Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            Style = SiYuanCheckBoxStyle,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        check.Click += (_, _) =>
        {
            item.Done = check.IsChecked == true;
            _controller.MarkDirty();
            var win = _controller.GetOrCreatePaperWindow(paper);
            win.QueueAutoReorderCompletedItems(item.Id, isChecking: item.Done);
            RefreshContent();
        };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Text) ? Strings.Get("OutlineEmptyTodo") : item.Text,
            FontSize = 11,
            Foreground = item.Done ? SiYuanTheme.TextMuted : SiYuanTheme.TextPrimary,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        text.MouseLeftButtonDown += (_, e) =>
        {
            _selectedPaperId = paper.Id;
            RefreshTree();
            RefreshDetail();
            e.Handled = true;
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (PaperWindow.TryGetTodoDueAt(item, out var dueAt))
        {
            var now = DateTime.Now;
            var isPastDue = dueAt < now;
            var isSoon = !isPastDue && dueAt <= now.AddMinutes(AppController.TodoReminderLeadTimeMinutes);

            var dueBadge = new Border
            {
                Background = isPastDue ? SiYuanTheme.DangerBg : isSoon ? SiYuanTheme.CautionBg : SiYuanTheme.BlockBg,
                BorderBrush = isPastDue ? SiYuanTheme.Danger : isSoon ? SiYuanTheme.CautionText : SiYuanTheme.BlockBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0.5, 4, 0.5),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = Strings.Format("ToolTipTodoDue", dueAt.ToString("yyyy-MM-dd HH:mm")),
                Child = new TextBlock
                {
                    Text = (isPastDue ? "! " : (isSoon ? "⏰ " : "")) + (dueAt.Date == now.Date ? dueAt.ToString("HH:mm") : dueAt.ToString("M/d")),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isPastDue ? SiYuanTheme.Danger : isSoon ? SiYuanTheme.CautionText : SiYuanTheme.TextMuted
                }
            };

            dueBadge.MouseLeftButtonDown += (_, e) =>
            {
                var win = _controller.GetOrCreatePaperWindow(paper);
                win.ShowTodoDueDialog(item, owner: this, onSaved: RefreshContent);
                e.Handled = true;
            };

            Grid.SetColumn(dueBadge, 2);
            row.Children.Add(dueBadge);
        }

        var win = _controller.GetOrCreatePaperWindow(paper);
        AttachColumnContextMenu(row, paper, win, item, 0);

        return row;
    }

    private UIElement BuildNoteHeadingTreeLeaf(PaperData paper, (int Level, string Text) heading)
    {
        var indent = (heading.Level - 1) * 16;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(indent, 0, 0, 0),
            Height = 28,
            Cursor = Cursors.Hand
        };

        var tag = new Border
        {
            Background = SiYuanTheme.BlockBg,
            BorderBrush = SiYuanTheme.BlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(3, 1, 3, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"H{heading.Level}",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = SiYuanTheme.Accent
            }
        };
        panel.Children.Add(tag);

        var title = new TextBlock
        {
            Text = heading.Text,
            FontSize = 11.5,
            Foreground = SiYuanTheme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(title);

        panel.MouseLeftButtonDown += (_, e) =>
        {
            _selectedPaperId = paper.Id;
            _pendingHeadingScrollTarget = heading.Text;
            RefreshTree();
            RefreshDetail();
            e.Handled = true;
        };

        var leafMenu = CreateSiYuanContextMenu();
        leafMenu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineCopyTitle"), () =>
        {
            Clipboard.SetText(heading.Text);
        }));
        leafMenu.Items.Add(CreateSiYuanMenuSeparator());
        leafMenu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeletePaper"), () =>
        {
            if (MessageBox.Show(Strings.Format("OutlineDeleteConfirm", _controller.PaperTitleText(paper)), Strings.Get("OutlineDeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _controller.DeletePaper(paper);
                _selectedPaperId = null;
                RefreshContent();
            }
        }));
        panel.ContextMenu = leafMenu;

        return panel;
    }

    private static Border CreateBreadcrumbNode(string label, Action? onClick = null, bool isBold = false)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow
        };

        var tb = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = isBold ? SiYuanTheme.TextPrimary : SiYuanTheme.TextMuted,
            FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = tb;

        border.MouseEnter += (_, _) =>
        {
            border.Background = SiYuanTheme.HoverBg;
            tb.Foreground = SiYuanTheme.TextPrimary;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            tb.Foreground = isBold ? SiYuanTheme.TextPrimary : SiYuanTheme.TextMuted;
        };

        if (onClick != null)
        {
            border.MouseLeftButtonDown += (_, e) =>
            {
                onClick();
                e.Handled = true;
            };
        }

        return border;
    }

    private static TextBlock CreateBreadcrumbSeparator() => new()
    {
        Text = " / ",
        FontSize = 12,
        Foreground = SiYuanTheme.TextMuted,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void RefreshDetail()
    {
        var paper = _controller.State.Papers.FirstOrDefault(p => p.Id == _selectedPaperId);
        if (paper == null)
        {
            _mainEditorContainer.Child = new TextBlock
            {
                Text = Strings.Get("OutlineSelectPaper"),
                Foreground = SiYuanTheme.TextMuted,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return;
        }

        var workspace = new Grid { Margin = new Thickness(36, 12, 40, 20) };
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: Breadcrumb & Actions
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: Document H1 Title
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 2: Metadata info line & progress bar
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 3: Main Body Content

        // Row 0: Breadcrumbs & Action Toolbar (Compressed to 30px height, hover interaction)
        var topNav = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 8) };
        topNav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topNav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var breadcrumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        breadcrumbs.Children.Add(CreateBreadcrumbNode("PaperTodo", () =>
        {
            _activeActivityTab = "papers";
            RefreshActivityBar();
            RefreshTree();
        }));
        breadcrumbs.Children.Add(CreateBreadcrumbSeparator());
        breadcrumbs.Children.Add(CreateBreadcrumbNode(paper.Type == PaperTypes.Todo ? Strings.Get("OutlineTodoPaper") : Strings.Get("OutlineNotePaper")));
        breadcrumbs.Children.Add(CreateBreadcrumbSeparator());
        breadcrumbs.Children.Add(CreateBreadcrumbNode(_controller.PaperTitleText(paper), isBold: true));
        Grid.SetColumn(breadcrumbs, 0);
        topNav.Children.Add(breadcrumbs);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (paper.Type == PaperTypes.Todo)
        {
            var addBtn = ActionPillButton(
                Geometry.Parse("M 2,7 L 12,7 M 7,2 L 7,12"),
                Strings.Get("OutlineAddItem"),
                isPrimary: true,
                () =>
                {
                    var win = _controller.GetOrCreatePaperWindow(paper);
                    var lastUncompleted = paper.Items.OrderBy(i => i.Order).LastOrDefault(i => !i.Done);
                    win.AddItemAfter(lastUncompleted, "");
                    RefreshContent();
                });
            actions.Children.Add(addBtn);

            var clearBtn = ActionPillButton(
                Geometry.Parse("M 2.5,11.5 L 11.5,11.5 M 4,11.5 L 5.5,4.5 L 9.5,4.5 L 11,11.5 M 7.5,2 L 7.5,4.5"),
                Strings.Get("OutlineClearDoneShort"),
                isPrimary: false,
                () =>
                {
                    var win = _controller.GetOrCreatePaperWindow(paper);
                    win.ClearDoneItems();
                    RefreshContent();
                });
            actions.Children.Add(clearBtn);
        }
        else
        {
            var modeBtn = ActionPillButton(
                _notePreviewMode
                    ? Geometry.Parse("M 2.5,11.5 L 5,11 L 11.5,4.5 L 9.5,2.5 L 3,9 Z")
                    : Geometry.Parse("M 2,7 C 3.5,4 6,2.5 7,2.5 C 8,2.5 10.5,4 12,7 C 10.5,10 8,11.5 7,11.5 C 6,11.5 3.5,10 2,7 Z M 7,5.5 C 7.8,5.5 8.5,6.2 8.5,7 C 8.5,7.8 7.8,8.5 7,8.5 C 6.2,8.5 5.5,7.8 5.5,7 C 5.5,6.2 6.2,5.5 7,5.5 Z"),
                _notePreviewMode ? Strings.Get("OutlineEditMode") : Strings.Get("OutlinePreviewMode"),
                isPrimary: false,
                () =>
                {
                    _notePreviewMode = !_notePreviewMode;
                    RefreshDetail();
                });
            actions.Children.Add(modeBtn);
        }

        var deleteBtn = ActionPillButton(
            Geometry.Parse("M 3,4 L 11,4 M 4.5,4 L 4.5,12 C 4.5,12.5 5,13 5.5,13 L 8.5,13 C 9,13 9.5,12.5 9.5,12 L 9.5,4 M 5.5,4 L 5.5,2.5 L 8.5,2.5 L 8.5,4"),
            Strings.Get("OutlineDelete"),
            isPrimary: false,
            () =>
            {
                if (MessageBox.Show(Strings.Format("OutlineDeleteConfirm", _controller.PaperTitleText(paper)), Strings.Get("OutlineDeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    _controller.DeletePaper(paper);
                    _selectedPaperId = null;
                    RefreshContent();
                }
            });
        actions.Children.Add(deleteBtn);

        Grid.SetColumn(actions, 1);
        topNav.Children.Add(actions);

        Grid.SetRow(topNav, 0);
        workspace.Children.Add(topNav);

        // Row 1: Document 28px Immersive Large Title (H1) - Zero border, zero padding
        var titleBox = new TextBox
        {
            Text = _controller.PaperTitleText(paper),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = SiYuanTheme.TextPrimary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CaretBrush = SiYuanTheme.TextPrimary,
            Margin = new Thickness(0, 4, 0, 4)
        };
        titleBox.LostFocus += (_, _) =>
        {
            _controller.UpdatePaperTitle(paper, titleBox.Text);
            if (_controller.GetPaperWindow(paper.Id) is PaperWindow win)
            {
                win.RefreshPaperTitle();
            }
            RefreshTree();
        };
        Grid.SetRow(titleBox, 1);
        workspace.Children.Add(titleBox);

        // Row 2: Meta Info Line & 2px Slim Progress Bar
        if (paper.Type == PaperTypes.Todo)
        {
            var totalTasks = paper.Items.Count;
            var doneTasks = paper.Items.Count(i => i.Done);
            var pct = totalTasks == 0 ? 0 : (int)Math.Round((double)doneTasks / totalTasks * 100);
            var metaStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var metaText = new TextBlock
            {
                Text = Strings.Format("OutlineTodoProgress", totalTasks, doneTasks, pct),
                FontSize = 11.5,
                Foreground = SiYuanTheme.TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            metaStack.Children.Add(metaText);

            if (totalTasks > 0)
            {
                var progressBar = new ProgressBar
                {
                    Height = 2.0,
                    Minimum = 0,
                    Maximum = Math.Max(1, totalTasks),
                    Value = doneTasks,
                    Foreground = SiYuanTheme.Accent,
                    Background = SiYuanTheme.Guideline,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                metaStack.Children.Add(progressBar);
            }

            Grid.SetRow(metaStack, 2);
            workspace.Children.Add(metaStack);
        }
        else
        {
            var noteMetaLine = new TextBlock
            {
                Text = Strings.Format("OutlineNoteMetrics", (paper.Content ?? "").Count(c => !char.IsWhiteSpace(c)), (paper.Content ?? "").Split('\n').Length),
                FontSize = 11.5,
                Foreground = SiYuanTheme.TextMuted,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(noteMetaLine, 2);
            workspace.Children.Add(noteMetaLine);
        }

        // Row 3: Content
        var content = paper.Type == PaperTypes.Todo ? BuildDetailTodoContent(paper) : BuildDetailNoteContent(paper);
        Grid.SetRow(content, 3);
        workspace.Children.Add(content);

        _mainEditorContainer.Child = workspace;
    }

    private static Border ActionPillButton(Geometry iconGeometry, string text, bool isPrimary, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 3.5, 9, 3.5),
            Margin = new Thickness(5, 0, 0, 0),
            Background = isPrimary ? SiYuanTheme.Accent : SiYuanTheme.BlockBg,
            BorderBrush = isPrimary ? SiYuanTheme.Accent : SiYuanTheme.BlockBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var path = new System.Windows.Shapes.Path
        {
            Data = iconGeometry,
            Width = 11,
            Height = 11,
            Stroke = isPrimary ? Brushes.White : SiYuanTheme.TextMuted,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(path);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isPrimary ? Brushes.White : SiYuanTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(tb);
        border.Child = stack;

        if (isPrimary)
        {
            border.MouseEnter += (_, _) => border.Opacity = 0.88;
            border.MouseLeave += (_, _) => border.Opacity = 1.0;
        }
        else
        {
            border.MouseEnter += (_, _) =>
            {
                border.Background = SiYuanTheme.HoverBg;
                path.Stroke = SiYuanTheme.TextPrimary;
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = SiYuanTheme.BlockBg;
                path.Stroke = SiYuanTheme.TextMuted;
            };
        }

        border.MouseLeftButtonDown += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
        return border;
    }

    private UIElement BuildDetailTodoContent(PaperData paper)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };

        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 24) };
        var win = _controller.GetOrCreatePaperWindow(paper);
        var ordered = paper.Items.OrderBy(i => i.Order).ToList();

        var activeItems = ordered.Where(i => !i.Done).ToList();
        var doneItems = ordered.Where(i => i.Done).ToList();

        foreach (var item in activeItems)
        {
            panel.Children.Add(BuildDetailTodoRow(paper, win, item));
        }

        // Add new item row
        var addRow = new Border
        {
            Margin = new Thickness(0, 6, 0, 4),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = AppUi.ControlRadius,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        addRow.Child = new TextBlock
        {
            Text = Strings.Get("OutlineAddPlaceholder"),
            FontSize = 12,
            Foreground = SiYuanTheme.TextMuted
        };
        addRow.MouseEnter += (_, _) => addRow.Background = SiYuanTheme.HoverBg;
        addRow.MouseLeave += (_, _) => addRow.Background = Brushes.Transparent;
        addRow.MouseLeftButtonDown += (_, e) =>
        {
            var lastUncompleted = paper.Items.OrderBy(i => i.Order).LastOrDefault(i => !i.Done);
            win.AddItemAfter(lastUncompleted, "");
            RefreshContent();
            e.Handled = true;
        };
        panel.Children.Add(addRow);

        // Completed Section
        if (doneItems.Count > 0)
        {
            var doneDivider = new Grid { Margin = new Thickness(0, 16, 0, 8) };
            doneDivider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            doneDivider.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var doneLabel = new TextBlock
            {
                Text = Strings.Format("OutlineCompletedGroup", doneItems.Count),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = SiYuanTheme.TextMuted,
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(doneLabel, 0);
            doneDivider.Children.Add(doneLabel);

            var doneLine = new Border
            {
                Height = 1,
                Background = SiYuanTheme.Guideline,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(doneLine, 1);
            doneDivider.Children.Add(doneLine);

            panel.Children.Add(doneDivider);

            foreach (var item in doneItems)
            {
                panel.Children.Add(BuildDetailTodoRow(paper, win, item));
            }
        }

        scroll.Content = panel;
        return scroll;
    }

    private UIElement BuildDetailTodoRow(PaperData paper, PaperWindow win, PaperItem item)
    {
        PaperWindow.NormalizeTodoItemColumns(item);

        // Native SiYuan continuous block row: zero card frame, natural block text flow
        var itemRow = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(2, 3, 4, 3),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinHeight = 28
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Gutter handle ⠿
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // Checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Columns
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Due badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Delete button

        // Gutter Handle (Hidden until hover)
        var handle = new TextBlock
        {
            Text = "⠿",
            FontSize = 12,
            Foreground = SiYuanTheme.TextMuted,
            Opacity = 0.0,
            Cursor = Cursors.SizeAll,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        handle.MouseEnter += (_, _) => handle.Opacity = 1.0;
        handle.MouseLeave += (_, _) => handle.Opacity = 0.65;
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        // Checkbox
        var check = new CheckBox
        {
            IsChecked = item.Done,
            Style = SiYuanCheckBoxStyle,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        check.Click += (_, _) =>
        {
            item.Done = check.IsChecked == true;
            _controller.MarkDirty();
            win.QueueAutoReorderCompletedItems(item.Id, isChecking: item.Done);
            RefreshContent();
        };
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        // Multi-column Text Host
        var columnHost = BuildDetailTodoColumnsHost(paper, win, item);
        Grid.SetColumn(columnHost, 2);
        grid.Children.Add(columnHost);

        // Time Node Badge
        var dueBadge = BuildDetailTodoDueBadge(paper, win, item);
        Grid.SetColumn(dueBadge, 3);
        grid.Children.Add(dueBadge);

        // Delete item button (Hidden until hover)
        var delBtn = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Opacity = 0.0,
            ToolTip = Strings.Get("OutlineDeleteItem"),
            Child = new TextBlock
            {
                Text = "×",
                FontSize = 13,
                Foreground = SiYuanTheme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        delBtn.MouseEnter += (_, _) =>
        {
            delBtn.Opacity = 1.0;
            delBtn.Background = SiYuanTheme.DangerBg;
        };
        delBtn.MouseLeave += (_, _) =>
        {
            delBtn.Opacity = 0.65;
            delBtn.Background = Brushes.Transparent;
        };
        delBtn.MouseLeftButtonDown += (_, e) =>
        {
            win.RemoveItem(item);
            RefreshContent();
            e.Handled = true;
        };
        Grid.SetColumn(delBtn, 4);
        grid.Children.Add(delBtn);

        itemRow.MouseEnter += (_, _) =>
        {
            itemRow.Background = SiYuanTheme.HoverBg;
            handle.Opacity = 0.65;
            delBtn.Opacity = 0.65;
        };
        itemRow.MouseLeave += (_, _) =>
        {
            itemRow.Background = Brushes.Transparent;
            handle.Opacity = 0.0;
            delBtn.Opacity = 0.0;
        };

        itemRow.Child = grid;
        AttachColumnContextMenu(itemRow, paper, win, item, 0);
        return itemRow;
    }

    private UIElement BuildDetailTodoColumnsHost(PaperData paper, PaperWindow win, PaperItem item)
    {
        var colCount = PaperWindow.TodoItemColumnCount(item);
        if (colCount <= 1)
        {
            var box = CreateTodoColumnTextBox(paper, win, item, 0, item.Text);
            AttachColumnContextMenu(box, paper, win, item, 0);
            return box;
        }

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Stretch };

        for (var c = 0; c < colCount; c++)
        {
            var widthWeight = c < item.TodoColumnWidths.Count ? item.TodoColumnWidths[c] : 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widthWeight, GridUnitType.Star) });
            if (c < colCount - 1)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            }
        }

        // Column 0
        var box0 = CreateTodoColumnTextBox(paper, win, item, 0, item.Text);
        AttachColumnContextMenu(box0, paper, win, item, 0);
        Grid.SetColumn(box0, 0);
        grid.Children.Add(box0);

        for (var c = 1; c < colCount; c++)
        {
            var splitterIndex = (c * 2) - 1;
            var colIndex = c * 2;
            var extraIndex = c - 1;

            // 1px dashed hairline splitter with 7px drag target
            var splitter = new GridSplitter
            {
                Width = 7,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeWE,
                Focusable = false
            };
            var splitterTemplate = new ControlTemplate(typeof(GridSplitter));
            var lineFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Line));
            lineFactory.SetValue(System.Windows.Shapes.Line.X1Property, 0.0);
            lineFactory.SetValue(System.Windows.Shapes.Line.Y1Property, 0.0);
            lineFactory.SetValue(System.Windows.Shapes.Line.X2Property, 0.0);
            lineFactory.SetValue(System.Windows.Shapes.Line.Y2Property, 1000.0);
            lineFactory.SetValue(System.Windows.Shapes.Line.StrokeProperty, SiYuanTheme.Guideline);
            lineFactory.SetValue(System.Windows.Shapes.Line.StrokeThicknessProperty, 1.0);
            lineFactory.SetValue(System.Windows.Shapes.Line.StrokeDashArrayProperty, new DoubleCollection { 2, 2 });
            lineFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            lineFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
            splitterTemplate.VisualTree = lineFactory;
            splitter.Template = splitterTemplate;

            splitter.DragCompleted += (_, _) =>
            {
                for (var w = 0; w < colCount; w++)
                {
                    var aw = grid.ColumnDefinitions[w * 2].ActualWidth;
                    item.TodoColumnWidths[w] = aw <= 0 ? 1 : Math.Clamp(Math.Round(aw, 2), 0.2, 100);
                }
                _controller.MarkDirty();
            };
            Grid.SetColumn(splitter, splitterIndex);
            grid.Children.Add(splitter);

            var extraText = extraIndex < item.TodoExtraColumns.Count ? item.TodoExtraColumns[extraIndex] : "";
            var extraBox = CreateTodoColumnTextBox(paper, win, item, c, extraText);
            AttachColumnContextMenu(extraBox, paper, win, item, c);
            Grid.SetColumn(extraBox, colIndex);
            grid.Children.Add(extraBox);
        }

        return grid;
    }

    private TextBox CreateTodoColumnTextBox(PaperData paper, PaperWindow win, PaperItem item, int colIndex, string initialText)
    {
        var box = new TextBox
        {
            Text = initialText,
            FontSize = 13,
            Foreground = item.Done ? SiYuanTheme.TextMuted : SiYuanTheme.TextPrimary,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = SiYuanTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            Padding = new Thickness(4, 2, 4, 2)
        };

        box.TextChanged += (_, _) =>
        {
            if (colIndex == 0)
            {
                item.Text = box.Text;
            }
            else if (colIndex - 1 < item.TodoExtraColumns.Count)
            {
                item.TodoExtraColumns[colIndex - 1] = box.Text;
            }
            _controller.MarkDirty();
            win.RefreshTodoRowsForExternalChange();
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None))
            {
                win.AddItemAfter(item, "");
                RefreshContent();
                e.Handled = true;
            }
        };

        return box;
    }

    private void AttachColumnContextMenu(FrameworkElement element, PaperData paper, PaperWindow win, PaperItem item, int columnIndex)
    {
        var menu = CreateSiYuanContextMenu();

        var colCount = PaperWindow.TodoItemColumnCount(item);
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineInsertColumn"), () =>
        {
            win.InsertTodoColumnBefore(item, columnIndex);
            RefreshContent();
        }, isEnabled: colCount < 4));

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeleteColumn"), () =>
        {
            win.DeleteTodoColumn(item, columnIndex);
            RefreshContent();
        }, isEnabled: colCount > 1));

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineAddColumn"), () =>
        {
            win.SetTodoItemColumnCount(item, colCount + 1);
            RefreshContent();
        }, isEnabled: colCount < 4));

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineRemoveColumn"), () =>
        {
            win.SetTodoItemColumnCount(item, colCount - 1);
            RefreshContent();
        }, isEnabled: colCount > 1));

        menu.Items.Add(CreateSiYuanMenuSeparator());

        menu.Items.Add(CreateSiYuanMenuItem(string.IsNullOrWhiteSpace(item.DueAtLocal) ? Strings.Get("OutlineSetDue") : Strings.Get("OutlineEditDue"), () =>
        {
            win.ShowTodoDueDialog(item, owner: this, onSaved: RefreshContent);
        }));

        if (!string.IsNullOrWhiteSpace(item.DueAtLocal))
        {
            menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineClearDue"), () =>
            {
                win.ClearTodoDue(item);
                RefreshContent();
            }));
        }

        menu.Items.Add(CreateSiYuanMenuItem(item.ReminderIntervalValue.HasValue ? Strings.Get("OutlineEditReminder") : Strings.Get("OutlineSetReminder"), () =>
        {
            win.ShowTodoReminderIntervalDialog(item, owner: this, onSaved: RefreshContent);
        }));

        if (item.ReminderIntervalValue.HasValue)
        {
            menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineClearReminder"), () =>
            {
                win.ClearTodoReminderInterval(item);
                RefreshContent();
            }));
        }

        menu.Items.Add(CreateSiYuanMenuSeparator());

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeleteTodo"), () =>
        {
            win.RemoveItem(item);
            RefreshContent();
        }));

        element.ContextMenu = menu;
    }

    private static ContextMenu CreateSiYuanContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = Theme.PaperBrush,
            BorderBrush = SiYuanTheme.Border,
            BorderThickness = new Thickness(1),
            Foreground = SiYuanTheme.TextPrimary,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Padding = new Thickness(4)
        };

        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, AppUi.BlockRadius);
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));
        borderFactory.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = SiYuanTheme.IsDark ? 0.5 : 0.16,
            Color = Colors.Black
        });

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        borderFactory.AppendChild(presenter);

        menu.Template = new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = borderFactory
        };

        return menu;
    }

    private static MenuItem CreateSiYuanMenuItem(string header, Action onClick, bool isEnabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            FontSize = 12,
            Foreground = isEnabled ? SiYuanTheme.TextPrimary : SiYuanTheme.TextMuted,
            Padding = new Thickness(10, 5, 14, 5),
            Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow,
            FontFamily = AppTypography.UiFontFamily
        };

        var style = new Style(typeof(MenuItem));
        var template = new ControlTemplate(typeof(MenuItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemBorder";
        border.SetValue(Border.CornerRadiusProperty, AppUi.ControlRadius);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(HeaderedItemsControl.HeaderProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, SiYuanTheme.HoverBg, "ItemBorder"));
        template.Triggers.Add(hoverTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        item.Style = style;

        item.Click += (_, _) => onClick();
        return item;
    }

    private static Separator CreateSiYuanMenuSeparator()
    {
        var sep = new Separator { Margin = new Thickness(4, 3, 4, 3) };
        var template = new ControlTemplate(typeof(Separator));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.HeightProperty, 1.0);
        border.SetValue(Border.BackgroundProperty, SiYuanTheme.Border);
        border.SetValue(UIElement.OpacityProperty, 0.7);
        template.VisualTree = border;
        sep.Template = template;
        return sep;
    }

    private UIElement BuildDetailTodoDueBadge(PaperData paper, PaperWindow win, PaperItem item)
    {
        var hasDue = PaperWindow.TryGetTodoDueAt(item, out var dueAt);

        if (!hasDue)
        {
            var setDueBtn = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = AppUi.ControlRadius,
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = Strings.Get("OutlineDueHint")
            };
            var dueIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2.5,3.5 L 11.5,3.5 M 2.5,2 C 2,2 1.5,2.5 1.5,3 L 1.5,12 C 1.5,12.5 2,13 2.5,13 L 11.5,13 C 12,13 12.5,12.5 12.5,12 L 12.5,3 C 12.5,2.5 12,2 11.5,2 Z M 4,1 L 4,3 M 10,1 L 10,3 M 4.5,6 L 6,6 M 8,6 L 9.5,6 M 4.5,8.5 L 6,8.5 M 8,8.5 L 9.5,8.5"),
                Stroke = SiYuanTheme.TextMuted,
                StrokeThickness = 1.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 12,
                Height = 12,
                Opacity = 0.35,
                VerticalAlignment = VerticalAlignment.Center
            };
            setDueBtn.Child = dueIcon;
            setDueBtn.MouseEnter += (_, _) =>
            {
                setDueBtn.Background = SiYuanTheme.HoverBg;
                dueIcon.Opacity = 0.9;
                dueIcon.Stroke = SiYuanTheme.TextPrimary;
            };
            setDueBtn.MouseLeave += (_, _) =>
            {
                setDueBtn.Background = Brushes.Transparent;
                dueIcon.Opacity = 0.35;
                dueIcon.Stroke = SiYuanTheme.TextMuted;
            };
            setDueBtn.MouseLeftButtonDown += (_, e) =>
            {
                win.ShowTodoDueDialog(item, owner: this, onSaved: RefreshContent);
                e.Handled = true;
            };
            return setDueBtn;
        }

        var now = DateTime.Now;
        var isPastDue = dueAt < now;
        var isSoon = !isPastDue && dueAt <= now.AddMinutes(AppController.TodoReminderLeadTimeMinutes);

        var dateText = (dueAt.Date == now.Date ? Strings.Get("OutlineToday") + dueAt.ToString("HH:mm") :
                        dueAt.Date == now.Date.AddDays(1) ? Strings.Get("OutlineTomorrow") + dueAt.ToString("HH:mm") :
                        dueAt.ToString("M/d HH:mm"));

        var badge = new Border
        {
            Background = isPastDue ? SiYuanTheme.DangerBg : isSoon ? SiYuanTheme.CautionBg : SiYuanTheme.BlockBg,
            BorderBrush = isPastDue ? SiYuanTheme.Danger : isSoon ? SiYuanTheme.CautionText : SiYuanTheme.BlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = AppUi.ControlRadius,
            Padding = new Thickness(6, 2, 7, 2),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Format("ToolTipTodoDue", dueAt.ToString("yyyy-MM-dd HH:mm")) + (item.ReminderIntervalValue.HasValue ? Strings.Format("OutlineReminderEvery", item.ReminderIntervalValue, Strings.Get(item.ReminderIntervalUnit == "hours" ? "TodoReminderIntervalUnitHours" : "TodoReminderIntervalUnitMinutes")) : "")
        };

        var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var badgeIcon = new System.Windows.Shapes.Path
        {
            Data = isPastDue
                ? Geometry.Parse("M 7,2 L 13,12 L 1,12 Z M 7,5.5 L 7,8.5 M 7,10 L 7,10.5")
                : isSoon
                ? Geometry.Parse("M 7,1.5 C 3.96,1.5 1.5,3.96 1.5,7 C 1.5,10.04 3.96,12.5 7,12.5 C 10.04,12.5 12.5,10.04 12.5,7 C 12.5,3.96 10.04,1.5 7,1.5 Z M 7,4 L 7,7 L 9,9")
                : Geometry.Parse("M 2.5,3.5 L 11.5,3.5 M 2.5,2 C 2,2 1.5,2.5 1.5,3 L 1.5,12 C 1.5,12.5 2,13 2.5,13 L 11.5,13 C 12,13 12.5,12.5 12.5,12 L 12.5,3 C 12.5,2.5 12,2 11.5,2 Z M 4,1 L 4,3 M 10,1 L 10,3"),
            Stroke = isPastDue ? SiYuanTheme.Danger : isSoon ? SiYuanTheme.CautionText : SiYuanTheme.TextMuted,
            StrokeThickness = 1.1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 11,
            Height = 11,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badgeStack.Children.Add(badgeIcon);

        var textBlock = new TextBlock
        {
            Text = dateText,
            FontSize = 10.5,
            FontWeight = isPastDue || isSoon ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isPastDue ? SiYuanTheme.Danger : isSoon ? SiYuanTheme.CautionText : SiYuanTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        badgeStack.Children.Add(textBlock);
        badge.Child = badgeStack;

        badge.MouseEnter += (_, _) => badge.Opacity = 0.82;
        badge.MouseLeave += (_, _) => badge.Opacity = 1.0;
        badge.MouseLeftButtonDown += (_, e) =>
        {
            win.ShowTodoDueDialog(item, owner: this, onSaved: RefreshContent);
            e.Handled = true;
        };

        var menu = CreateSiYuanContextMenu();
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineEditDue"), () => win.ShowTodoDueDialog(item, owner: this, onSaved: RefreshContent)));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineClearDue"), () => { win.ClearTodoDue(item); RefreshContent(); }));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineSetReminder"), () => win.ShowTodoReminderIntervalDialog(item, owner: this, onSaved: RefreshContent)));
        if (item.ReminderIntervalValue.HasValue)
        {
            menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineClearReminder"), () => { win.ClearTodoReminderInterval(item); RefreshContent(); }));
        }

        badge.ContextMenu = menu;
        return badge;
    }

    private UIElement BuildDetailNoteContent(PaperData paper)
    {
        if (!_noteHosts.TryGetValue(paper.Id, out var host))
        {
            host = new Grid();
            var editor = new MarkdownTextBox
            {
                Text = paper.Content ?? "", MaxLength = 100000,
                Margin = new Thickness(0, 4, 16, 24),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            editor.TextChanged += (_, _) =>
            {
                if (_updatingNoteEditor) return;
                paper.Content = editor.Text;
                _controller.MarkDirty();
                _controller.GetPaperWindow(paper.Id)?.RefreshNoteContentForExternalChange();
            };
            host.Children.Add(editor);
            var copy = new Button
            {
                Content = Strings.Get("MenuCopy"), Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(8, 3, 8, 3)
            };
            string? code = null;
            editor.MouseMove += (_, e) =>
            {
                code = editor.CodeBlockAtPoint(e.GetPosition(editor));
                copy.Visibility = code != null ? Visibility.Visible : Visibility.Collapsed;
                copy.Margin = new Thickness(0, Math.Max(0, e.GetPosition(host).Y - 28), 28, 0);
            };
            host.MouseLeave += (_, _) => copy.Visibility = Visibility.Collapsed;
            copy.Click += async (_, _) =>
            {
                if (code == null) return;
                Clipboard.SetText(code);
                copy.Content = Strings.Get("OutlineCopied");
                await System.Threading.Tasks.Task.Delay(900);
                copy.Content = Strings.Get("MenuCopy");
            };
            host.Children.Add(copy);
            _noteHosts[paper.Id] = host;
        }
        if (host.Parent is Panel panel) panel.Children.Remove(host);
        var box = host.Children.OfType<MarkdownTextBox>().Single();
        var focused = box.IsKeyboardFocusWithin;
        var start = box.SelectionStart;
        var length = box.SelectionLength;
        var vertical = box.VerticalOffset;
        var horizontal = box.HorizontalOffset;
        _updatingNoteEditor = true;
        try
        {
            if (box.Text != paper.Content) box.Document.Replace(0, box.Document.TextLength, paper.Content ?? "");
            box.SetMarkdownRenderMode(_controller.State.MarkdownRenderMode);
            box.SetTextZoom(paper.TextZoom);
            box.SetLineSpacing(_controller.State.NoteLineSpacing);
            box.SetPreviewMode(_notePreviewMode);
            box.RefreshVisualStyle();
        }
        finally { _updatingNoteEditor = false; }
        host.Children.OfType<Button>().Single().Style = AppUi.ButtonStyle();
        if (_notePreviewMode) AttachNotePreviewContextMenu(box, paper);
        else AttachNoteContextMenu(box, paper);
        var heading = _pendingHeadingScrollTarget;
        _pendingHeadingScrollTarget = null;
        Dispatcher.BeginInvoke(() =>
        {
            if (!host.IsVisible) return;
            var selection = Math.Min(start, box.Text.Length);
            box.Select(selection, Math.Min(length, box.Text.Length - selection));
            box.ScrollToHorizontalOffset(horizontal);
            box.ScrollToVerticalOffset(vertical);
            if (heading != null)
            {
                foreach (var line in box.Document.Lines)
                {
                    var text = box.Document.GetText(line);
                    var match = MarkdownHeadingRegex().Match(text);
                    if (!match.Success || !string.Equals(match.Groups[2].Value.Trim(), heading, StringComparison.Ordinal)) continue;
                    box.Select(line.Offset, line.Length);
                    box.ScrollToLine(line.LineNumber);
                    break;
                }
            }
            if (focused && !_notePreviewMode && IsActive) box.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        return host;
    }

    private void AttachNoteContextMenu(MarkdownTextBox box, PaperData paper)
    {
        var menu = CreateSiYuanContextMenu();

        var cutItem = CreateSiYuanMenuItem(Strings.Get("OutlineCut"), () => box.Cut(), isEnabled: box.SelectionLength > 0);
        var copyItem = CreateSiYuanMenuItem(Strings.Get("OutlineCopy"), () => box.Copy(), isEnabled: box.SelectionLength > 0);
        var pasteItem = CreateSiYuanMenuItem(Strings.Get("OutlinePaste"), () => box.Paste(), isEnabled: Clipboard.ContainsText());
        var selectAllItem = CreateSiYuanMenuItem(Strings.Get("OutlineSelectAll"), () => box.SelectAll());

        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(selectAllItem);

        menu.Items.Add(CreateSiYuanMenuSeparator());

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineBold"), () => WrapSelection(box, "**", "**")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineItalic"), () => WrapSelection(box, "*", "*")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineStrikethrough"), () => WrapSelection(box, "~~", "~~")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineHeading1"), () => InsertLinePrefix(box, "# ")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineHeading2"), () => InsertLinePrefix(box, "## ")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineQuote"), () => InsertLinePrefix(box, "> ")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineList"), () => InsertLinePrefix(box, "- ")));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineCodeBlock"), () => WrapSelection(box, "```\n", "\n```")));

        menu.Items.Add(CreateSiYuanMenuSeparator());

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineSwitchPreview"), () =>
        {
            _notePreviewMode = true;
            RefreshDetail();
        }));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineInsertTime"), () =>
        {
            InsertText(box, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        }));

        menu.Items.Add(CreateSiYuanMenuSeparator());

        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeletePaper"), () =>
        {
            if (MessageBox.Show(Strings.Format("OutlineDeleteConfirm", _controller.PaperTitleText(paper)), Strings.Get("OutlineDeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _controller.DeletePaper(paper);
                _selectedPaperId = null;
                RefreshContent();
            }
        }));

        menu.Opened += (_, _) =>
        {
            cutItem.IsEnabled = box.SelectionLength > 0;
            copyItem.IsEnabled = box.SelectionLength > 0;
            pasteItem.IsEnabled = Clipboard.ContainsText();
        };

        box.ContextMenu = menu;
    }

    private void AttachNotePreviewContextMenu(FrameworkElement element, PaperData paper)
    {
        var menu = CreateSiYuanContextMenu();
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineSwitchEdit"), () =>
        {
            _notePreviewMode = false;
            RefreshDetail();
        }));
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineCopyAll"), () =>
        {
            Clipboard.SetText(paper.Content ?? "");
        }));
        menu.Items.Add(CreateSiYuanMenuSeparator());
        menu.Items.Add(CreateSiYuanMenuItem(Strings.Get("OutlineDeletePaper"), () =>
        {
            if (MessageBox.Show(Strings.Format("OutlineDeleteConfirm", _controller.PaperTitleText(paper)), Strings.Get("OutlineDeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _controller.DeletePaper(paper);
                _selectedPaperId = null;
                RefreshContent();
            }
        }));
        element.ContextMenu = menu;
    }

    private static void WrapSelection(MarkdownTextBox box, string prefix, string suffix)
    {
        var start = box.SelectionStart;
        var len = box.SelectionLength;
        var selected = box.SelectedText;
        if (len == 0)
        {
            box.SelectedText = prefix + suffix;
            box.SelectionStart = start + prefix.Length;
            box.SelectionLength = 0;
        }
        else
        {
            box.SelectedText = prefix + selected + suffix;
            box.SelectionStart = start;
            box.SelectionLength = len + prefix.Length + suffix.Length;
        }
        box.Focus();
    }

    private static void InsertLinePrefix(MarkdownTextBox box, string linePrefix)
    {
        box.InsertLinePrefix(linePrefix);
        box.Focus();
    }

    private static void InsertText(MarkdownTextBox box, string insert)
    {
        var start = box.SelectionStart;
        box.SelectedText = insert;
        box.SelectionStart = start + insert.Length;
        box.SelectionLength = 0;
        box.Focus();
    }

    private static List<(int Level, string Text)> ExtractHeadings(string markdown)
    {
        var result = new List<(int Level, string Text)>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;

        var matches = MarkdownHeadingRegex().Matches(markdown);
        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var hashCount = match.Groups[1].Value.Length;
                var text = match.Groups[2].Value.Trim();
                result.Add((Math.Clamp(hashCount, 1, 6), text));
            }
        }
        return result;
    }

    /// <summary>
    /// 思源笔记（SiYuan Note）专属自洽调色盘：
    /// 严谨区分亮色与暗色模式，彻底告别色调冲突与杂乱质感。
    /// </summary>
    private static class SiYuanTheme
    {
        public static bool IsDark => Theme.IsDark;

        public static Brush WindowBorder => Theme.PaperBorderBrush;
        public static Brush TitlebarBg => Theme.WorkspaceBrush;
        public static Brush ActivityBarBg => Theme.SurfaceBrush;
        public static Brush SidebarBg => Theme.SurfaceBrush;
        public static Brush EditorBg => Theme.PaperBrush;
        public static Brush InputBg => Theme.SurfaceBrush;
        public static Brush InputBorder => Theme.CheckBoxBorderBrush;
        public static Brush Border => Theme.PaperBorderBrush;
        public static Brush Guideline => Theme.PaperBorderBrush;
        public static Brush HoverBg => Theme.HoverBrush;
        public static Brush ActiveRowBg => Theme.SelectionBrush;
        public static Brush TreeSelectedBg => Theme.SelectionBrush;
        public static Brush TreeSelectedBorder => Theme.SelectionBrush;
        public static Brush BlockBg => Theme.SurfaceBrush;
        public static Brush BlockBorder => Theme.PaperBorderBrush;
        public static Brush TextPrimary => Theme.TextBrush;
        public static Brush TextMuted => Theme.WeakTextBrush;
        public static Brush Accent => Theme.ActiveBrush;
        public static Brush Danger => Theme.DangerBrush;
        public static Brush DangerBg => Theme.Danger(24);
        public static Brush CautionBg => IsDark ? Solid("#3A2B14") : Solid("#FFF8E6");
        public static Brush CautionText => IsDark ? Solid("#FFAB2E") : Solid("#B25E00");
        public static Brush CodeBg => Theme.CodeBrush;
        public static Brush CodeBorder => Theme.PaperBorderBrush;

        public static SolidColorBrush Solid(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
