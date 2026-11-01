using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly object PersistentScriptProcessLock = new();
    private static readonly Dictionary<string, Process> PersistentScriptProcesses = new(StringComparer.OrdinalIgnoreCase);
    private const string NotePreviewStatusText = "\u9884\u89c8";
    private const string NoteEditStatusText = "\u7f16\u8f91";
    private Border? _noteToolbar;
    private Border? _noteStatusBar;
    private Border? _noteSurfaceCanvas;
    private Border? _noteBindingLine;

    public void UpdateMarkdownRenderMode()
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            var mode = _controller.State.MarkdownRenderMode;
            TraceNoteRender($"UpdateMarkdownRenderMode rebuild mode={mode}");
            RebuildNoteBodyForMarkdownMode();
        }
    }

    public void RefreshNoteContentForExternalChange()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        if (_noteBox.Text != (_paper.Content ?? ""))
        {
            _noteBox.Text = _paper.Content ?? "";
            RefreshNoteStatusBar();
        }
    }

    private void TraceNoteRender(string message)
    {
#if DEBUG
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "md-render-trace.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} paper={_paper.Id[..Math.Min(6, _paper.Id.Length)]} {message}{Environment.NewLine}";
            lock (NoteRenderTraceLock)
            {
                System.IO.File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Test-only diagnostics must never affect note interaction.
        }
#endif
    }

    private void RebuildNoteBodyForMarkdownMode()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var oldBox = _noteBox;
        var text = oldBox?.Text ?? _paper.Content ?? "";
        var caret = oldBox?.CaretIndex ?? 0;
        var verticalOffset = oldBox?.VerticalOffset ?? 0;
        var horizontalOffset = oldBox?.HorizontalOffset ?? 0;
        _paper.Content = text;

        TraceNoteRender($"RebuildNoteBody start textLength={text.Length} caret={caret} v={verticalOffset:F1} h={horizontalOffset:F1}");

        var oldBodies = new List<UIElement>();
        if (_noteBodyElement != null)
        {
            oldBodies.Add(_noteBodyElement);
        }
        else
        {
            var zoomHost = _textZoomIndicator?.Parent as UIElement;
            foreach (UIElement child in _shell.Children)
            {
                if (Grid.GetRow(child) == 1 && !ReferenceEquals(child, zoomHost))
                {
                    oldBodies.Add(child);
                }
            }
        }

        _noteBox = null;
        _showNotePreview = null;

        var body = BuildNoteBody();
        body.Opacity = 0;
        body.IsHitTestVisible = false;
        Grid.SetRow(body, 1);
        Panel.SetZIndex(body, 1);
        _noteBodyElement = body;
        _shell.Children.Add(body);

        if (_noteBox == null)
        {
            TraceNoteRender("RebuildNoteBody end: no note box");
            return;
        }

        _noteBox.CaretIndex = Math.Clamp(caret, 0, _noteBox.Text.Length);
        _showNotePreview?.Invoke();
        body.UpdateLayout();

        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_noteBox == null)
                {
                    return;
                }

                foreach (var oldBody in oldBodies)
                {
                    _shell.Children.Remove(oldBody);
                }

                body.Opacity = 1;
                body.IsHitTestVisible = true;
                _noteBox.ScrollToHorizontalOffset(horizontalOffset);
                _noteBox.ScrollToVerticalOffset(verticalOffset);
                _showNotePreview?.Invoke();
                TraceNoteRender($"RebuildNoteBody restored caret={_noteBox.CaretIndex} v={verticalOffset:F1} h={horizontalOffset:F1}");
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ExitNoteEditor()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        if (_noteBox.ContextMenu?.IsOpen == true)
        {
            return;
        }

        Keyboard.ClearFocus();
        _showNotePreview?.Invoke();
    }


    private UIElement BuildNoteBody()
    {
        var host = new Grid
        {
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _noteBox = new MarkdownTextBox
        {
            MaxLength = 100000,
            Text = _paper.Content ?? "",
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = NoteTypography.FontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Language = NoteTypography.Language,
            Margin = NotePageContentMargin(),
            FocusVisualStyle = null
        };
        NoteTypography.ApplyTextRendering(_noteBox);
        var box = _noteBox;
        box.SetMarkdownRenderMode(_controller.State.MarkdownRenderMode);
        box.SetTextZoom(CurrentTextZoom());
        box.SetLineSpacing(_controller.State.NoteLineSpacing);

        var toolbar = BuildNoteCanvasToolbar();
        Grid.SetRow(toolbar, 0);
        host.Children.Add(toolbar);

        var noteSurface = BuildNoteSurface(box);
        Grid.SetRow(noteSurface, 1);
        host.Children.Add(noteSurface);

        var statusBar = BuildNoteStatusBar();
        Grid.SetRow(statusBar, 2);
        host.Children.Add(statusBar);

        var editorMenu = CreateContextMenu();
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuFormat")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuBold"), (_, _) => box.WrapSelection("**", "**")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuItalic"), (_, _) => box.WrapSelection("*", "*")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuStrikethrough"), (_, _) => box.WrapSelection("~~", "~~")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuHeading"), (_, _) => box.InsertLinePrefix("# ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuQuote"), (_, _) => box.InsertLinePrefix("> ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuList"), (_, _) => box.InsertLinePrefix("- ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCodeBlock"), (_, _) => box.WrapSelection("```\n", "\n```")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuInsertLink"), (_, _) => box.InsertMarkdownLink()));
        editorMenu.Items.Add(MenuSeparator());
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuText")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCopy"), (_, _) => box.Copy()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuPaste"), (_, _) => box.Paste()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuSelectAll"), (_, _) => box.SelectAll()));

        var previewMenu = BuildPaperContextMenu();
        var isPreviewing = false;
        var isEnteringEditorFromPreview = false;

        void ShowPreview()
        {
            TraceNoteRender($"ShowPreview before isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SelectionLength = 0;
            box.SetPreviewMode(true);
            box.ContextMenu = previewMenu;
            isPreviewing = true;
            RefreshNoteStatusBar();
            TraceNoteRender($"ShowPreview after isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        }

        _showNotePreview = ShowPreview;

        void ShowEditor(bool focus = true)
        {
            TraceNoteRender($"ShowEditor before focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SetPreviewMode(false);
            box.ContextMenu = editorMenu;
            isPreviewing = false;

            if (focus && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            RefreshNoteStatusBar();
            TraceNoteRender($"ShowEditor after focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} focused={box.IsKeyboardFocusWithin}");
        }

        void ShowEditorAtPreviewPoint(Point previewPoint)
        {
            TraceNoteRender($"ShowEditorAtPreviewPoint x={previewPoint.X:F1} y={previewPoint.Y:F1}");
            var hasPreviewCaret = box.TryGetCharacterIndexFromPoint(previewPoint, out var caretIndex);

            isEnteringEditorFromPreview = true;
            ShowEditor(focus: false);

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }

            if (hasPreviewCaret)
            {
                box.CaretIndex = Math.Clamp(caretIndex, 0, box.Text.Length);
                box.SelectionLength = 0;
            }
            TraceNoteRender($"ShowEditorAtPreviewPoint after hasCaret={hasPreviewCaret} caret={box.CaretIndex}");
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    isEnteringEditorFromPreview = false;
                    TraceNoteRender($"ShowEditorAtPreviewPoint release focused={box.IsKeyboardFocusWithin} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        static void OpenMarkdownLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Link opening is optional; the note should never crash because a URL handler failed.
            }
        }

        box.TextChanged += (_, _) =>
        {
            if (_applyingSyncVisuals) return;
            var wasScriptCapsule = IsScriptCapsuleText(_paper.Content ?? "");
            _paper.Content = box.Text;
            _paper.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var isScriptCapsule = IsScriptCapsuleText(_paper.Content ?? "");
            if (wasScriptCapsule != isScriptCapsule)
            {
                RefreshCapsuleLabel();
                RefreshPaperContextMenus();
                _controller.RefreshTodoRowsForLinkedNote(_paper.Id);
            }
            _controller.MarkDirty();
            RefreshNoteStatusBar();
        };

        box.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (e.Key == Key.B)
            {
                box.WrapSelection("**", "**");
                e.Handled = true;
            }
            else if (e.Key == Key.I)
            {
                box.WrapSelection("*", "*");
                e.Handled = true;
            }
            else if (e.Key == Key.K)
            {
                box.InsertMarkdownLink();
                e.Handled = true;
            }
        };

        box.GotKeyboardFocus += (_, _) =>
        {
            TraceNoteRender($"GotKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        };

        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.ContextMenu != null && box.ContextMenu.IsOpen)
            {
                TraceNoteRender("LostKeyboardFocus ignored: context menu open");
                return;
            }
            if (isEnteringEditorFromPreview)
            {
                TraceNoteRender($"LostKeyboardFocus ignored: entering editor isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }
            TraceNoteRender($"LostKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            ShowPreview();
        };

        MouseButtonEventHandler noteMouseDown = (_, e) =>
        {
            if (IsScrollBarInteractionSource(e.OriginalSource as DependencyObject, box))
            {
                TraceNoteRender($"PreviewMouseLeftButtonDown ignored: scrollbar isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }

            var textViewPoint = e.GetPosition(box.TextArea.TextView);
            TraceNoteRender($"PreviewMouseLeftButtonDown isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} handled={e.Handled}");
            if (!isPreviewing)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var editUrl))
                {
                    OpenMarkdownLink(editUrl);
                    e.Handled = true;
                }
                return;
            }

            var point = e.GetPosition(box);
            if (box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var url))
            {
                OpenMarkdownLink(url);
                e.Handled = true;
                return;
            }

            ShowEditorAtPreviewPoint(point);
            e.Handled = true;
        };
        box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, noteMouseDown, true);

        box.MouseMove += (sender, e) =>
        {
            var isOverLink = box.TryGetMarkdownLinkFromTextViewPoint(e.GetPosition(box.TextArea.TextView), out _);
            if (isPreviewing)
            {
                box.SetInteractionCursor(isOverLink ? Cursors.Hand : Cursors.Arrow);
            }
            else
            {
                box.SetInteractionCursor(isOverLink && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                    ? Cursors.Hand
                    : Cursors.IBeam);
            }
        };

        box.MouseLeave += (_, _) =>
        {
            box.SetInteractionCursor(isPreviewing ? Cursors.Arrow : Cursors.IBeam);
        };

        editorMenu.Closed += (_, _) =>
        {
            if (!isPreviewing && !box.IsFocused && !box.IsKeyboardFocusWithin)
            {
                ShowPreview();
            }
        };

        if (box.IsFocused || string.IsNullOrEmpty(box.Text))
        {
            ShowEditor();
        }
        else
        {
            ShowPreview();
        }

        return host;
    }

    private static Thickness NotePageContentMargin()
    {
        var padding = NoteTypography.ContentPadding;
        return new Thickness(padding.Left + 3, padding.Top + 4, padding.Right + 6, padding.Bottom + 4);
    }

    private static Brush NoteCanvasBrush => Theme.PaperBrush;

    private FrameworkElement BuildNoteSurface(MarkdownTextBox box)
    {
        var canvas = new Border
        {
            Margin = new Thickness(8, 6, 8, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = AppUi.PanelRadius,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        canvas.SetResourceReference(Border.BackgroundProperty, "NoteCanvasBrushKey");
        canvas.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        _noteSurfaceCanvas = canvas;

        var page = new Border
        {
            BorderThickness = new Thickness(0),
            CornerRadius = AppUi.BlockRadius,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        page.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        page.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");

        var pageGrid = new Grid
        {
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };

        var bindingLine = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(14, 14, 0, 14),
            CornerRadius = new CornerRadius(1),
            Opacity = 0.72,
            IsHitTestVisible = false
        };
        bindingLine.SetResourceReference(Border.BackgroundProperty, "NoteBindingBrushKey");
        _noteBindingLine = bindingLine;

        pageGrid.Children.Add(bindingLine);
        pageGrid.Children.Add(box);
        _noteCanvasLayer = new Canvas
        {
            ClipToBounds = true,
            Background = null,
            Focusable = false
        };
        pageGrid.Children.Add(_noteCanvasLayer);
        RefreshNoteCanvasLayer();
        page.Child = pageGrid;
        canvas.Child = page;
        return canvas;
    }

    private Border BuildNoteCanvasToolbar()
    {
        var toolbar = new Border
        {
            MinHeight = 31,
            Padding = new Thickness(9, 3, 9, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            SnapsToDevicePixels = true
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "NoteStatusBrushKey");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        _noteToolbar = toolbar;

        var layout = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        tools.Children.Add(NoteCanvasToolButton("{}", "\u6dfb\u52a0\u4ee3\u7801\u5757", () => AddNoteCanvasElement(NoteCanvasElementTypes.Code)));

        _noteCanvasElementCountText = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _noteCanvasElementCountText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");

        Grid.SetColumn(tools, 0);
        Grid.SetColumn(_noteCanvasElementCountText, 2);
        layout.Children.Add(tools);
        layout.Children.Add(_noteCanvasElementCountText);

        toolbar.Child = layout;
        RefreshNoteCanvasSummary();
        return toolbar;
    }

    private Button NoteCanvasToolButton(string label, string tooltip, Action onClick)
    {
        var button = IconButton(label, tooltip);
        button.MinWidth = 28;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Border BuildNoteStatusBar()
    {
        var bar = new Border
        {
            MinHeight = 26,
            Padding = new Thickness(10, 3, 10, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        bar.SetResourceReference(Border.BackgroundProperty, "NoteStatusBrushKey");
        bar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        _noteStatusBar = bar;

        var layout = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modePill = new Border
        {
            MinWidth = 42,
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = AppUi.ControlRadius,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        modePill.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");

        _noteModeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _noteModeText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        modePill.Child = _noteModeText;

        _noteStatsText = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _noteStatsText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");

        _noteZoomText = new TextBlock
        {
            FontSize = 11,
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _noteZoomText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");

        Grid.SetColumn(modePill, 0);
        Grid.SetColumn(_noteStatsText, 1);
        Grid.SetColumn(_noteZoomText, 2);
        layout.Children.Add(modePill);
        layout.Children.Add(_noteStatsText);
        layout.Children.Add(_noteZoomText);

        bar.Child = layout;
        RefreshNoteStatusBar();
        return bar;
    }

    private void RefreshNoteStatusBar()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var text = _noteBox?.Text ?? _paper.Content ?? "";
        if (_noteModeText != null)
        {
            _noteModeText.Text = _noteBox?.IsPreviewMode == true ? NotePreviewStatusText : NoteEditStatusText;
        }

        if (_noteStatsText != null)
        {
            var curWidth = ActualWidth > 0 ? ActualWidth : Width;
            var elementCount = _paper.NoteCanvasElements?.Count ?? 0;
            if (curWidth < 250)
            {
                _noteStatsText.Text = $"{CountNoteTextCharacters(text)} \u5b57";
                if (_noteZoomText != null) _noteZoomText.Visibility = Visibility.Collapsed;
            }
            else if (curWidth < 300)
            {
                _noteStatsText.Text = $"{CountNoteTextCharacters(text)} \u5b57 \u00b7 {CountNoteLines(text)} \u884c";
                if (_noteZoomText != null) _noteZoomText.Visibility = Visibility.Visible;
            }
            else
            {
                var extra = elementCount > 0 ? $" | {elementCount} \u5143\u7d20" : "";
                _noteStatsText.Text = $"{BuildNoteStatsText(text)}{extra}";
                if (_noteZoomText != null) _noteZoomText.Visibility = Visibility.Visible;
            }
        }

        if (_noteZoomText != null && _noteZoomText.Visibility == Visibility.Visible)
        {
            _noteZoomText.Text = $"{(int)Math.Round(CurrentTextZoom() * 100)}%";
        }
    }

    internal void UpdateNoteSizeResponsiveLayout(double width, double height)
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var isShort = height < 240;
        var isNarrow = width < 280;

        if (_noteToolbar != null)
        {
            _noteToolbar.MinHeight = isShort ? 25 : 31;
            _noteToolbar.Padding = isShort ? new Thickness(6, 1, 6, 2) : new Thickness(9, 3, 9, 4);
        }

        if (_noteCanvasElementCountText != null)
        {
            _noteCanvasElementCountText.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_noteStatusBar != null)
        {
            _noteStatusBar.MinHeight = isShort ? 21 : 26;
            _noteStatusBar.Padding = isShort ? new Thickness(6, 1, 6, 2) : new Thickness(10, 3, 10, 4);
        }

        if (_noteSurfaceCanvas != null)
        {
            _noteSurfaceCanvas.Margin = isShort || isNarrow ? new Thickness(4, 2, 4, 0) : new Thickness(8, 6, 8, 0);
            _noteSurfaceCanvas.Padding = new Thickness(0);
        }

        if (_noteBindingLine != null)
        {
            _noteBindingLine.Margin = isNarrow ? new Thickness(8, 6, 0, 6) : new Thickness(14, 14, 0, 14);
        }

        if (_noteBox != null)
        {
            if (isNarrow)
            {
                _noteBox.Margin = new Thickness(15, isShort ? 2 : 4, 6, isShort ? 2 : 4);
            }
            else
            {
                var defaultMargin = NotePageContentMargin();
                _noteBox.Margin = isShort
                    ? new Thickness(defaultMargin.Left, 4, defaultMargin.Right, 4)
                    : defaultMargin;
            }
        }

        RefreshNoteStatusBar();
    }

    private static string BuildNoteStatsText(string text)
    {
        return $"{CountNoteTextCharacters(text)} \u5b57 | {CountNoteLines(text)} \u884c";
    }

    private static int CountNoteTextCharacters(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountNoteLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private void RefreshNoteCanvasLayer()
    {
        if (_paper.Type != PaperTypes.Note || _noteCanvasLayer == null)
        {
            return;
        }

        _noteCanvasLayer.Children.Clear();
        _paper.NoteCanvasElements ??= new List<NoteCanvasElement>();

        var orderedElements = OrderedNoteCanvasElements();
        for (var i = 0; i < orderedElements.Count; i++)
        {
            var element = orderedElements[i];
            var layerRank = i + 1;
            var view = BuildNoteCanvasElementView(element, layerRank, orderedElements.Count);
            Canvas.SetLeft(view, element.X);
            Canvas.SetTop(view, element.Y);
            Panel.SetZIndex(view, layerRank * 10);
            _noteCanvasLayer.Children.Add(view);
        }

        RefreshNoteCanvasSummary();
        RefreshNoteStatusBar();
    }

    private FrameworkElement BuildNoteCanvasElementView(NoteCanvasElement element, int layerRank, int layerCount)
    {
        var isActive = string.Equals(_activeNoteCanvasElement?.Id, element.Id, StringComparison.Ordinal);
        var isTopLayer = layerRank == layerCount && layerCount > 1;
        var chrome = new Border
        {
            Width = element.Width,
            Height = element.Height,
            MinWidth = 72,
            MinHeight = 48,
            CornerRadius = AppUi.BlockRadius,
            BorderThickness = new Thickness(isActive || isTopLayer ? 2 : 1),
            Background = NoteCanvasElementBackground(element),
            BorderBrush = NoteCanvasElementBorderBrush(element, isActive, isTopLayer),
            Effect = AppUi.NoteCanvasElementShadow(layerRank, layerCount, isActive || isTopLayer),
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        chrome.ContextMenu = BuildNoteCanvasElementContextMenu(element);
        chrome.PreviewMouseLeftButtonDown += (_, _) => _activeNoteCanvasElement = element;

        var root = new Grid
        {
            ClipToBounds = true
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });

        var header = new Border
        {
            Height = 22,
            Padding = new Thickness(7, 2, 6, 2),
            Background = NoteCanvasElementHeaderBrush(element, isActive, isTopLayer),
            Cursor = Cursors.SizeAll,
            ToolTip = "\u62d6\u52a8\u5143\u7d20"
        };
        var headerLayout = new Grid();
        headerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new TextBlock
        {
            Text = NoteCanvasElementTypeLabel(element.Type),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headerText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");

        var layerBadge = new Border
        {
            Padding = new Thickness(5, 0, 5, 1),
            MinWidth = 32,
            CornerRadius = AppUi.SmallRadius,
            Background = NoteCanvasElementLayerBadgeBrush(isActive, isTopLayer),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var layerText = new TextBlock
        {
            Text = isTopLayer ? $"\u9876\u5c42 {layerRank}" : $"\u5c42 {layerRank}",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layerText.SetResourceReference(TextBlock.ForegroundProperty, isTopLayer ? "TextBrushKey" : "WeakTextBrushKey");
        layerBadge.Child = layerText;

        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(layerBadge, 1);
        headerLayout.Children.Add(headerText);
        headerLayout.Children.Add(layerBadge);
        header.Child = headerLayout;

        header.PreviewMouseLeftButtonDown += (_, e) => BeginNoteCanvasElementDrag(element, chrome, header, e, resizing: false);
        header.PreviewMouseMove += (_, e) => UpdateNoteCanvasElementDrag(e);
        header.PreviewMouseLeftButtonUp += (_, e) => EndNoteCanvasElementDrag(e);
        header.LostMouseCapture += (_, _) => EndNoteCanvasElementDrag(null);

        var editor = new TextBox
        {
            Text = element.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            Padding = new Thickness(9, 7, 9, 7),
            FontFamily = element.Type == NoteCanvasElementTypes.Code ? NoteTypography.CodeFontFamily : NoteTypography.FontFamily,
            FontSize = element.Type == NoteCanvasElementTypes.Code ? NoteTypography.CodeFontSize : NoteTypography.FontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Language = NoteTypography.Language,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
            ContextMenu = chrome.ContextMenu
        };
        NoteTypography.ApplyTextRendering(editor);
        editor.TextChanged += (_, _) =>
        {
            if (!string.Equals(element.Text, editor.Text, StringComparison.Ordinal))
            {
                element.Text = editor.Text;
                _controller.MarkDirty();
                RefreshNoteCanvasSummary();
                RefreshNoteStatusBar();
            }
        };
        editor.PreviewMouseLeftButtonDown += (_, _) => _activeNoteCanvasElement = element;

        var resizeGrip = new Border
        {
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
            CornerRadius = new CornerRadius(4),
            Background = Theme.Tint((byte)(Theme.IsDark ? 72 : 58)),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "\u8c03\u6574\u5927\u5c0f"
        };
        resizeGrip.PreviewMouseLeftButtonDown += (_, e) => BeginNoteCanvasElementDrag(element, chrome, resizeGrip, e, resizing: true);
        resizeGrip.PreviewMouseMove += (_, e) => UpdateNoteCanvasElementDrag(e);
        resizeGrip.PreviewMouseLeftButtonUp += (_, e) => EndNoteCanvasElementDrag(e);
        resizeGrip.LostMouseCapture += (_, _) => EndNoteCanvasElementDrag(null);

        Grid.SetRow(header, 0);
        Grid.SetRow(editor, 1);
        Grid.SetRowSpan(resizeGrip, 2);
        root.Children.Add(header);
        root.Children.Add(editor);
        root.Children.Add(resizeGrip);

        chrome.Child = root;
        return chrome;
    }

    private ContextMenu BuildNoteCanvasElementContextMenu(NoteCanvasElement element)
    {
        var menu = CreateContextMenu();
        menu.Items.Add(MenuHeader($"{NoteCanvasElementTypeLabel(element.Type)} \u00b7 \u5c42 {NoteCanvasElementLayerRank(element)}"));
        menu.Items.Add(MenuItem("\u4e0a\u79fb\u4e00\u5c42", (_, _) => MoveNoteCanvasElementLayer(element, 1)));
        menu.Items.Add(MenuItem("\u4e0b\u79fb\u4e00\u5c42", (_, _) => MoveNoteCanvasElementLayer(element, -1)));
        menu.Items.Add(MenuItem("\u7f6e\u9876", (_, _) => BringNoteCanvasElementToFront(element)));
        menu.Items.Add(MenuItem("\u7f6e\u5e95", (_, _) => SendNoteCanvasElementToBack(element)));
        menu.Items.Add(MenuItem("\u590d\u5236", (_, _) => DuplicateNoteCanvasElement(element)));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuItem(Strings.Get("MenuDelete"), (_, _) => DeleteNoteCanvasElement(element)));
        return menu;
    }

    private void AddNoteCanvasElement(string type)
    {
        if (_paper.Type != PaperTypes.Note || _paper.IsPinnedToDesktop)
        {
            return;
        }

        _paper.NoteCanvasElements ??= new List<NoteCanvasElement>();
        var maxZ = _paper.NoteCanvasElements.Count == 0 ? 0 : _paper.NoteCanvasElements.Max(e => e.ZIndex);
        var element = new NoteCanvasElement
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = NoteCanvasElementTypes.Normalize(type),
            Text = DefaultNoteCanvasElementText(type),
            Width = DefaultNoteCanvasElementWidth(type),
            Height = DefaultNoteCanvasElementHeight(type),
            ZIndex = maxZ + 10
        };

        var center = NextNoteCanvasElementPoint(element.Width, element.Height);
        element.X = center.X;
        element.Y = center.Y;
        _paper.NoteCanvasElements.Add(element);
        _activeNoteCanvasElement = element;
        _controller.MarkDirty();
        RefreshNoteCanvasLayer();
    }

    private Point NextNoteCanvasElementPoint(double width, double height)
    {
        var layerWidth = _noteCanvasLayer?.ActualWidth > 0 ? _noteCanvasLayer.ActualWidth : Math.Max(220, _paper.Width - 40);
        var layerHeight = _noteCanvasLayer?.ActualHeight > 0 ? _noteCanvasLayer.ActualHeight : Math.Max(160, _paper.Height - 90);
        var offset = Math.Min(80, (_paper.NoteCanvasElements?.Count ?? 0) * 12);
        var x = Math.Max(10, Math.Min(layerWidth - width - 10, 28 + offset));
        var y = Math.Max(10, Math.Min(layerHeight - height - 10, 28 + offset));
        return new Point(x, y);
    }

    private static string DefaultNoteCanvasElementText(string type)
    {
        return "Console.WriteLine(\"PaperTodo\");";
    }

    private static double DefaultNoteCanvasElementWidth(string type)
    {
        return 230;
    }

    private static double DefaultNoteCanvasElementHeight(string type)
    {
        return 116;
    }

    private static string NoteCanvasElementTypeLabel(string type)
    {
        return "CODE";
    }

    private List<NoteCanvasElement> OrderedNoteCanvasElements()
    {
        return (_paper.NoteCanvasElements ?? new List<NoteCanvasElement>())
            .OrderBy(e => e.ZIndex)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private int NoteCanvasElementLayerRank(NoteCanvasElement element)
    {
        var ordered = OrderedNoteCanvasElements();
        var index = ordered.FindIndex(e => string.Equals(e.Id, element.Id, StringComparison.Ordinal));
        return index < 0 ? 1 : index + 1;
    }

    private static Brush NoteCanvasElementBackground(NoteCanvasElement element)
    {
        return Theme.CodeBrush;
    }

    private static Brush NoteCanvasElementHeaderBrush(NoteCanvasElement element, bool isActive, bool isTopLayer)
    {
        if (isActive || isTopLayer)
        {
            return Theme.Tint((byte)(Theme.IsDark ? 96 : 76));
        }

        return Theme.Tint((byte)(Theme.IsDark ? 70 : 50));
    }

    private static Brush NoteCanvasElementBorderBrush(NoteCanvasElement element, bool isActive, bool isTopLayer)
    {
        if (isActive || isTopLayer)
        {
            return Theme.ActiveBrush;
        }

        return Theme.Tint((byte)(Theme.IsDark ? 110 : 96));
    }

    private static Brush NoteCanvasElementLayerBadgeBrush(bool isActive, bool isTopLayer)
    {
        if (isActive || isTopLayer)
        {
            return Theme.Tint((byte)(Theme.IsDark ? 118 : 96));
        }

        return Theme.Tint((byte)(Theme.IsDark ? 46 : 34));
    }

    private void BeginNoteCanvasElementDrag(NoteCanvasElement element, FrameworkElement view, UIElement captureTarget, MouseButtonEventArgs e, bool resizing)
    {
        if (_paper.IsPinnedToDesktop || _noteCanvasLayer == null)
        {
            return;
        }

        _activeNoteCanvasElement = element;
        _noteCanvasDrag = new NoteCanvasDragState(
            element,
            view,
            captureTarget,
            e.GetPosition(_noteCanvasLayer),
            element.X,
            element.Y,
            element.Width,
            element.Height,
            resizing);
        captureTarget.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateNoteCanvasElementDrag(MouseEventArgs e)
    {
        var drag = _noteCanvasDrag;
        if (drag == null || _noteCanvasLayer == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(_noteCanvasLayer);
        var dx = point.X - drag.StartPoint.X;
        var dy = point.Y - drag.StartPoint.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            return;
        }

        if (drag.Resizing)
        {
            var width = Math.Clamp(drag.StartWidth + dx, 72, Math.Max(72, _noteCanvasLayer.ActualWidth - drag.Element.X));
            var height = Math.Clamp(drag.StartHeight + dy, 48, Math.Max(48, _noteCanvasLayer.ActualHeight - drag.Element.Y));
            drag.Element.Width = Math.Round(width, 1);
            drag.Element.Height = Math.Round(height, 1);
            drag.View.Width = drag.Element.Width;
            drag.View.Height = drag.Element.Height;
        }
        else
        {
            var maxX = Math.Max(0, _noteCanvasLayer.ActualWidth - drag.Element.Width);
            var maxY = Math.Max(0, _noteCanvasLayer.ActualHeight - drag.Element.Height);
            drag.Element.X = Math.Round(Math.Clamp(drag.StartX + dx, 0, maxX), 1);
            drag.Element.Y = Math.Round(Math.Clamp(drag.StartY + dy, 0, maxY), 1);
            Canvas.SetLeft(drag.View, drag.Element.X);
            Canvas.SetTop(drag.View, drag.Element.Y);
        }

        drag.Changed = true;
        e.Handled = true;
    }

    private void EndNoteCanvasElementDrag(MouseEventArgs? e)
    {
        var drag = _noteCanvasDrag;
        if (drag == null)
        {
            return;
        }

        _noteCanvasDrag = null;
        if (drag.CaptureTarget.IsMouseCaptured)
        {
            drag.CaptureTarget.ReleaseMouseCapture();
        }

        if (drag.Changed)
        {
            _controller.MarkDirty();
            RefreshNoteCanvasSummary();
            RefreshNoteStatusBar();
        }

        if (e != null)
        {
            e.Handled = true;
        }
    }

    private void DeleteNoteCanvasElement(NoteCanvasElement element)
    {
        if (_paper.NoteCanvasElements.Remove(element))
        {
            if (ReferenceEquals(_activeNoteCanvasElement, element))
            {
                _activeNoteCanvasElement = null;
            }

            _controller.MarkDirty();
            RefreshNoteCanvasLayer();
        }
    }

    private void DuplicateNoteCanvasElement(NoteCanvasElement element)
    {
        var maxZ = _paper.NoteCanvasElements.Count == 0 ? 0 : _paper.NoteCanvasElements.Max(e => e.ZIndex);
        var copy = new NoteCanvasElement
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = element.Type,
            Text = element.Text,
            X = element.X + 18,
            Y = element.Y + 18,
            Width = element.Width,
            Height = element.Height,
            ZIndex = maxZ + 10
        };
        _paper.NoteCanvasElements.Add(copy);
        _activeNoteCanvasElement = copy;
        _controller.MarkDirty();
        RefreshNoteCanvasLayer();
    }

    private void BringNoteCanvasElementToFront(NoteCanvasElement element)
    {
        var maxZ = _paper.NoteCanvasElements.Count == 0 ? 0 : _paper.NoteCanvasElements.Max(e => e.ZIndex);
        element.ZIndex = maxZ + 10;
        _activeNoteCanvasElement = element;
        _controller.MarkDirty();
        RefreshNoteCanvasLayer();
    }

    private void SendNoteCanvasElementToBack(NoteCanvasElement element)
    {
        var minZ = _paper.NoteCanvasElements.Count == 0 ? 0 : _paper.NoteCanvasElements.Min(e => e.ZIndex);
        element.ZIndex = minZ - 10;
        _activeNoteCanvasElement = element;
        _controller.MarkDirty();
        RefreshNoteCanvasLayer();
    }

    private void MoveNoteCanvasElementLayer(NoteCanvasElement element, int direction)
    {
        var ordered = OrderedNoteCanvasElements();
        var index = ordered.FindIndex(e => string.Equals(e.Id, element.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var nextIndex = Math.Clamp(index + direction, 0, ordered.Count - 1);
        if (nextIndex == index)
        {
            return;
        }

        (ordered[index].ZIndex, ordered[nextIndex].ZIndex) = (ordered[nextIndex].ZIndex, ordered[index].ZIndex);
        _activeNoteCanvasElement = element;
        _controller.MarkDirty();
        RefreshNoteCanvasLayer();
    }

    private void RefreshNoteCanvasSummary()
    {
        if (_noteCanvasElementCountText != null)
        {
            var count = _paper.NoteCanvasElements?.Count ?? 0;
            _noteCanvasElementCountText.Text = $"{count} \u5143\u7d20";
        }
    }


    public void UpdateTextZoom()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var zoom = CurrentTextZoom();
        if (_noteBox != null)
        {
            var expectedFontSize = Math.Round(NoteTypography.FontSize * zoom, 1);
            if (IsLoaded && Math.Abs(_noteBox.FontSize - expectedFontSize) > 0.001)
            {
                RebuildNoteBodyForMarkdownMode();
            }
            else
            {
                _noteBox.SetTextZoom(zoom);
                _noteBox.SetLineSpacing(_controller.State.NoteLineSpacing);
            }
        }

        if (_textZoomIndicator != null)
        {
            _textZoomIndicator.Text = $"{(int)Math.Round(zoom * 100)}%";
            _textZoomIndicator.Foreground = WeakTextBrush;
            _textZoomIndicator.Opacity = 0.55;
            if (_textZoomIndicator.Parent is UIElement host)
            {
                host.Visibility = Math.Abs(zoom - 1.0) < 0.001 ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        RefreshNoteStatusBar();
    }

    private double CurrentTextZoom()
    {
        return Math.Clamp(_paper.TextZoom, 0.5, 1.5);
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        var step = e.Delta > 0 ? 0.1 : -0.1;
        _controller.SetPaperTextZoom(_paper, _paper.TextZoom + step);
        e.Handled = true;
    }

    private void OpenMarkdownInDefaultEditor()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        try
        {
            var path = WriteExternalMarkdownFile();
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Format("OpenMarkdownFailureMessage", CurrentExternalMarkdownExtension(), ex.Message),
                Strings.Get("OpenMarkdownFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void UpdateExternalMarkdownExtension()
    {
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Content = ExternalOpenButtonLabel();
            _openMarkdownButton.ToolTip = OpenMarkdownEditorToolTip();
            _openMarkdownButton.Visibility = _controller.State.ShowTopBarExternalOpenButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }


    private string OpenMarkdownEditorToolTip()
    {
        return Strings.Format("ToolTipOpenMarkdownEditor", CurrentExternalMarkdownExtension());
    }

    private string ExternalOpenButtonLabel()
    {
        var extension = CurrentExternalMarkdownExtension().TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ExternalMarkdownFileExtensions.Default.TrimStart('.');
        }

        return extension.Length > 2
            ? extension[..2].ToUpperInvariant()
            : extension.ToUpperInvariant();
    }

    private string CurrentExternalMarkdownExtension()
    {
        return ExternalMarkdownFileExtensions.Normalize(_controller.State.ExternalMarkdownExtension);
    }

    private string WriteExternalMarkdownFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"paper-{_paper.Id}{CurrentExternalMarkdownExtension()}");
        var text = _noteBox?.Text ?? _paper.Content ?? "";
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private readonly record struct ScriptCapsuleSpec(string Engine, string Script, bool UsePersistentProcess);
    private readonly record struct ScriptCapsuleMarkerSpec(string Engine, bool UsePersistentProcess);

    private string CapsuleIconText()
    {
        if (IsScriptCapsule())
        {
            return "⚡";
        }

        return _paper.Type == PaperTypes.Note ? "✎" : "✓";
    }

    private double CapsuleIconFontSizeForCurrentPaper()
    {
        return IsScriptCapsule() ? CapsuleIconFontSize + 2 : CapsuleIconFontSize;
    }

    private bool IsScriptCapsule()
    {
        return TryGetScriptCapsule(out _);
    }

    internal static bool IsScriptCapsuleContent(string? text)
    {
        return IsScriptCapsuleText(text ?? "");
    }

    private void ActivateFromCollapsedCapsule()
    {
        if (TryRunScriptCapsule())
        {
            return;
        }

        if (_paper.IsPinnedToDesktop)
        {
            _controller.SetPaperPinnedToDesktop(_paper, false);
            return;
        }

        SetCollapsedState(false);
    }

    public void UpdateNoteLineSpacing()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        _noteBox?.SetLineSpacing(_controller.State.NoteLineSpacing);
    }

    private void OpenCapsuleForEditing()
    {
        if (_paper.IsCollapsed)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                ShowMainWindowForDeepCapsuleActivation();
                SetCollapsedState(false, alignExpandedToDockedEdge: true);
            }
            else
            {
                SetCollapsedState(false);
            }

            return;
        }

        EnsureExpandedSurfaceGeometry(alignToDockedEdge: HasDeepCapsuleSlotPlacement);
        _controller.BringPaperToFront(_paper);
    }

    internal bool TryRunScriptCapsule()
    {
        if (!TryGetScriptCapsule(out var spec))
        {
            return false;
        }

        _ = RunScriptCapsuleAsync(spec);
        return true;
    }

    private bool TryGetScriptCapsule(out ScriptCapsuleSpec spec)
    {
        spec = default;
        if (_paper.Type != PaperTypes.Note)
        {
            return false;
        }

        var text = _noteBox?.Text ?? _paper.Content ?? "";
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        if (!TryParseScriptCapsuleMarker(firstLine, out var markerSpec))
        {
            return false;
        }

        var scriptStart = firstLineEnd < 0 ? text.Length : firstLineEnd;
        if (scriptStart < text.Length && text[scriptStart] == '\r')
        {
            scriptStart++;
        }
        if (scriptStart < text.Length && text[scriptStart] == '\n')
        {
            scriptStart++;
        }

        spec = new ScriptCapsuleSpec(
            markerSpec.Engine,
            NormalizeScriptCapsuleIndent(text[scriptStart..]),
            markerSpec.UsePersistentProcess);
        return true;
    }

    private static bool IsScriptCapsuleText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        return TryParseScriptCapsuleMarker(firstLine, out _);
    }

    private static bool TryParseScriptCapsuleMarker(string firstLine, out ScriptCapsuleMarkerSpec spec)
    {
        spec = default;
        var marker = firstLine.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        spec = marker switch
        {
            "!pf" or "!powerf" => new ScriptCapsuleMarkerSpec("auto", true),
            "!p" or "!power" => new ScriptCapsuleMarkerSpec("auto", false),
            "!pwsh" or "!ps7" => new ScriptCapsuleMarkerSpec("pwsh", false),
            "!ps5" or "!winps" => new ScriptCapsuleMarkerSpec("powershell", false),
            _ => default
        };
        return !string.IsNullOrEmpty(spec.Engine);
    }

    private async Task RunScriptCapsuleAsync(ScriptCapsuleSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Script))
        {
            ShowScriptCapsuleFailure(Strings.Get("ScriptCapsuleEmptyMessage"));
            return;
        }

        if (spec.UsePersistentProcess && _controller.State.UsePersistentPowerShellProcess)
        {
            RunPersistentScriptCapsule(spec);
            return;
        }

        string? path = null;
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(spec.Engine);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = _controller.State.HideScriptRunWindow,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodedPowerShellLaunchCommand(path));

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                ShowScriptCapsuleFailure(Strings.Get("ScriptCapsuleStartFailureMessage"));
                return;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var detail = CompactScriptCapsuleOutput(output, error);
                ShowScriptCapsuleFailure(Strings.Format("ScriptCapsuleExitFailureMessage", process.ExitCode, detail));
            }
        }
        catch (Exception ex)
        {
            ShowScriptCapsuleFailure(ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Temporary script cleanup must not affect the user's note.
                }
            }
        }
    }

    private void RunPersistentScriptCapsule(ScriptCapsuleSpec spec)
    {
        string? path = null;
        var submitted = false;
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(spec.Engine);
            var process = EnsurePersistentScriptProcess(executable, _controller.State.HideScriptRunWindow);
            var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
            process.StandardInput.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine($"try {{ & '{escapedPath}' }} finally {{ Remove-Item -LiteralPath '{escapedPath}' -ErrorAction SilentlyContinue }}");
            process.StandardInput.Flush();
            submitted = true;
        }
        catch (Exception ex)
        {
            ShowScriptCapsuleFailure(ex.Message);
        }
        finally
        {
            if (!submitted && !string.IsNullOrWhiteSpace(path))
            {
                DeleteScriptCapsuleFile(path);
            }
        }
    }

    private static Process EnsurePersistentScriptProcess(string executable, bool hideWindow)
    {
        var key = $"{executable}|{hideWindow}";
        lock (PersistentScriptProcessLock)
        {
            if (PersistentScriptProcesses.TryGetValue(key, out var existing) && !existing.HasExited)
            {
                return existing;
            }

            if (existing != null)
            {
                existing.Dispose();
                PersistentScriptProcesses.Remove(key);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = hideWindow,
                RedirectStandardInput = true,
                StandardInputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-NoExit");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("-");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                lock (PersistentScriptProcessLock)
                {
                    if (PersistentScriptProcesses.TryGetValue(key, out var current) && ReferenceEquals(current, process))
                    {
                        PersistentScriptProcesses.Remove(key);
                    }
                }

                process.Dispose();
            };
            process.Start();
            PersistentScriptProcesses[key] = process;
            return process;
        }
    }

    private static string NormalizeScriptCapsuleIndent(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        var normalized = script.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var commonIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t')
            {
                indent++;
            }
            commonIndent = Math.Min(commonIndent, indent);
        }

        if (commonIndent is int.MaxValue or <= 0)
        {
            return script;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var remove = Math.Min(commonIndent, LeadingWhitespaceLength(lines[i]));
            lines[i] = lines[i][remove..];
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int LeadingWhitespaceLength(string text)
    {
        var length = 0;
        while (length < text.Length && text[length] is ' ' or '\t')
        {
            length++;
        }

        return length;
    }

    private string WriteScriptCapsuleFile(string script)
    {
        var directory = ScriptCapsuleTempDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"script-{_paper.Id}-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string ScriptCapsuleTempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "PaperTodo", "Scripts");
    }

    private static void DeleteScriptCapsuleFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary script cleanup must not affect the user's note.
        }
    }

    internal static void CleanupOldScriptCapsuleTempFiles()
    {
        try
        {
            var directory = ScriptCapsuleTempDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(directory, "script-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private string ResolvePowerShellExecutable(string engine)
    {
        return ResolvePowerShellExecutable(_controller.State, engine);
    }

    internal static void EnsurePersistentScriptProcessForSettings(AppState state)
    {
        if (!state.UsePersistentPowerShellProcess)
        {
            return;
        }

        try
        {
            var executable = ResolvePowerShellExecutable(state, "auto");
            EnsurePersistentScriptProcess(executable, state.HideScriptRunWindow);
        }
        catch
        {
            // Prewarming is best-effort; explicit script execution will report failures.
        }
    }

    private static string ResolvePowerShellExecutable(AppState state, string engine)
    {
        if (engine == "pwsh")
        {
            return FindPowerShellExecutable("pwsh.exe")
                ?? throw new InvalidOperationException(Strings.Get("ScriptCapsulePowerShell7NotFound"));
        }

        if (engine == "powershell")
        {
            return "powershell.exe";
        }

        if (state.PreferPowerShell7)
        {
            var pwsh = FindPowerShellExecutable("pwsh.exe");
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return pwsh;
            }
        }

        return "powershell.exe";
    }

    private static string? FindPowerShellExecutable(string fileName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")))
        {
            candidates.AddRange(
                (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => Path.Combine(path.Trim(), fileName)));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "PowerShell", "7", fileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EncodedPowerShellLaunchCommand(string path)
    {
        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var command = string.Join(
            "; ",
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
            "$OutputEncoding = [System.Text.Encoding]::UTF8",
            $"& '{escapedPath}'");
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string CompactScriptCapsuleOutput(string output, string error)
    {
        var text = string.Join(
            Environment.NewLine,
            new[] { error.Trim(), output.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text))
        {
            return Strings.Get("ScriptCapsuleNoOutput");
        }

        const int maxLength = 1800;
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private void ShowScriptCapsuleFailure(string message)
    {
        MessageBox.Show(
            message,
            Strings.Get("ScriptCapsuleFailureTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RefreshPaperContextMenus()
    {
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
        if (_deepCapsuleSlotLeftArea != null)
        {
            _deepCapsuleSlotLeftArea.ContextMenu = BuildDeepCapsuleSlotContextMenu();
        }
        if (_paperChrome != null)
        {
            _paperChrome.ContextMenu = BuildPaperContextMenu();
        }
    }

    internal static void StopPersistentScriptProcesses()
    {
        lock (PersistentScriptProcessLock)
        {
            foreach (var process in PersistentScriptProcesses.Values.ToList())
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            if (process.StartInfo.RedirectStandardInput)
                            {
                                process.StandardInput.Close();
                            }
                        }
                        catch
                        {
                            // The process may already be exiting or the pipe may be broken.
                        }

                        if (!process.WaitForExit(250))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(1000);
                        }
                    }
                }
                catch
                {
                    // Persistent script sessions are optional and disposable.
                }
                finally
                {
                    process.Dispose();
                }
            }

            PersistentScriptProcesses.Clear();
        }
    }

}
