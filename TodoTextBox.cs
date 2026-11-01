using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;

namespace PaperTodo;

public sealed class TodoTextBox : TextBox
{
    private static readonly ControlTemplate CenteredTemplate = BuildCenteredTemplate();
    private Thickness _basePadding = new(2, 2, 2, 2);
    private double _lineHeight = double.NaN;
    private double _minimumLineBoxHeight;
    private bool _isUpdatingPadding;
    private bool _centerUpdateQueued;

    public static readonly DependencyProperty IsDoneProperty =
        DependencyProperty.Register(
            nameof(IsDone),
            typeof(bool),
            typeof(TodoTextBox),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsDone
    {
        get => (bool)GetValue(IsDoneProperty);
        set => SetValue(IsDoneProperty, value);
    }

    public TodoTextBox()
    {
        Template = CenteredTemplate;
        Loaded += (_, _) => QueueCenteringUpdate();
        SizeChanged += (_, _) => QueueCenteringUpdate();
        TextChanged += (_, _) => QueueCenteringUpdate();
    }

    public void ConfigureLineBox(Thickness basePadding, double lineHeight, double minHeight)
    {
        _basePadding = basePadding;
        _lineHeight = lineHeight;
        _minimumLineBoxHeight = minHeight;
        MinHeight = minHeight;
        VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Top;
        SetValue(TextBlock.LineHeightProperty, VisibleLineHeight(lineHeight));
        SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        QueueCenteringUpdate();
    }

    private static ControlTemplate BuildCenteredTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        host.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        host.SetValue(Control.PaddingProperty, new Thickness(0));
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        border.AppendChild(host);

        return new ControlTemplate(typeof(TextBox))
        {
            VisualTree = border
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == FontSizeProperty ||
            e.Property == FontFamilyProperty ||
            e.Property == TextWrappingProperty ||
            e.Property == TextBlock.LineHeightProperty)
        {
            QueueCenteringUpdate();
        }
    }

    private void QueueCenteringUpdate()
    {
        if (_isUpdatingPadding || _centerUpdateQueued)
        {
            return;
        }

        _centerUpdateQueued = true;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            _centerUpdateQueued = false;
            UpdateCenteredPadding();
        }), DispatcherPriority.Loaded);
    }

    private void UpdateCenteredPadding()
    {
        if (_isUpdatingPadding)
        {
            return;
        }

        var lineHeight = !double.IsNaN(_lineHeight) && _lineHeight > 0
            ? _lineHeight
            : Math.Max(FontSize * 1.35, FontSize + 4);
        var lineCount = EffectiveLineCount();
        var visibleLineHeight = VisibleLineHeight(lineHeight);
        var contentHeight = visibleLineHeight + Math.Max(0, lineCount - 1) * lineHeight;
        var targetHeight = Math.Max(_minimumLineBoxHeight, contentHeight + _basePadding.Top + _basePadding.Bottom);
        targetHeight = Math.Round(targetHeight, 1);
        var centeredPadding = Math.Max(_basePadding.Top, (targetHeight - contentHeight) / 2.0);
        centeredPadding = Math.Round(centeredPadding, 1);

        var next = new Thickness(_basePadding.Left, centeredPadding, _basePadding.Right, centeredPadding);
        if (Math.Abs(Height - targetHeight) < 0.05 &&
            Math.Abs(Padding.Left - next.Left) < 0.05 &&
            Math.Abs(Padding.Top - next.Top) < 0.05 &&
            Math.Abs(Padding.Right - next.Right) < 0.05 &&
            Math.Abs(Padding.Bottom - next.Bottom) < 0.05)
        {
            return;
        }

        _isUpdatingPadding = true;
        try
        {
            SetValue(TextBlock.LineHeightProperty, visibleLineHeight);
            Height = targetHeight;
            Padding = next;
            InvalidateVisual();
        }
        finally
        {
            _isUpdatingPadding = false;
        }
    }

    private int EffectiveLineCount()
    {
        try
        {
            if (LineCount > 0)
            {
                return LineCount;
            }
        }
        catch
        {
            // LineCount can be temporarily unavailable before the template finishes loading.
        }

        return Math.Max(1, Text.Count(ch => ch == '\n') + 1);
    }

    private double VisibleLineHeight(double lineHeight)
    {
        // WPF's TextBox lays glyphs inside the requested LineHeight box. For large custom
        // line spacing, using LineHeight as the visual text height makes a single-line todo
        // look top-aligned. Center the visible glyph block, while keeping LineHeight as the
        // distance between multiple lines.
        var fontVisualHeight = Math.Max(FontSize + 2, FontSize * 1.28);
        return Math.Min(lineHeight, fontVisualHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (!IsDone || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var y = Math.Max(ActualHeight / 2.0, 10);
        var lineColor = Theme.BrightWeakTextBrush is SolidColorBrush solid
            ? Color.FromArgb(205, solid.Color.R, solid.Color.G, solid.Color.B)
            : Color.FromArgb(205, 138, 122, 99);
        var pen = new Pen(new SolidColorBrush(lineColor), 1.35);
        pen.Freeze();

        drawingContext.DrawLine(
            pen,
            new Point(3, y),
            new Point(Math.Max(3, ActualWidth - 3), y));
    }
}
