using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private UIElement BuildTodoBody()
    {
        if (_paper.Items.Count == 0)
        {
            _paper.Items.Add(new PaperItem { Order = 0 });
        }

        _todoPanel = new StackPanel
        {
            Margin = new Thickness(6.4, 3.2, 5.6, 3.2)
        };

        RebuildTodoRows();

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _todoPanel,
            FocusVisualStyle = null,
            CanContentScroll = false
        };

        scrollViewer.RequestBringIntoView += (_, e) =>
        {
            if (_allowNextBringIntoView)
            {
                _allowNextBringIntoView = false;
                return;
            }

            // Prevent WPF's automatic focus-tracking from snapping scroll position back to the focused top item!
            e.Handled = true;
        };

        scrollViewer.PreviewMouseWheel += (_, e) =>
        {
            if (scrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            var offset = scrollViewer.VerticalOffset - (e.Delta * 0.45);
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, scrollViewer.ScrollableHeight));
            e.Handled = true;
        };

        _todoScrollViewer = scrollViewer;
        return scrollViewer;
    }


    public void RefreshTodoRowsForExternalChange()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        RebuildTodoRows(CurrentFocusedTodoItemId());
    }

    public void UpdateTodoVisualSize()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        RebuildTodoRows(CurrentFocusedTodoItemId());
    }

    private void RebuildTodoRows(string? focusItemId = null, TodoFocusPlacement focusPlacement = TodoFocusPlacement.End)
    {
        if (_todoPanel == null)
        {
            return;
        }

        _todoRowsGeneration++;
        var targetFocus = focusItemId ?? _pendingFocusItemId;
        _pendingFocusItemId = null;

        NormalizeTodoItems();
        NormalizeOrders();

        // 记录现有行的ID，用于判断哪些是新增的
        var existingIds = new HashSet<string>(_todoRows.Select(r => (string)r.Tag));

        _todoPanel.Children.Clear();
        _todoEditors.Clear();
        _todoRows.Clear();
        _linkedNoteDropRow = null;

        foreach (var item in OrderedItems())
        {
            var isNew = !existingIds.Contains(item.Id);
            var row = BuildTodoRow(item, isNewItem: isNew);
            _todoPanel.Children.Add(row);

            if (!isNew && _controller.State.EnableAnimations && item.Id == _recentlyMovedItemId)
            {
                var isDown = item.Done;
                row.Opacity = isDown ? 0.35 : 0.6;
                AnimationHelper.GetTranslateTransform(row).Y = isDown ? -12 : 12;
                Dispatcher.InvokeAsync(() =>
                {
                    if (isDown)
                    {
                        var fade = new System.Windows.Media.Animation.DoubleAnimation(row.Opacity, 0.75, TimeSpan.FromMilliseconds(200))
                        {
                            EasingFunction = AnimationHelper.QuickEase
                        };
                        row.BeginAnimation(OpacityProperty, fade);
                    }
                    else
                    {
                        AnimationHelper.FadeIn(row, 200);
                    }
                    AnimationHelper.TranslateTo(row, 0, 0, 220, AnimationHelper.QuickEase);
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
        }
        _recentlyMovedItemId = null;

        _todoPanel.Children.Add(BuildTodoAppendArea());
        _renderedTodoStructure = string.Join("|", _paper.Items.Select(i => $"{i.Id}:{i.TodoColumnCount}"));

        if (!string.IsNullOrWhiteSpace(targetFocus))
        {
            FocusTodoItem(targetFocus, focusPlacement);
        }
    }

    public static int TodoItemColumnCount(PaperItem item)
        => Math.Clamp(item.TodoColumnCount <= 0 ? 1 : item.TodoColumnCount, 1, 4);

    public static void NormalizeTodoItemColumns(PaperItem item)
    {
        item.TodoColumnCount = TodoItemColumnCount(item);
        item.TodoExtraColumns ??= new List<string>();
        item.TodoColumnWidths ??= new List<double>();
        while (item.TodoExtraColumns.Count < item.TodoColumnCount - 1)
        {
            item.TodoExtraColumns.Add("");
        }
        if (item.TodoExtraColumns.Count > item.TodoColumnCount - 1)
        {
            item.TodoExtraColumns.RemoveRange(item.TodoColumnCount - 1, item.TodoExtraColumns.Count - (item.TodoColumnCount - 1));
        }
        while (item.TodoColumnWidths.Count < item.TodoColumnCount)
        {
            item.TodoColumnWidths.Add(1);
        }
        if (item.TodoColumnWidths.Count > item.TodoColumnCount)
        {
            item.TodoColumnWidths.RemoveRange(item.TodoColumnCount, item.TodoColumnWidths.Count - item.TodoColumnCount);
        }
        for (var i = 0; i < item.TodoColumnWidths.Count; i++)
        {
            var width = item.TodoColumnWidths[i];
            item.TodoColumnWidths[i] = double.IsNaN(width) || double.IsInfinity(width) || width <= 0
                ? 1
                : Math.Clamp(Math.Round(width, 3), 0.2, 8);
        }
    }

    public void SetTodoItemColumnCount(PaperItem item, int columnCount)
    {
        var normalized = Math.Clamp(columnCount, 1, 4);
        if (TodoItemColumnCount(item) == normalized)
        {
            return;
        }

        PushUndoSnapshot();
        item.TodoColumnCount = normalized;
        NormalizeTodoItemColumns(item);

        _controller.MarkDirty();
        RebuildTodoRows(item.Id);
    }

    public void InsertTodoColumnBefore(PaperItem item, int columnIndex)
    {
        NormalizeTodoItemColumns(item);
        var count = TodoItemColumnCount(item);
        if (count >= 4)
        {
            return;
        }

        PushUndoSnapshot();
        var insertIndex = Math.Clamp(columnIndex, 0, count);
        if (insertIndex == 0)
        {
            item.TodoExtraColumns.Insert(0, item.Text);
            item.Text = "";
        }
        else
        {
            item.TodoExtraColumns.Insert(insertIndex - 1, "");
        }

        item.TodoColumnCount = count + 1;
        item.TodoColumnWidths.Insert(insertIndex, 1);
        NormalizeTodoItemColumns(item);
        _controller.MarkDirty();
        RebuildTodoRows(item.Id);
    }

    public void DeleteTodoColumn(PaperItem item, int columnIndex)
    {
        NormalizeTodoItemColumns(item);
        var count = TodoItemColumnCount(item);
        if (count <= 1)
        {
            return;
        }

        PushUndoSnapshot();
        var deleteIndex = Math.Clamp(columnIndex, 0, count - 1);
        if (deleteIndex == 0)
        {
            item.Text = item.TodoExtraColumns.Count > 0 ? item.TodoExtraColumns[0] : "";
            if (item.TodoExtraColumns.Count > 0)
            {
                item.TodoExtraColumns.RemoveAt(0);
            }
        }
        else if (deleteIndex - 1 < item.TodoExtraColumns.Count)
        {
            item.TodoExtraColumns.RemoveAt(deleteIndex - 1);
        }

        if (deleteIndex < item.TodoColumnWidths.Count)
        {
            item.TodoColumnWidths.RemoveAt(deleteIndex);
        }
        item.TodoColumnCount = count - 1;
        NormalizeTodoItemColumns(item);
        _controller.MarkDirty();
        RebuildTodoRows(item.Id);
    }

    private void FocusTodoItem(string? itemId, TodoFocusPlacement placement = TodoFocusPlacement.End)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_todoEditors.TryGetValue(itemId, out var box))
            {
                box.Focus();
                box.CaretIndex = placement == TodoFocusPlacement.Start ? 0 : box.Text.Length;
                _allowNextBringIntoView = true;
                box.BringIntoView();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private UIElement BuildTodoAppendArea()
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var area = new Border
        {
            Margin = new Thickness(0, 6, 0, 2),
            Padding = new Thickness(0, Math.Max(3, metrics.TextVerticalPadding + 1), 0, Math.Max(3, metrics.TextVerticalPadding + 1)),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = AppendBorderBrush,
            Background = AppendBgBrush,
            MinHeight = metrics.AppendMinHeight,
            Cursor = Cursors.IBeam,
            AllowDrop = true,
            ToolTip = Strings.Get("AppendAreaToolTip")
        };

        _appendArea = area;

        var plus = new TextBlock
        {
            Text = "＋",
            Foreground = WeakTextBrush,
            Opacity = 0.42,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = metrics.AppendGlyphFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        area.Child = plus;

        area.MouseEnter += (_, _) =>
        {
            area.Background = AppendHoverBgBrush;
            plus.Opacity = 0.7;
        };

        area.MouseLeave += (_, _) =>
        {
            ResetAppendAreaDropState();
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            var lastUncompleted = OrderedItems().LastOrDefault(i => !i.Done);
            var newItem = AddItemAfter(lastUncompleted, "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
        };

        return area;
    }

    private sealed record TodoRowMetadataBinding(
        PaperItem Item,
        WrapPanel MetadataPanel,
        FrameworkElement Check,
        FrameworkElement Handle,
        Border? DueBadge,
        Border? RelativeBadge,
        TextBlock? DueLabel,
        Border? LinkButton,
        DateTime DueAt,
        bool HasDue,
        bool HasLinkedNote);

    internal void UpdateTodoSizeResponsiveLayout(double width, double height)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var isShort = height < 230;
        var isNarrow = width < 270;

        if (_appendArea != null)
        {
            var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
            _appendArea.MinHeight = isShort ? 22 : metrics.AppendMinHeight;
            _appendArea.Margin = isShort ? new Thickness(0, 3, 0, 1) : new Thickness(0, 6, 0, 2);
        }

        if (_todoPanel != null)
        {
            _todoPanel.Margin = isNarrow ? new Thickness(3.5, 2, 3.5, 2) : new Thickness(6.4, 3.2, 5.6, 3.2);
        }

        var isStacked = width < 360;
        var now = DateTime.Now;
        var yearMode = _controller.State.TodoDueYearDisplayMode;
        var showRelative = _controller.State.ShowTodoDueRelativeTime;
        var metricsForRows = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);

        foreach (var row in _todoRows)
        {
            if (row.Child is not Grid grid) continue;

            if (grid.Tag is TodoRowMetadataBinding binding)
            {
                var panel = binding.MetadataPanel;
                if (isStacked)
                {
                    Grid.SetRow(panel, 1);
                    Grid.SetColumn(panel, 1);
                    panel.Margin = new Thickness(0, 2, 0, 2);
                    panel.HorizontalAlignment = HorizontalAlignment.Left;

                    Grid.SetRowSpan(binding.Check, 2);
                    binding.Check.VerticalAlignment = VerticalAlignment.Top;
                    binding.Check.Margin = new Thickness(0, Math.Max(2, (metricsForRows.RowMinHeight - 18) / 2), 0, 0);

                    Grid.SetRowSpan(binding.Handle, 2);
                    binding.Handle.VerticalAlignment = VerticalAlignment.Top;
                    binding.Handle.Margin = new Thickness(0, Math.Max(2, (metricsForRows.RowMinHeight - 18) / 2), 0, 0);
                }
                else
                {
                    Grid.SetRow(panel, 0);
                    Grid.SetColumn(panel, 2);
                    panel.Margin = new Thickness(4, 0, 4, 0);
                    panel.HorizontalAlignment = HorizontalAlignment.Right;

                    Grid.SetRowSpan(binding.Check, 1);
                    binding.Check.VerticalAlignment = VerticalAlignment.Center;
                    binding.Check.Margin = new Thickness(0);

                    Grid.SetRowSpan(binding.Handle, 1);
                    binding.Handle.VerticalAlignment = VerticalAlignment.Stretch;
                    binding.Handle.Margin = new Thickness(0);
                }

                if (binding.HasDue && binding.DueLabel != null)
                {
                    binding.DueLabel.Text = FormatTodoDueBadgeLabel(binding.DueAt, now, yearMode, width, isStacked);
                }

                if (binding.RelativeBadge != null)
                {
                    binding.RelativeBadge.Visibility = showRelative ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }

    private void ShowAppendAreaAsTrashBin(bool active, bool hovered = false)
    {
        if (_appendArea == null)
        {
            return;
        }

        if (active)
        {
            if (hovered)
            {
                _appendArea.Background = TrashHoverBgBrush;
                _appendArea.BorderBrush = TrashHoverBorderBrush;
                _appendArea.BorderThickness = new Thickness(1.5);
            }
            else
            {
                _appendArea.Background = TrashBgBrush;
                _appendArea.BorderBrush = TrashBorderBrush;
                _appendArea.BorderThickness = new Thickness(1);
            }

            if (_appendArea.Child is TextBlock text)
            {
                var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
                text.Text = "🗑";
                text.Foreground = TrashTextBrush;
                text.Opacity = hovered ? 1.0 : 0.65;
                text.FontSize = metrics.TrashGlyphFontSize;
            }
        }
        else
        {
            _appendArea.Background = AppendBgBrush;
            _appendArea.BorderBrush = AppendBorderBrush;
            _appendArea.BorderThickness = new Thickness(1);

            if (_appendArea.Child is TextBlock text)
            {
                var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
                text.Text = "＋";
                text.Foreground = WeakTextBrush;
                text.Opacity = 0.42;
                text.FontSize = metrics.AppendGlyphFontSize;
            }
        }
    }

    private void ResetAppendAreaDropState()
    {
        ShowAppendAreaAsTrashBin(active: false);
    }

    private static string CompactLinkedNoteTitle(string title, int fullTextElementLimit, int truncatedTextElementCount)
    {
        var text = title.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        int[] textElements;
        try
        {
            textElements = StringInfo.ParseCombiningCharacters(text);
        }
        catch
        {
            if (text.Length <= fullTextElementLimit)
            {
                return text;
            }

            return text[..Math.Min(Math.Max(1, truncatedTextElementCount), text.Length)] + "…";
        }

        if (textElements.Length <= fullTextElementLimit)
        {
            return text;
        }

        var keep = Math.Max(1, truncatedTextElementCount);
        var end = textElements.Length > keep ? textElements[keep] : Math.Min(keep, text.Length);
        return text[..end] + "…";
    }

    private static string CompactLinkedNoteTitleByDisplayWidth(string title, int fullDisplayWidthLimit, int truncatedDisplayWidth)
    {
        var text = title.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        if (DeepCapsuleLayout.DisplayWidth(text) <= fullDisplayWidthLimit)
        {
            return text;
        }

        var keepWidth = Math.Max(1, truncatedDisplayWidth);
        var indexes = StringInfo.ParseCombiningCharacters(text);
        var width = 0;
        var end = 0;
        foreach (var index in indexes)
        {
            var nextIndex = NextTextElementIndex(indexes, index, text.Length);
            var element = text[index..nextIndex];
            var elementWidth = Math.Max(1, DeepCapsuleLayout.DisplayWidth(element));
            if (width > 0 && width + elementWidth > keepWidth)
            {
                break;
            }

            width += elementWidth;
            end = nextIndex;
        }

        if (end <= 0)
        {
            end = indexes.Length > 0 ? NextTextElementIndex(indexes, indexes[0], text.Length) : Math.Min(1, text.Length);
        }

        return text[..end] + "…";
    }

    private static int NextTextElementIndex(int[] indexes, int currentIndex, int fallbackLength)
    {
        for (var i = 0; i < indexes.Length; i++)
        {
            if (indexes[i] == currentIndex)
            {
                return i + 1 < indexes.Length ? indexes[i + 1] : fallbackLength;
            }
        }

        return fallbackLength;
    }

    private UIElement BuildTodoRow(PaperItem item, bool isNewItem = false)
    {
        NormalizeTodoItemColumns(item);
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var linkedNoteTitle = "";
        var hasLinkedNote = _controller.State.EnableTodoNoteLinks &&
            _controller.TryGetLinkedNoteTitle(item.LinkedNoteId, out linkedNoteTitle);
        var runLinkedScriptOnClick = hasLinkedNote &&
            _controller.ShouldRunLinkedScriptCapsule(item.LinkedNoteId);

        var row = new Border
        {
            Margin = new Thickness(0, 2, 0, 2),
            Padding = AppUi.TodoRowPadding,
            CornerRadius = new CornerRadius(RadiusControl),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            AllowDrop = true,
            Tag = item.Id,
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(1, 1),
                    new TranslateTransform(0, 0)
                }
            },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        row.MouseEnter += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = HoverBrush;
            }
        };

        row.MouseLeave += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = Brushes.Transparent;
            }
        };

        var grid = new Grid
        {
            MinHeight = metrics.RowMinHeight,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(metrics.CheckColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(18, metrics.CheckColumnWidth - 4)) });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = SharedCheckBoxStyle
        };

        var text = new TodoTextBox
        {
            Text = item.Text,
            IsDone = item.Done,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = item.Done ? BrightWeakTextBrush : TextBrush,
            CaretBrush = TextBrush,
            FontSize = metrics.TextFontSize,
            Padding = new Thickness(2, metrics.TextVerticalPadding, 2, metrics.TextVerticalPadding),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MaxLength = 5000
        };
        ApplyTodoTextLineSpacing(text, metrics);

        _todoEditors[item.Id] = text;

        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text;
            item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _paper.UpdatedAt = item.UpdatedAt;
            _controller.MarkDirty();
        };

        text.PreviewKeyDown += (_, e) => HandleTodoKeyDown(e, item, text);
        DataObject.AddPastingHandler(text, (sender, e) => HandleTodoPaste(e, item, text));

        text.GotFocus += (_, _) =>
        {
            _activeOriginalItemId = item.Id;
            _activeOriginalText = text.Text;
        };

        text.LostFocus += (_, _) =>
        {
            if (_activeOriginalItemId == item.Id && _activeOriginalText != null && text.Text != _activeOriginalText)
            {
                var oldText = item.Text;
                item.Text = _activeOriginalText;

                _undoStack.Add(CloneItems(_paper.Items));
                if (_undoStack.Count > MaxUndoDepth)
                {
                    _undoStack.RemoveAt(0);
                }
                _redoStack.Clear();

                item.Text = oldText;
                _activeOriginalText = oldText;
            }
        };

        check.Checked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = true;
            item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _paper.UpdatedAt = item.UpdatedAt;
            ApplyTodoRowDoneVisualState(row, done: true);
            _controller.MarkDirty();

            // 完成动画：只淡化，不缩小
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.75, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }

            QueueAutoReorderCompletedItems(item.Id, isChecking: true);
        };

        check.Unchecked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = false;
            item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _paper.UpdatedAt = item.UpdatedAt;
            ApplyTodoRowDoneVisualState(row, done: false);
            _controller.MarkDirty();

            // 取消完成动画
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(row.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }

            QueueAutoReorderCompletedItems(item.Id, isChecking: false);
        };

        ContextMenu CreateItemMenu(int columnIndex)
        {
            var itemMenu = CreateContextMenu();
            itemMenu.Items.Add(MenuHeader(Strings.Get("MenuTodoItem")));
            if (hasLinkedNote)
            {
                var openMenuText = runLinkedScriptOnClick
                    ? Strings.Format("MenuEditLinkedScriptCapsule", linkedNoteTitle)
                    : Strings.Format("MenuOpenLinkedNote", linkedNoteTitle);
                itemMenu.Items.Add(MenuItem(openMenuText, (_, _) => _controller.OpenLinkedNote(item.LinkedNoteId, this)));
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuUnlinkNote"), (_, _) => UnlinkNoteFromTodoItem(item)));
                itemMenu.Items.Add(MenuSeparator());
            }
            itemMenu.Items.Add(MenuItem(
                string.IsNullOrWhiteSpace(item.DueAtLocal) ? Strings.Get("MenuSetTodoDue") : Strings.Get("MenuChangeTodoDue"),
                (_, _) => ShowTodoDueDialog(item)));
            if (!string.IsNullOrWhiteSpace(item.DueAtLocal))
            {
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearTodoDue"), (_, _) => ClearTodoDue(item)));
            }
            itemMenu.Items.Add(MenuItem(
                item.ReminderIntervalValue is int ? Strings.Get("MenuChangeTodoReminderInterval") : Strings.Get("MenuSetTodoReminderInterval"),
                (_, _) => ShowTodoReminderIntervalDialog(item)));
            if (item.ReminderIntervalValue is int)
            {
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearTodoReminderInterval"), (_, _) => ClearTodoReminderInterval(item)));
            }
            itemMenu.Items.Add(MenuSeparator());
            var insertColumnBefore = MenuItem(Strings.Get("MenuInsertTodoColumnBefore"), (_, _) => InsertTodoColumnBefore(item, columnIndex));
            insertColumnBefore.IsEnabled = TodoItemColumnCount(item) < 4;
            itemMenu.Items.Add(insertColumnBefore);
            var deleteCurrentColumn = MenuItem(Strings.Get("MenuDeleteTodoColumn"), (_, _) => DeleteTodoColumn(item, columnIndex));
            deleteCurrentColumn.IsEnabled = TodoItemColumnCount(item) > 1;
            itemMenu.Items.Add(deleteCurrentColumn);
            var increaseColumns = MenuItem(Strings.Get("MenuIncreaseTodoColumns"), (_, _) => SetTodoItemColumnCount(item, TodoItemColumnCount(item) + 1));
            increaseColumns.IsEnabled = TodoItemColumnCount(item) < 4;
            itemMenu.Items.Add(increaseColumns);
            var decreaseColumns = MenuItem(Strings.Get("MenuDecreaseTodoColumns"), (_, _) => SetTodoItemColumnCount(item, TodoItemColumnCount(item) - 1));
            decreaseColumns.IsEnabled = TodoItemColumnCount(item) > 1;
            itemMenu.Items.Add(decreaseColumns);
            itemMenu.Items.Add(MenuSeparator());
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuDeleteItem"), (_, _) => RemoveItem(item)));
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));

            itemMenu.Opened += (_, _) => row.Background = HoverBrush;
            itemMenu.Closed += (_, _) =>
            {
                if (!row.IsMouseOver)
                {
                    row.Background = Brushes.Transparent;
                }
            };

            return itemMenu;
        }

        void AttachItemContextMenu(FrameworkElement element, int columnIndex = 0)
        {
            element.ContextMenu = CreateItemMenu(columnIndex);
            element.PreviewMouseRightButtonDown += (_, _) => text.Focus();
        }

        AttachItemContextMenu(row, 0);
        AttachItemContextMenu(check, 0);
        AttachItemContextMenu(text, 0);

        var textHost = BuildTodoTextColumnHost(item, text, metrics, AttachItemContextMenu);

        var hasDue = TryGetTodoDueAt(item, out var dueAt);
        Border? dueBadge = null;
        Border? relativeBadge = null;
        TextBlock? dueLabel = null;
        var curWidth = ActualWidth > 0 ? ActualWidth : Width;
        var hasMetadata = hasDue || hasLinkedNote;
        var isStacked = hasMetadata && curWidth < 360;

        if (hasDue)
        {
            relativeBadge = BuildTodoDueRelativeBadge(dueAt, metrics);
            AttachItemContextMenu(relativeBadge);
            relativeBadge.Visibility = _controller.State.ShowTodoDueRelativeTime
                ? Visibility.Visible
                : Visibility.Collapsed;

            var (badge, label) = BuildTodoDueBadge(item, dueAt, metrics, curWidth, isStacked);
            dueBadge = badge;
            dueLabel = label;
            AttachItemContextMenu(dueBadge);
        }

        Border? linkButton = null;
        if (hasLinkedNote)
        {
            var showLinkedNoteName = _controller.State.ShowLinkedNoteName;
            var allowLongLinkedNoteTitle = showLinkedNoteName && _controller.State.AllowLongLinkedNoteTitles;

            string LinkedNoteButtonLabel(bool isTodoMultiline)
            {
                var title = allowLongLinkedNoteTitle
                    ? CompactLinkedNoteTitleByDisplayWidth(
                        linkedNoteTitle,
                        isTodoMultiline ? 20 : 10,
                        isTodoMultiline ? 20 : 10)
                    : isTodoMultiline
                        ? CompactLinkedNoteTitle(linkedNoteTitle, 6, 5)
                        : CompactLinkedNoteTitle(linkedNoteTitle, 3, 3);
                return runLinkedScriptOnClick ? "⚡ " + title : title;
            }

            double LegacyLinkedNoteButtonWidth(bool isTodoMultiline)
            {
                return isTodoMultiline
                    ? Math.Max(runLinkedScriptOnClick ? 52 : 44, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.35 : 2))
                    : Math.Max(runLinkedScriptOnClick ? 58 : 50, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.55 : 2.2));
            }

            double LegacyLinkedNoteTextMaxWidth(bool isTodoMultiline)
            {
                return metrics.CheckColumnWidth * (isTodoMultiline
                    ? runLinkedScriptOnClick ? 2.15 : 1.8
                    : runLinkedScriptOnClick ? 2.35 : 2);
            }

            double LinkedNoteButtonWidth(bool isTodoMultiline, string label)
            {
                if (!showLinkedNoteName)
                {
                    return Math.Max(23, metrics.CheckColumnWidth);
                }

                var legacyWidth = LegacyLinkedNoteButtonWidth(isTodoMultiline);
                if (!allowLongLinkedNoteTitle)
                {
                    return legacyWidth;
                }

                var measuredWidth = MeasureCapsuleTextWidth(label, metrics.LinkedNoteNameFontSize, FontWeights.SemiBold, AppTypography.UiFontFamily) + 10;
                var rawWidth = Math.Max(legacyWidth, Math.Ceiling(measuredWidth));
                var maxAllowed = Math.Max(30.0, (curWidth - 120.0) * 0.42);
                return Math.Min(rawWidth, maxAllowed);
            }

            double LinkedNoteTextMaxWidth(bool isTodoMultiline, double buttonWidth)
            {
                if (allowLongLinkedNoteTitle)
                {
                    return Math.Max(1, buttonWidth - 6);
                }

                return LegacyLinkedNoteTextMaxWidth(isTodoMultiline);
            }

            var linkedNoteButtonText = showLinkedNoteName
                ? LinkedNoteButtonLabel(isTodoMultiline: false)
                : runLinkedScriptOnClick ? "⚡" : "\uE71B";
            var initialLinkedNoteButtonWidth = showLinkedNoteName
                ? LinkedNoteButtonWidth(isTodoMultiline: false, linkedNoteButtonText)
                : Math.Max(23, metrics.CheckColumnWidth);
            var linkGlyph = new TextBlock
            {
                Text = linkedNoteButtonText,
                Foreground = WeakTextBrush,
                Opacity = 0.72,
                FontFamily = showLinkedNoteName
                    ? AppTypography.UiFontFamily
                    : runLinkedScriptOnClick ? new FontFamily("Segoe UI Symbol") : new FontFamily("Segoe MDL2 Assets"),
                FontSize = showLinkedNoteName
                    ? metrics.LinkedNoteNameFontSize
                    : runLinkedScriptOnClick ? metrics.LinkedNoteIconFontSize + 1 : metrics.LinkedNoteIconFontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = showLinkedNoteName ? metrics.LinkedNoteNameFontSize + 1 : double.NaN,
                MaxWidth = showLinkedNoteName ? LinkedNoteTextMaxWidth(isTodoMultiline: false, initialLinkedNoteButtonWidth) : double.PositiveInfinity
            };

            var builtLinkButton = new Border
            {
                Width = showLinkedNoteName
                    ? initialLinkedNoteButtonWidth
                    : Math.Max(23, metrics.CheckColumnWidth),
                MinWidth = Math.Max(23, metrics.CheckColumnWidth),
                MinHeight = Math.Max(20, metrics.RowMinHeight - 4),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = showLinkedNoteName ? new Thickness(5, 1, 5, 1) : new Thickness(0),
                CornerRadius = new CornerRadius(RadiusControl),
                Background = LinkedNoteBgBrush,
                Cursor = Cursors.Hand,
                ToolTip = runLinkedScriptOnClick
                    ? Strings.Format("ToolTipRunLinkedScriptCapsule", linkedNoteTitle)
                    : Strings.Format("ToolTipOpenLinkedNote", linkedNoteTitle),
                Child = linkGlyph
            };
            linkButton = builtLinkButton;

            void UpdateLinkedNoteNameLayout()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                var isTodoMultiline = text.LineCount > 1;
                linkGlyph.Text = LinkedNoteButtonLabel(isTodoMultiline);
                linkGlyph.TextWrapping = TextWrapping.NoWrap;
                var buttonWidth = LinkedNoteButtonWidth(isTodoMultiline, linkGlyph.Text);
                linkGlyph.MaxWidth = LinkedNoteTextMaxWidth(isTodoMultiline, buttonWidth);
                builtLinkButton.Width = buttonWidth;
            }

            void QueueLinkedNoteNameLayoutUpdate()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                Dispatcher.BeginInvoke((Action)UpdateLinkedNoteNameLayout, System.Windows.Threading.DispatcherPriority.Render);
            }

            if (showLinkedNoteName)
            {
                text.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                row.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                text.TextChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                QueueLinkedNoteNameLayoutUpdate();
            }

            builtLinkButton.MouseEnter += (_, _) =>
            {
                builtLinkButton.Background = LinkedNoteHoverBgBrush;
                linkGlyph.Foreground = TextBrush;
                linkGlyph.Opacity = 1.0;
            };
            builtLinkButton.MouseLeave += (_, _) =>
            {
                builtLinkButton.Background = LinkedNoteBgBrush;
                linkGlyph.Foreground = WeakTextBrush;
                linkGlyph.Opacity = 0.7;
                builtLinkButton.Opacity = 1.0;
            };
            builtLinkButton.MouseLeftButtonDown += (_, e) =>
            {
                builtLinkButton.Opacity = 0.72;
                e.Handled = true;
            };
            builtLinkButton.MouseLeftButtonUp += (_, e) =>
            {
                builtLinkButton.Opacity = 1.0;
                if (!_controller.ShouldRunLinkedScriptCapsule(item.LinkedNoteId) ||
                    !_controller.RunLinkedScriptCapsule(item.LinkedNoteId))
                {
                    _controller.OpenLinkedNote(item.LinkedNoteId, this);
                }
                e.Handled = true;
            };
            AttachItemContextMenu(builtLinkButton);
        }

        var handleGlyph = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.48,
            FontSize = Math.Max(11, metrics.TextFontSize - 1),
            FontFamily = AppTypography.SymbolFontFamily,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var handle = new Border
        {
            Width = Math.Max(14, metrics.CheckColumnWidth - 8),
            MinHeight = metrics.RowMinHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = handleGlyph,
            ToolTip = Strings.Get("DragSortToolTip")
        };

        handle.MouseEnter += (_, _) => handleGlyph.Opacity = 0.78;
        handle.MouseLeave += (_, _) =>
        {
            if (_todoDrag?.ItemId != item.Id)
            {
                handleGlyph.Opacity = 0.48;
            }
        };

        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _todoDrag = new TodoDragState(item.Id, row, handle, e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        };
        AttachItemContextMenu(handle);

        Grid.SetColumn(check, 0);
        Grid.SetRow(check, 0);
        Grid.SetRowSpan(check, isStacked ? 2 : 1);
        check.VerticalAlignment = isStacked ? VerticalAlignment.Top : VerticalAlignment.Center;
        check.Margin = isStacked ? new Thickness(0, Math.Max(2, (metrics.RowMinHeight - 18) / 2), 0, 0) : new Thickness(0);
        grid.Children.Add(check);

        Grid.SetColumn(textHost, 1);
        Grid.SetRow(textHost, 0);
        grid.Children.Add(textHost);

        Grid.SetColumn(handle, 3);
        Grid.SetRow(handle, 0);
        Grid.SetRowSpan(handle, isStacked ? 2 : 1);
        handle.VerticalAlignment = isStacked ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        handle.Margin = isStacked ? new Thickness(0, Math.Max(2, (metrics.RowMinHeight - 18) / 2), 0, 0) : new Thickness(0);
        grid.Children.Add(handle);

        if (hasMetadata)
        {
            var metadataPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            AttachItemContextMenu(metadataPanel);

            if (hasDue && dueBadge != null)
            {
                if (relativeBadge != null)
                {
                    relativeBadge.Margin = new Thickness(0, 1, 4, 1);
                    metadataPanel.Children.Add(relativeBadge);
                }
                dueBadge.Margin = new Thickness(0, 1, 4, 1);
                metadataPanel.Children.Add(dueBadge);
            }

            if (hasLinkedNote && linkButton != null)
            {
                linkButton.Margin = new Thickness(0, 1, 4, 1);
                metadataPanel.Children.Add(linkButton);
            }

            if (isStacked)
            {
                Grid.SetRow(metadataPanel, 1);
                Grid.SetColumn(metadataPanel, 1);
                metadataPanel.Margin = new Thickness(0, 2, 0, 2);
                metadataPanel.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(metadataPanel, 0);
                Grid.SetColumn(metadataPanel, 2);
                metadataPanel.Margin = new Thickness(4, 0, 4, 0);
                metadataPanel.HorizontalAlignment = HorizontalAlignment.Right;
            }

            grid.Children.Add(metadataPanel);
            grid.Tag = new TodoRowMetadataBinding(
                item,
                metadataPanel,
                check,
                handle,
                dueBadge,
                relativeBadge,
                dueLabel,
                linkButton,
                dueAt,
                hasDue,
                hasLinkedNote);
        }

        row.Child = grid;
        _todoRows.Add(row);

        // 新增动画：只对新建的项播放动画
        if (_controller.State.EnableAnimations && isNewItem)
        {
            row.Opacity = 0;
            AnimationHelper.GetTranslateTransform(row).Y = -20;

            Dispatcher.InvokeAsync(() =>
            {
                AnimationHelper.FadeIn(row, 250);
                AnimationHelper.TranslateTo(row, 0, 0, 250, AnimationHelper.SmoothEase);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        return row;
    }

    private UIElement BuildTodoTextColumnHost(
        PaperItem item,
        TodoTextBox primaryText,
        TodoVisualMetrics metrics,
        Action<FrameworkElement, int> attachItemContextMenu)
    {
        var columnCount = TodoItemColumnCount(item);
        if (columnCount <= 1)
        {
            return primaryText;
        }

        var host = new Grid
        {
            VerticalAlignment = VerticalAlignment.Stretch
        };
        for (var i = 0; i < columnCount; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(item.TodoColumnWidths[i], GridUnitType.Star) });
            if (i < columnCount - 1)
            {
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            }
        }

        void AddColumn(UIElement element, int column, Thickness margin)
        {
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Margin = margin;
            }
            Grid.SetColumn(element, column * 2);
            host.Children.Add(element);
        }

        AddColumn(primaryText, 0, new Thickness(0, 0, 3, 0));

        for (var i = 1; i < columnCount; i++)
        {
            var extraIndex = i - 1;
            var splitterColumn = (i * 2) - 1;
            var separator = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = PaperBorderBrush,
                Opacity = 0.9,
                IsHitTestVisible = false
            };
            Grid.SetColumn(separator, splitterColumn);
            host.Children.Add(separator);

            var splitter = new GridSplitter
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeWE,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false
            };
            splitter.DragCompleted += (_, _) =>
            {
                for (var widthIndex = 0; widthIndex < columnCount; widthIndex++)
                {
                    var actualWidth = host.ColumnDefinitions[widthIndex * 2].ActualWidth;
                    item.TodoColumnWidths[widthIndex] = actualWidth <= 0 ? 1 : Math.Clamp(Math.Round(actualWidth, 3), 0.2, 10000);
                }
                _controller.MarkDirty();
            };
            Grid.SetColumn(splitter, splitterColumn);
            host.Children.Add(splitter);

            var extraText = new TodoTextBox
            {
                Text = extraIndex < item.TodoExtraColumns.Count ? item.TodoExtraColumns[extraIndex] : "",
                IsDone = item.Done,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = item.Done ? BrightWeakTextBrush : TextBrush,
                CaretBrush = TextBrush,
                FontSize = metrics.TextFontSize,
                Padding = new Thickness(8, metrics.TextVerticalPadding, 4, metrics.TextVerticalPadding),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxLength = 5000
            };
            ApplyTodoTextLineSpacing(extraText, metrics);

            extraText.TextChanged += (_, _) =>
            {
                NormalizeTodoItemColumns(item);
                if (extraIndex < item.TodoExtraColumns.Count)
                {
                    item.TodoExtraColumns[extraIndex] = extraText.Text;
                    _controller.MarkDirty();
                }
            };
            extraText.PreviewKeyDown += (_, e) => HandleTodoKeyDown(e, item, extraText);
            attachItemContextMenu(extraText, i);
            AddColumn(extraText, i, new Thickness(6, 0, i == columnCount - 1 ? 0 : 3, 0));
        }

        return host;
    }

    private double TodoTextLineHeight(TodoVisualMetrics metrics)
        => Math.Round(metrics.TextFontSize * Math.Clamp(_controller.State.TodoLineSpacing, 0.8, 5.0), 1);

    private void ApplyTodoTextLineSpacing(TodoTextBox box, TodoVisualMetrics metrics)
    {
        var lineHeight = TodoTextLineHeight(metrics);
        var basePadding = new Thickness(box.Padding.Left, metrics.TextVerticalPadding, box.Padding.Right, metrics.TextVerticalPadding);
        var minHeight = Math.Max(metrics.RowMinHeight, lineHeight + (metrics.TextVerticalPadding * 2));
        box.ConfigureLineBox(basePadding, lineHeight, minHeight);
    }

    private void ApplyTodoRowDoneVisualState(DependencyObject root, bool done)
    {
        foreach (var box in VisualDescendants<TodoTextBox>(root))
        {
            box.IsDone = done;
            box.Foreground = done ? BrightWeakTextBrush : TextBrush;
            box.InvalidateVisual();
        }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in VisualDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private (Border Badge, TextBlock Label) BuildTodoDueBadge(PaperItem item, DateTime dueAt, TodoVisualMetrics metrics, double curWidth, bool isStacked)
    {
        var now = DateTime.Now;
        var isPastDue = dueAt < now;
        var isSoon = !isPastDue && dueAt <= now.AddMinutes(AppController.TodoReminderLeadTimeMinutes);
        var foreground = isPastDue
            ? Theme.DangerBrush
            : isSoon ? Theme.ActiveBrush : WeakTextBrush;
        var background = isPastDue
            ? Theme.Danger((byte)(Theme.IsDark ? 28 : 18))
            : isSoon ? Theme.Tint((byte)(Theme.IsDark ? 42 : 28)) : LinkedNoteBgBrush;

        var label = new TextBlock
        {
            Text = FormatTodoDueBadgeLabel(dueAt, now, _controller.State.TodoDueYearDisplayMode, curWidth, isStacked),
            Foreground = foreground,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = Math.Max(9.5, metrics.LinkedNoteNameFontSize),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        var badge = new Border
        {
            MinWidth = Math.Max(28, metrics.CheckColumnWidth * 1.2),
            MinHeight = Math.Max(20, metrics.RowMinHeight - 4),
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(5, 1, 5, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = background,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Format("ToolTipTodoDue", FormatTodoDueFull(dueAt)),
            Child = label
        };

        badge.MouseEnter += (_, _) =>
        {
            badge.Background = isPastDue ? Theme.Danger((byte)(Theme.IsDark ? 42 : 30)) : LinkedNoteHoverBgBrush;
            label.Foreground = isPastDue ? Theme.DangerBrush : TextBrush;
        };
        badge.MouseLeave += (_, _) =>
        {
            badge.Background = background;
            label.Foreground = foreground;
            badge.Opacity = 1.0;
        };
        badge.MouseLeftButtonDown += (_, e) =>
        {
            badge.Opacity = 0.72;
            e.Handled = true;
        };
        badge.MouseLeftButtonUp += (_, e) =>
        {
            badge.Opacity = 1.0;
            ShowTodoDueDialog(item);
            e.Handled = true;
        };

        return (badge, label);
    }

    private Border BuildTodoDueRelativeBadge(DateTime dueAt, TodoVisualMetrics metrics)
    {
        var now = DateTime.Now;
        var isPastDue = dueAt < now;
        var foreground = isPastDue ? Theme.DangerBrush : WeakTextBrush;
        var background = isPastDue
            ? Theme.Danger((byte)(Theme.IsDark ? 22 : 14))
            : Theme.Tint((byte)(Theme.IsDark ? 24 : 16));

        var label = new TextBlock
        {
            Text = FormatTodoDueRelativeLabel(dueAt, now),
            Foreground = foreground,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = Math.Max(9.5, metrics.LinkedNoteNameFontSize),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        return new Border
        {
            MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
            Margin = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = background,
            ToolTip = Strings.Format("ToolTipTodoDue", FormatTodoDueFull(dueAt)),
            Child = label
        };
    }

    internal static bool TryGetTodoDueAt(PaperItem item, out DateTime dueAt)
    {
        dueAt = default;
        if (string.IsNullOrWhiteSpace(item.DueAtLocal))
        {
            return false;
        }

        if (DateTime.TryParseExact(
                item.DueAtLocal,
                "yyyy-MM-ddTHH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dueAt))
        {
            return true;
        }

        return DateTime.TryParse(
            item.DueAtLocal,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out dueAt);
    }

    public void ShowTodoDueDialog(PaperItem item, Window? owner = null, Action? onSaved = null)
    {
        var hasExistingDue = TryGetTodoDueAt(item, out var existingDue);
        var initialDue = hasExistingDue ? existingDue : DateTime.Now.AddHours(1);
        initialDue = new DateTime(
            initialDue.Year,
            initialDue.Month,
            initialDue.Day,
            initialDue.Hour,
            initialDue.Minute,
            0);

        var dialogOwner = owner ?? this;
        var dialog = new Window
        {
            Title = Strings.Get("TodoDueDialogTitle"),
            Width = 354,
            Height = 242,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Owner = dialogOwner,
            Topmost = dialogOwner.Topmost
        };

        var border = new Border
        {
            Background = PaperBrush,
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusBlock),
            Padding = new Thickness(16),
            Effect = AppUi.FloatingShadow()
        };

        var stack = new StackPanel();
        border.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoDueDialogTitle"),
            Foreground = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoDueDialogMessage"),
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var pickerGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12)
        };
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

        var datePicker = new DatePicker
        {
            SelectedDate = initialDue.Date,
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Tint(80),
            Background = Theme.Tint((byte)(Theme.IsDark ? 22 : 12)),
            Foreground = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 13,
            Padding = new Thickness(7, 4, 7, 4)
        };
        Grid.SetColumn(datePicker, 0);
        pickerGrid.Children.Add(datePicker);

        var hourBox = TodoDueComboBox(Enumerable.Range(0, 24).Select(i => i.ToString("00", CultureInfo.InvariantCulture)));
        hourBox.SelectedItem = initialDue.Hour.ToString("00", CultureInfo.InvariantCulture);
        Grid.SetColumn(hourBox, 2);
        pickerGrid.Children.Add(hourBox);

        var minuteBox = TodoDueComboBox(Enumerable.Range(0, 60).Select(i => i.ToString("00", CultureInfo.InvariantCulture)));
        minuteBox.SelectedItem = initialDue.Minute.ToString("00", CultureInfo.InvariantCulture);
        Grid.SetColumn(minuteBox, 4);
        pickerGrid.Children.Add(minuteBox);

        stack.Children.Add(pickerGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var cancel = TodoDialogButton(Strings.Get("TodoDueDialogCancel"));
        var clear = TodoDialogButton(Strings.Get("TodoDueDialogClear"));
        var ok = TodoDialogButton(Strings.Get("TodoDueDialogOk"), isPrimary: true);
        cancel.Click += (_, _) => dialog.Close();
        clear.Click += (_, _) =>
        {
            SetTodoDue(item, null);
            onSaved?.Invoke();
            dialog.Close();
        };
        ok.Click += (_, _) =>
        {
            var selectedDate = datePicker.SelectedDate ?? initialDue.Date;
            var hour = int.TryParse(hourBox.SelectedItem as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHour)
                ? parsedHour
                : initialDue.Hour;
            var minute = int.TryParse(minuteBox.SelectedItem as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinute)
                ? parsedMinute
                : initialDue.Minute;
            var dueAt = selectedDate.Date.AddHours(hour).AddMinutes(minute);

            SetTodoDue(item, dueAt);
            onSaved?.Invoke();
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(clear);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        dialog.Content = border;
        dialog.Loaded += (_, _) =>
        {
            datePicker.Focus();
            datePicker.IsDropDownOpen = !hasExistingDue;
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.ShowDialog();
    }

    public void ShowTodoReminderIntervalDialog(PaperItem item, Window? owner = null, Action? onSaved = null)
    {
        var initialValue = item.ReminderIntervalValue ?? _controller.State.TodoReminderIntervalValue;
        var initialUnit = TodoReminderIntervalUnits.Normalize(item.ReminderIntervalUnit ?? _controller.State.TodoReminderIntervalUnit);

        var dialogOwner = owner ?? this;
        var dialog = new Window
        {
            Title = Strings.Get("TodoReminderIntervalDialogTitle"),
            Width = 326,
            Height = 216,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Owner = dialogOwner,
            Topmost = dialogOwner.Topmost
        };

        var border = new Border
        {
            Background = PaperBrush,
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusBlock),
            Padding = new Thickness(16),
            Effect = AppUi.FloatingShadow()
        };

        var stack = new StackPanel();
        border.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoReminderIntervalDialogTitle"),
            Foreground = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoReminderIntervalDialogMessage"),
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var editorGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

        var valueBox = new TextBox
        {
            Text = Math.Clamp(initialValue <= 0 ? 10 : initialValue, 1, 240).ToString(CultureInfo.InvariantCulture),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Tint(80),
            Background = Theme.Tint((byte)(Theme.IsDark ? 22 : 12)),
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 13,
            Padding = new Thickness(8, 5, 8, 5),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(valueBox, 0);
        editorGrid.Children.Add(valueBox);

        var unitBox = TodoDueComboBox(new[]
        {
            Strings.Get("TodoReminderIntervalUnitMinutes"),
            Strings.Get("TodoReminderIntervalUnitHours")
        });
        unitBox.SelectedIndex = initialUnit == TodoReminderIntervalUnits.Hours ? 1 : 0;
        Grid.SetColumn(unitBox, 2);
        editorGrid.Children.Add(unitBox);
        stack.Children.Add(editorGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var cancel = TodoDialogButton(Strings.Get("TodoDueDialogCancel"));
        var clear = TodoDialogButton(Strings.Get("TodoReminderIntervalDialogUseGlobal"));
        var ok = TodoDialogButton(Strings.Get("TodoDueDialogOk"), isPrimary: true);
        cancel.Click += (_, _) => dialog.Close();
        clear.Click += (_, _) =>
        {
            SetTodoReminderInterval(item, null, null);
            onSaved?.Invoke();
            dialog.Close();
        };
        ok.Click += (_, _) =>
        {
            var value = int.TryParse(valueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : initialValue;
            var unit = unitBox.SelectedIndex == 1 ? TodoReminderIntervalUnits.Hours : TodoReminderIntervalUnits.Minutes;
            SetTodoReminderInterval(item, Math.Clamp(value <= 0 ? 1 : value, 1, 240), unit);
            onSaved?.Invoke();
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(clear);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        dialog.Content = border;
        dialog.Loaded += (_, _) =>
        {
            valueBox.Focus();
            valueBox.SelectAll();
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.ShowDialog();
    }

    private ComboBox TodoDueComboBox(IEnumerable<string> values)
    {
        var box = new ComboBox
        {
            ItemsSource = values.ToList(),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Tint(80),
            Background = Theme.Tint((byte)(Theme.IsDark ? 22 : 12)),
            Foreground = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 13,
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEditable = false,
            MaxDropDownHeight = 220
        };
        return box;
    }

    private static Button TodoDialogButton(string text, bool isPrimary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = isPrimary ? Theme.ActiveBrush : Theme.HoverBrush,
            Foreground = isPrimary ? PaperBrush : TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Cursor = Cursors.Hand,
            MinWidth = 64
        };
        return button;
    }

    private void SetTodoDue(PaperItem item, DateTime? dueAt)
    {
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.DueAtLocal = dueAt?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        _controller.ResetTodoReminder(item.Id);
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
    }

    public void ClearTodoDue(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DueAtLocal))
        {
            return;
        }

        SetTodoDue(item, null);
    }

    private void SetTodoReminderInterval(PaperItem item, int? value, string? unit)
    {
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.ReminderIntervalValue = value;
        item.ReminderIntervalUnit = value is int ? TodoReminderIntervalUnits.Normalize(unit) : null;
        _controller.ResetTodoReminder(item.Id);
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
    }

    public void ClearTodoReminderInterval(PaperItem item)
    {
        if (item.ReminderIntervalValue is not int)
        {
            return;
        }

        SetTodoReminderInterval(item, null, null);
    }

    private static string FormatTodoDueLabel(DateTime dueAt, DateTime now, bool relative)
    {
        if (relative)
        {
            return FormatTodoDueRelativeLabel(dueAt, now);
        }

        return FormatTodoDueAbsoluteLabel(dueAt, now, TodoDueYearDisplayModes.None);
    }

    private static string FormatTodoDueBadgeLabel(DateTime dueAt, DateTime now, string? yearDisplayMode, double cardWidth, bool isStacked)
    {
        var isToday = dueAt.Date == now.Date;
        var isTomorrow = dueAt.Date == now.Date.AddDays(1);
        var isThisYear = dueAt.Year == now.Year;

        if (isToday)
        {
            return "今天 " + dueAt.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        if (isTomorrow)
        {
            return Strings.Format("TodoDueTomorrowLabel", dueAt.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        var normalizedYearMode = TodoDueYearDisplayModes.Normalize(yearDisplayMode);
        if (normalizedYearMode == TodoDueYearDisplayModes.Full || (!isThisYear && cardWidth >= 300))
        {
            return dueAt.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
        }

        if (normalizedYearMode == TodoDueYearDisplayModes.Short)
        {
            return dueAt.ToString("yy/M/d HH:mm", CultureInfo.CurrentCulture);
        }

        return dueAt.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatTodoDueAbsoluteLabel(DateTime dueAt, DateTime now, string? yearDisplayMode)
    {
        var normalizedYearMode = TodoDueYearDisplayModes.Normalize(yearDisplayMode);
        if (normalizedYearMode == TodoDueYearDisplayModes.Full)
        {
            return dueAt.ToString("yyyy年M/d HH:mm", CultureInfo.CurrentCulture);
        }
        if (normalizedYearMode == TodoDueYearDisplayModes.Short)
        {
            return dueAt.ToString("yy年M/d HH:mm", CultureInfo.CurrentCulture);
        }

        if (dueAt.Date == now.Date)
        {
            return dueAt.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        if (dueAt.Date == now.Date.AddDays(1))
        {
            return Strings.Format("TodoDueTomorrowLabel", dueAt.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        return dueAt.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatTodoDueRelativeLabel(DateTime dueAt, DateTime now)
    {
        var delta = dueAt - now;
        var isPast = delta.TotalSeconds < 0;
        var span = delta.Duration();
        var totalMinutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes % (24 * 60)) / 60;
        var minutes = totalMinutes % 60;
        var parts = new List<string>();

        if (days > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentUICulture, Strings.Get("TodoDueRelativeDayUnit"), days));
        }
        if (hours > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentUICulture, Strings.Get("TodoDueRelativeHourUnit"), hours));
        }
        if (minutes > 0 || parts.Count == 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentUICulture, Strings.Get("TodoDueRelativeMinuteUnit"), minutes <= 0 ? 1 : minutes));
        }

        var text = string.Concat(parts);

        return isPast
            ? Strings.Format("TodoDueRelativePast", text)
            : Strings.Format("TodoDueRelativeFuture", text);
    }

    private static string FormatTodoDueFull(DateTime dueAt)
        => dueAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    private void HandleTodoKeyDown(KeyEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (e.Key == Key.Back && _suppressTodoBackspaceUntilKeyUp)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            var newItem = AddItemAfter(item, "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && AllTodoTextColumnsBlank(item) && _paper.Items.Count > 1)
        {
            var previous = PreviousItem(item);
            var next = NextItem(item);
            var focusTarget = previous?.Id ?? next?.Id;
            _suppressTodoBackspaceUntilKeyUp = true;

            // 退格删除不播放动画，直接删除
            PushUndoSnapshot();
            _controller.State.RecordDeletedItem(item.Id, _paper.Id);
            _paper.Items.RemoveAll(i => i.Id == item.Id);

            if (_paper.Items.Count == 0)
            {
                var replacement = new PaperItem();
                _paper.Items.Add(replacement);
                focusTarget = replacement.Id;
            }

            NormalizeTodoItems();
            NormalizeOrders();
            _controller.MarkDirty();

            var focusPlacement = previous != null ? TodoFocusPlacement.End : TodoFocusPlacement.Start;
            RebuildTodoRows(focusTarget, focusPlacement);
            e.Handled = true;
        }
    }

    private void HandleTodoPaste(DataObjectPastingEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (!ClipboardHelper.TryGetText(out var raw) || string.IsNullOrEmpty(raw))
        {
            return;
        }

        var lines = raw
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanPastedTodoLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length > 5000 ? line[..5000] : line)
            .ToList();

        if (lines.Count > 200)
        {
            lines = lines.Take(200).ToList();
        }

        if (lines.Count <= 1)
        {
            return;
        }

        e.CancelCommand();

        PushUndoSnapshot();
        ReplaceSelection(box, lines[0]);
        item.Text = box.Text;

        var last = item;
        var newItems = new List<PaperItem>();
        foreach (var line in lines.Skip(1))
        {
            last = AddItemAfter(last, line, pushUndo: false);
            newItems.Add(last);
        }

        _pendingFocusItemId = last.Id;
        RebuildTodoRows(last.Id);

        // 粘贴多行时的错峰动画
        if (_controller.State.EnableAnimations && newItems.Count > 1)
        {
            var animationGeneration = _todoRowsGeneration;
            for (int i = 0; i < Math.Min(newItems.Count, 15); i++)
            {
                var animItem = newItems[i];
                var animRow = _todoRows.FirstOrDefault(r => (string)r.Tag == animItem.Id);
                if (animRow == null) continue;

                var delay = i * 40;
                animRow.Opacity = 0;
                AnimationHelper.GetTranslateTransform(animRow).Y = -15;

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(delay),
                    Tag = animRow
                };
                timer.Tick += (s, _) =>
                {
                    timer.Stop();
                    var row = (Border)timer.Tag;
                    if (animationGeneration != _todoRowsGeneration || !_todoRows.Contains(row))
                    {
                        return;
                    }

                    AnimationHelper.FadeIn(row, 200);
                    AnimationHelper.TranslateTo(row, 0, 0, 220, AnimationHelper.QuickEase);
                };
                timer.Start();
            }
        }

        _controller.MarkDirty();
    }

    private static string CleanPastedTodoLine(string line)
    {
        var cleaned = line.Trim();

        cleaned = TodoCheckboxCleanRegex().Replace(cleaned, "");
        cleaned = TodoBulletCleanRegex().Replace(cleaned, "");
        cleaned = TodoNumberCleanRegex().Replace(cleaned, "");
        cleaned = TodoGlyphCleanRegex().Replace(cleaned, "");

        return cleaned.Trim();
    }


    public void UpdateTodoLinkFeature()
    {
        if (_linkNoteButton != null)
        {
            _linkNoteButton.Visibility = _controller.State.EnableTodoNoteLinks ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_controller.State.EnableTodoNoteLinks)
        {
            EndNoteLinkMouseGesture(commit: false);
            SetNoteLinkDropTarget(null);
        }

        RefreshTodoRowsForExternalChange();
    }


    public PaperItem AddItemAfter(PaperItem? after, string text, bool pushUndo = true)
    {
        if (pushUndo) PushUndoSnapshot();
        var ordered = OrderedItems().ToList();
        var index = after == null ? ordered.Count : ordered.FindIndex(i => i.Id == after.Id) + 1;
        if (index < 0) index = ordered.Count;

        var newItem = new PaperItem
        {
            Text = text,
            Done = false
        };

        // Newly added uncompleted items must always be placed above completed items.
        if (!newItem.Done)
        {
            var firstCompletedIndex = ordered.FindIndex(i => i.Done);
            if (firstCompletedIndex >= 0 && index > firstCompletedIndex)
            {
                index = firstCompletedIndex;
            }
        }

        ordered.Insert(index, newItem);
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        return newItem;
    }

    public void RemoveItem(PaperItem item, bool rebuild = true, string? focusItemId = null)
    {
        PushUndoSnapshot();
        var fallbackFocus = focusItemId ?? PreviousItem(item)?.Id ?? NextItem(item)?.Id;
        var itemId = item.Id;

        // 删除动画
        if (_controller.State.EnableAnimations)
        {
            var row = _todoRows.FirstOrDefault(r => (string)r.Tag == itemId);
            if (row != null)
            {
                _controller.State.RecordDeletedItem(itemId, _paper.Id);
                _paper.Items.RemoveAll(i => i.Id == itemId);

                if (_paper.Items.Count == 0)
                {
                    var replacement = new PaperItem();
                    _paper.Items.Add(replacement);
                    fallbackFocus = replacement.Id;
                }

                NormalizeTodoItems();
                NormalizeOrders();
                _controller.MarkDirty();

                var animationGeneration = _todoRowsGeneration;
                row.IsHitTestVisible = false;
                AnimationHelper.EnsureTransform(row);
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };

                fadeOut.Completed += (s, e) =>
                {
                    if (rebuild && animationGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(fallbackFocus);
                    }
                };

                row.BeginAnimation(OpacityProperty, fadeOut);
                AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                return;
            }
        }

        // 无动画或找不到行时直接删除
        _controller.State.RecordDeletedItem(itemId, _paper.Id);
        _paper.Items.RemoveAll(i => i.Id == itemId);

        if (_paper.Items.Count == 0)
        {
            var replacement = new PaperItem();
            _paper.Items.Add(replacement);
            fallbackFocus = replacement.Id;
        }

        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        if (rebuild)
        {
            RebuildTodoRows(fallbackFocus);
        }
    }

    public void ClearDoneItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();
        var completedItems = OrderedItems().Where(i => i.Done).ToList();
        if (completedItems.Count == 0)
        {
            return;
        }

        var completedItemIds = new HashSet<string>(completedItems.Select(i => i.Id), StringComparer.Ordinal);
        var clearDoneGeneration = ++_clearDoneGeneration;

        PushUndoSnapshot();
        var remainingItems = OrderedItems()
            .Where(i => !completedItemIds.Contains(i.Id))
            .ToList();

        if (remainingItems.Count == 0)
        {
            remainingItems.Add(new PaperItem());
        }

        _paper.Items = remainingItems;
        NormalizeTodoItems();
        NormalizeOrders();

        var focus = remainingItems.FirstOrDefault(i => i.Id == focusedId)?.Id
            ?? remainingItems.FirstOrDefault(i => !IsBlank(i))?.Id
            ?? remainingItems.FirstOrDefault()?.Id;

        _controller.MarkDirty();

        // 批量消失动画
        if (_controller.State.EnableAnimations && completedItems.Count > 0)
        {
            var animatedRows = completedItems
                .Take(15)
                .Select(item => _todoRows.FirstOrDefault(r => (string)r.Tag == item.Id))
                .Where(row => row != null)
                .Cast<Border>()
                .ToList();

            if (animatedRows.Count > 0)
            {
                var rowGeneration = _todoRowsGeneration;
                for (int i = 0; i < animatedRows.Count; i++)
                {
                    var row = animatedRows[i];
                    row.IsHitTestVisible = false;
                    var delay = i * 30;
                    void StartRowAnimation()
                    {
                        if (clearDoneGeneration != _clearDoneGeneration ||
                            rowGeneration != _todoRowsGeneration ||
                            !_todoRows.Contains(row))
                        {
                            return;
                        }

                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                        var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(180))
                        {
                            EasingFunction = AnimationHelper.QuickEase
                        };

                        row.BeginAnimation(OpacityProperty, fadeOut);
                        AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                    }

                    if (delay == 0)
                    {
                        StartRowAnimation();
                        continue;
                    }

                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(delay)
                    };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        StartRowAnimation();
                    };
                    timer.Start();
                }

                var finalizeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(((animatedRows.Count - 1) * 30) + 180)
                };
                finalizeTimer.Tick += (_, _) =>
                {
                    finalizeTimer.Stop();
                    if (clearDoneGeneration == _clearDoneGeneration &&
                        rowGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(focus);
                    }
                };
                finalizeTimer.Start();
                return;
            }
        }

        RebuildTodoRows(focus);
    }

    public bool TryHitTodoRow(Point screenPoint, out string? itemId)
    {
        itemId = null;
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || _paper.IsCollapsed || !IsVisible)
        {
            return false;
        }

        foreach (var row in _todoRows)
        {
            if (row.Tag is not string rowItemId || !row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var point = row.PointFromScreen(screenPoint);
            if (point.X < 0 || point.X > row.ActualWidth || point.Y < 0 || point.Y > row.ActualHeight)
            {
                continue;
            }

            itemId = rowItemId;
            return true;
        }

        return false;
    }

    public void SetNoteLinkDropTarget(string? itemId)
    {
        if (_linkedNoteDropRow?.Tag is string currentId &&
            string.Equals(currentId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        ClearNoteLinkDropTargetVisual();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var row = _todoRows.FirstOrDefault(r =>
            r.Tag is string rowItemId &&
            string.Equals(rowItemId, itemId, StringComparison.Ordinal));
        if (row == null)
        {
            return;
        }

        _linkedNoteDropRow = row;
        row.Background = NoteLinkTargetBgBrush;
        row.BorderBrush = NoteLinkTargetBorderBrush;
        row.BorderThickness = new Thickness(1);
        row.Padding = new Thickness(1, 3, 1, 3);
    }

    public bool LinkNoteToTodo(string itemId, string noteId)
    {
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || !_controller.IsExistingNote(noteId))
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return false;
        }

        if (string.Equals(item.LinkedNoteId, noteId, StringComparison.Ordinal))
        {
            return true;
        }

        var focusedId = CurrentFocusedTodoItemId();
        PushUndoSnapshot();
        item.LinkedNoteId = noteId;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
        return true;
    }

    private void UnlinkNoteFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedNoteId))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.LinkedNoteId = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
    }

    private void ClearNoteLinkDropTargetVisual()
    {
        var row = _linkedNoteDropRow;
        if (row == null)
        {
            return;
        }

        _linkedNoteDropRow = null;
        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (!Equals(_activeDropRow, row))
        {
            row.Background = row.IsMouseOver ? HoverBrush : Brushes.Transparent;
        }
    }







    private PaperItem? PreviousItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index > 0 ? ordered[index - 1] : null;
    }

    private PaperItem? NextItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index >= 0 && index < ordered.Count - 1 ? ordered[index + 1] : null;
    }

    private void BeginTodoMouseDrag()
    {
        if (_todoDrag == null)
        {
            return;
        }

        _todoDrag.IsDragging = true;

        var rowOrigin = _todoDrag.SourceRow.TranslatePoint(new Point(0, 0), this);
        _todoDrag.MouseOffsetInRow = new Point(
            Math.Max(0, _todoDrag.StartPoint.X - rowOrigin.X),
            Math.Max(0, _todoDrag.StartPoint.Y - rowOrigin.Y));

        _todoDrag.SourceRow.Opacity = 0.25;
        _todoDrag.SourceRow.Background = HoverBrush;
        _todoDrag.Handle.Opacity = 0.9;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _todoDrag.Ghost = CreateTodoDragGhost(_todoDrag);
        _dragLayer?.Children.Add(_todoDrag.Ghost);
        UpdateTodoDragGhost(_todoDrag, _todoDrag.StartPoint);

        ShowAppendAreaAsTrashBin(active: true);
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoMouseDrag(commit: _todoDrag.IsDragging);
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(this);

        if (!_todoDrag.IsDragging)
        {
            var movedEnough =
                Math.Abs(current.X - _todoDrag.StartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _todoDrag.StartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

            if (!movedEnough)
            {
                return;
            }

            BeginTodoMouseDrag();
        }

        var panelPoint = _todoPanel != null ? e.GetPosition(_todoPanel) : current;
        UpdateTodoMouseDrag(panelPoint, current);
        e.Handled = true;
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        EndTodoMouseDrag(commit: _todoDrag.IsDragging);
        e.Handled = true;
    }

    private void UpdateTodoMouseDrag(Point pointOnPanel, Point pointOnWindow)
    {
        if (_todoDrag == null || _todoPanel == null)
        {
            return;
        }

        UpdateTodoDragGhost(_todoDrag, pointOnWindow);
        ClearActiveDropIndicator();

        bool overTrash = false;
        if (_appendArea != null && _appendArea.IsVisible)
        {
            try
            {
                var transform = this.TransformToVisual(_appendArea);
                Point posInAppend = transform.Transform(pointOnWindow);
                if (posInAppend.X >= 0 && posInAppend.X <= _appendArea.ActualWidth &&
                    posInAppend.Y >= 0 && posInAppend.Y <= _appendArea.ActualHeight)
                {
                    overTrash = true;
                }
            }
            catch
            {
                // Fallback in case layout is not fully updated
            }
        }

        if (overTrash)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = true;
            ShowAppendAreaAsTrashBin(active: true, hovered: true);
            return;
        }

        ShowAppendAreaAsTrashBin(active: true, hovered: false);

        var candidates = _todoRows
            .Where(row => row.Tag is string id && id != _todoDrag.ItemId)
            .ToList();

        if (candidates.Count == 0)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
            return;
        }

        double bestDist = double.MaxValue;
        Border? bestRow = null;
        var bestPlacement = DropPlacement.After;

        foreach (var row in candidates)
        {
            double top = row.TranslatePoint(new Point(0, 0), _todoPanel).Y;
            ConsiderDropBoundary(row, DropPlacement.Before, top);
            ConsiderDropBoundary(row, DropPlacement.After, top + row.ActualHeight);
        }

        if (bestRow == null)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
            return;
        }

        ShowDropIndicator(bestRow, bestPlacement);
        _todoDrag.TargetId = bestRow.Tag as string;
        _todoDrag.TargetPlacement = bestPlacement;
        _todoDrag.DropAtEnd = false;

        void ConsiderDropBoundary(Border row, DropPlacement placement, double y)
        {
            double dist = Math.Abs(pointOnPanel.Y - y);
            if (dist >= bestDist)
            {
                return;
            }

            bestDist = dist;
            bestRow = row;
            bestPlacement = placement;
        }
    }

    private Border CreateTodoDragGhost(TodoDragState state)
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
        var text = item?.Text ?? "";
        var done = item?.Done == true;

        var ghost = new Border
        {
            Width = Math.Max(state.SourceRow.ActualWidth, 160),
            MinHeight = Math.Max(state.SourceRow.ActualHeight, 30),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = Theme.Tint(150),
            BorderThickness = new Thickness(1),
            Opacity = 0.65,
            IsHitTestVisible = false,
            Effect = AppUi.FloatingShadow()
        };

        var grid = new Grid
        {
            IsHitTestVisible = false
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(metrics.CheckColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(18, metrics.CheckColumnWidth - 4)) });

        var check = new TextBlock
        {
            Text = done ? "☑" : "☐",
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontSize = metrics.GhostTextFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var content = new TextBlock
        {
            Text = text,
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontSize = metrics.GhostTextFontSize,
            Padding = new Thickness(2, metrics.TextVerticalPadding, 2, metrics.TextVerticalPadding),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (done)
        {
            content.TextDecorations = TextDecorations.Strikethrough;
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var handle = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.58,
            FontSize = Math.Max(12, metrics.GhostTextFontSize - 1),
            FontFamily = AppTypography.SymbolFontFamily,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(handle, 2);
        grid.Children.Add(handle);

        ghost.Child = grid;
        return ghost;
    }

    private void CloseTodoDragGhost(TodoDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        _dragLayer?.Children.Remove(state.Ghost);
        state.Ghost = null;
    }

    private static void UpdateTodoDragGhost(TodoDragState state, Point pointOnWindow)
    {
        if (state.Ghost == null)
        {
            return;
        }

        Canvas.SetLeft(state.Ghost, pointOnWindow.X - state.MouseOffsetInRow.X);
        Canvas.SetTop(state.Ghost, pointOnWindow.Y - state.MouseOffsetInRow.Y);
    }

    private void EndTodoMouseDrag(bool commit)
    {
        var state = _todoDrag;
        if (state == null)
        {
            return;
        }

        _todoDrag = null;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Mouse.OverrideCursor = null;

        CloseTodoDragGhost(state);

        state.SourceRow.Opacity = 1.0;
        state.SourceRow.Background = Brushes.Transparent;
        state.Handle.Opacity = 1.0;

        ClearActiveDropIndicator();
        ShowAppendAreaAsTrashBin(active: false);

        if (!commit)
        {
            RebuildTodoRows(state.ItemId);
            return;
        }

        if (state.DropAtEnd)
        {
            var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
            if (item != null)
            {
                RemoveItem(item, rebuild: true);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetId))
        {
            MoveItem(state.ItemId, state.TargetId, state.TargetPlacement, focusDragged: true);
            return;
        }

        RebuildTodoRows(state.ItemId);
    }

    private void MoveItemBefore(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.Before, focusDragged);
    }

    private void MoveItemAfter(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.After, focusDragged);
    }

    private void MoveItem(string draggedId, string targetId, DropPlacement placement, bool focusDragged)
    {
        if (draggedId == targetId)
        {
            return;
        }

        var ordered = OrderedItems().ToList();
        var originalOrder = ordered.Select(i => i.Id).ToList();

        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        var target = ordered.FirstOrDefault(i => i.Id == targetId);

        if (dragged == null || target == null)
        {
            return;
        }

        ordered.Remove(dragged);

        var targetIndex = ordered.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (placement == DropPlacement.After)
        {
            targetIndex++;
        }

        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(targetIndex, dragged);

        if (originalOrder.SequenceEqual(ordered.Select(i => i.Id)))
        {
            return;
        }

        PushUndoSnapshot();
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusDragged ? dragged.Id : null);
    }

    private void MoveItemToEnd(string draggedId, bool focusDragged = true)
    {
        var ordered = OrderedItems().ToList();
        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        if (dragged == null)
        {
            return;
        }

        var oldIndex = ordered.IndexOf(dragged);
        if (oldIndex == ordered.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        ordered.Remove(dragged);
        ordered.Add(dragged);

        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        RebuildTodoRows(focusDragged ? dragged.Id : null);
    }

    private System.Windows.Threading.DispatcherTimer? _autoReorderTimer;
    private string? _recentlyMovedItemId;

    public void QueueAutoReorderCompletedItems(string itemId, bool isChecking)
    {
        if (_autoReorderTimer != null)
        {
            _autoReorderTimer.Stop();
            _autoReorderTimer = null;
        }

        if (!_controller.State.EnableAnimations)
        {
            ExecuteAutoReorderCompletedItems(itemId, isChecking);
            return;
        }

        var delay = isChecking ? 260 : 160;
        _autoReorderTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };
        _autoReorderTimer.Tick += (_, _) =>
        {
            _autoReorderTimer?.Stop();
            _autoReorderTimer = null;
            ExecuteAutoReorderCompletedItems(itemId, isChecking);
        };
        _autoReorderTimer.Start();
    }

    private void ExecuteAutoReorderCompletedItems(string triggerItemId, bool isChecking)
    {
        var ordered = OrderedItems().ToList();
        var triggerItem = ordered.FirstOrDefault(i => i.Id == triggerItemId);
        if (triggerItem == null)
        {
            return;
        }

        if (isChecking && triggerItem.Done)
        {
            var currentIndex = ordered.IndexOf(triggerItem);
            var lastUncompletedIndex = ordered.FindLastIndex(i => !i.Done);
            if (currentIndex >= 0 && lastUncompletedIndex > currentIndex)
            {
                PushUndoSnapshot();
                ordered.RemoveAt(currentIndex);
                ordered.Insert(lastUncompletedIndex, triggerItem);

                _recentlyMovedItemId = triggerItemId;
                _paper.Items = ordered;
                NormalizeTodoItems();
                NormalizeOrders();
                _controller.MarkDirty();
                RebuildTodoRows();
            }
        }
        else if (!isChecking && !triggerItem.Done)
        {
            var currentIndex = ordered.IndexOf(triggerItem);
            var firstCompletedIndex = ordered.FindIndex(i => i.Done);
            if (firstCompletedIndex >= 0 && currentIndex > firstCompletedIndex)
            {
                PushUndoSnapshot();
                ordered.RemoveAt(currentIndex);
                ordered.Insert(firstCompletedIndex, triggerItem);

                _recentlyMovedItemId = triggerItemId;
                _paper.Items = ordered;
                NormalizeTodoItems();
                NormalizeOrders();
                _controller.MarkDirty();
                RebuildTodoRows();
            }
        }
    }

    private IEnumerable<PaperItem> OrderedItems()
    {
        return _paper.Items.OrderBy(i => i.Order).ToList();
    }

    private void NormalizeTodoItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var ordered = _paper.Items.ToList();
        if (ordered.Count == 0)
        {
            ordered.Add(new PaperItem());
        }

        _paper.Items = ordered;
    }

    private static bool IsBlank(PaperItem item)
    {
        return AllTodoTextColumnsBlank(item) &&
            string.IsNullOrWhiteSpace(item.DueAtLocal) &&
            item.ReminderIntervalValue is not int &&
            string.IsNullOrWhiteSpace(item.LinkedNoteId);
    }

    private static bool AllTodoTextColumnsBlank(PaperItem item)
        => string.IsNullOrWhiteSpace(item.Text) &&
            (item.TodoExtraColumns == null || item.TodoExtraColumns.All(string.IsNullOrWhiteSpace));

    private string? CurrentFocusedTodoItemId()
    {
        var focused = FocusManager.GetFocusedElement(this);

        if (focused is TodoTextBox box)
        {
            foreach (var pair in _todoEditors)
            {
                if (ReferenceEquals(pair.Value, box))
                {
                    return pair.Key;
                }
            }
        }

        return null;
    }

    private void NormalizeOrders()
    {
        // Preserve the current list order. Sorting here would undo freshly inserted
        // or dragged rows because new items start with Order = 0 until we renumber them.
        for (var i = 0; i < _paper.Items.Count; i++)
        {
            _paper.Items[i].Order = i;
        }
    }

    private void ShowDropIndicator(Border row, DropPlacement placement)
    {
        if (!Equals(_activeDropRow, row))
        {
            ClearActiveDropIndicator();
            _activeDropRow = row;
        }

        if (_dragLayer == null)
        {
            return;
        }

        if (_dropIndicatorLine == null)
        {
            _dropIndicatorLine = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = DropIndicatorBrush,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_dropIndicatorLine, 1001);
            _dragLayer.Children.Add(_dropIndicatorLine);
        }

        _dropIndicatorLine.Background = DropIndicatorBrush;
        var rowOrigin = row.TranslatePoint(new Point(0, 0), _dragLayer);
        var y = placement == DropPlacement.Before
            ? rowOrigin.Y
            : rowOrigin.Y + row.ActualHeight;
        var width = Math.Max(24, row.ActualWidth - 8);

        _dropIndicatorLine.Width = width;
        Canvas.SetLeft(_dropIndicatorLine, rowOrigin.X + 4);
        Canvas.SetTop(_dropIndicatorLine, y - (_dropIndicatorLine.Height / 2));
    }

    private void ClearDropIndicator(Border row)
    {
        if (Equals(_activeDropRow, row))
        {
            _activeDropRow = null;
        }

        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (_dropIndicatorLine != null)
        {
            _dragLayer?.Children.Remove(_dropIndicatorLine);
            _dropIndicatorLine = null;
        }
    }

    private void ClearActiveDropIndicator()
    {
        if (_activeDropRow != null)
        {
            ClearDropIndicator(_activeDropRow);
            _activeDropRow = null;
        }
    }

    private static void ReplaceSelection(TextBox box, string replacement)
    {
        box.SelectedText = replacement;
        box.SelectionStart = box.SelectionStart + replacement.Length;
    }


    private static List<PaperItem> CloneItems(List<PaperItem> items)
    {
        return items.Select(i => new PaperItem
        {
            Id = i.Id,
            Text = i.Text,
            Done = i.Done,
            Order = i.Order,
            TodoColumnCount = TodoItemColumnCount(i),
            TodoExtraColumns = i.TodoExtraColumns?.ToList() ?? new List<string>(),
            TodoColumnWidths = i.TodoColumnWidths?.ToList() ?? new List<double>(),
            LinkedNoteId = i.LinkedNoteId,
            DueAtLocal = i.DueAtLocal,
            ReminderIntervalValue = i.ReminderIntervalValue,
            ReminderIntervalUnit = i.ReminderIntervalUnit
        }).ToList();
    }

    private void PushUndoSnapshot()
    {
        CommitFocusedTextIfNeeded();

        _undoStack.Add(CloneItems(_paper.Items));
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
    }

    private void CommitFocusedTextIfNeeded()
    {
        var focusedId = CurrentFocusedTodoItemId();
        if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
        {
            if (_activeOriginalItemId == focusedId && _activeOriginalText != null && box.Text != _activeOriginalText)
            {
                var item = _paper.Items.FirstOrDefault(i => i.Id == focusedId);
                if (item != null)
                {
                    var oldText = item.Text;
                    item.Text = _activeOriginalText;

                    var oldSnapshot = CloneItems(_paper.Items);
                    _undoStack.Add(oldSnapshot);
                    if (_undoStack.Count > MaxUndoDepth)
                    {
                        _undoStack.RemoveAt(0);
                    }

                    item.Text = oldText;
                    _activeOriginalText = oldText;
                }
            }
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _redoStack.Add(currentItems);

        var previousItems = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        _paper.Items = previousItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _undoStack.Add(currentItems);

        var nextItems = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _paper.Items = nextItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_paper.Type == PaperTypes.Note)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanUndo)
                    {
                        return;
                    }
                }

                Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanRedo)
                    {
                        return;
                    }
                }

                Redo();
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            _suppressTodoBackspaceUntilKeyUp = false;
        }
    }


}
