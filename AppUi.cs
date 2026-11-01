using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

public static class AppUi
{
    public const double RadiusSmall = 3;
    public const double RadiusControl = 3;
    public const double RadiusBlock = 6;
    public const double RadiusPanel = 6;
    public const double RadiusShell = 6;

    public static CornerRadius SmallRadius => new(RadiusSmall);
    public static CornerRadius ControlRadius => new(RadiusControl);
    public static CornerRadius BlockRadius => new(RadiusBlock);
    public static CornerRadius PanelRadius => new(RadiusPanel);
    public static CornerRadius ShellRadius => new(RadiusShell);

    public static Thickness PaperChromePadding => new(0);
    public static Thickness SettingsPadding => new(16, 14, 16, 16);
    public static Thickness TodoRowPadding => new(4, 2.5, 4, 2.5);

    public static Style ButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Theme.SurfaceBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.PaperBorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));

        var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
        border.SetValue(Border.CornerRadiusProperty, ControlRadius);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var overlay = new FrameworkElementFactory(typeof(Border), "Overlay");
        overlay.SetValue(Border.CornerRadiusProperty, ControlRadius);
        overlay.SetValue(Border.BackgroundProperty, Theme.HoverBrush);
        overlay.SetValue(UIElement.OpacityProperty, 0.0);
        grid.AppendChild(overlay);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        grid.AppendChild(content);
        var focusRing = new FrameworkElementFactory(typeof(Border), "FocusRing");
        focusRing.SetValue(Border.CornerRadiusProperty, ControlRadius);
        focusRing.SetValue(Border.BorderBrushProperty, Theme.ActiveBrush);
        focusRing.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        focusRing.SetValue(UIElement.IsHitTestVisibleProperty, false);
        focusRing.SetValue(UIElement.OpacityProperty, 0.0);
        grid.AppendChild(focusRing);
        border.AppendChild(grid);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "Overlay"));
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75, "ButtonBorder"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "FocusRing"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        template.Triggers.Add(focus);
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    public static System.Windows.Shapes.Path LineIcon(string data, Brush foreground, double size = 16)
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Stroke = foreground,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    public static DropShadowEffect PaperShadow()
    {
        return new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 2,
            Direction = 270,
            Color = Colors.Black,
            Opacity = Theme.IsDark ? 0.32 : 0.12
        };
    }

    public static DropShadowEffect FloatingShadow()
    {
        return new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Direction = 270,
            Color = Colors.Black,
            Opacity = Theme.IsDark ? 0.36 : 0.16
        };
    }

    public static DropShadowEffect SettingsShadow()
    {
        return new DropShadowEffect
        {
            BlurRadius = 24,
            ShadowDepth = 4,
            Direction = 270,
            Color = Colors.Black,
            Opacity = Theme.IsDark ? 0.40 : 0.20
        };
    }

    public static DropShadowEffect NoteCanvasElementShadow(int layerRank, int layerCount, bool active)
    {
        var normalized = Math.Clamp((double)Math.Max(1, layerRank) / Math.Max(1, layerCount), 0.0, 1.0);
        return new DropShadowEffect
        {
            BlurRadius = active ? 24 : 10 + (normalized * 10),
            ShadowDepth = active ? 5 : 1.5 + (normalized * 3.5),
            Direction = 315,
            Opacity = active
                ? (Theme.IsDark ? 0.52 : 0.30)
                : (Theme.IsDark ? 0.24 + (normalized * 0.14) : 0.14 + (normalized * 0.12))
        };
    }
}
