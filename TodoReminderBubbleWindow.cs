using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PaperTodo;

public sealed class TodoReminderBubbleWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    public TodoReminderBubbleWindow(string title, string message, int durationSeconds, Action activate)
    {
        Width = 260;
        Height = 104;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;

        var border = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.Tint(150),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 11, 13, 11),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 3,
                Opacity = 0.24
            }
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        border.Child = root;

        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Theme.Tint((byte)(Theme.IsDark ? 48 : 32)),
            Margin = new Thickness(0, 1, 10, 0),
            Child = new TextBlock
            {
                Text = "!",
                Foreground = Theme.ActiveBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetColumn(icon, 0);
        root.Children.Add(icon);

        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        root.Children.Add(stack);

        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Theme.TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Theme.WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 48
        });

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds <= 0 ? 5 : durationSeconds, 1, 600))
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };

        Content = border;
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) => activate();
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => _closeTimer.Start();
        Loaded += (_, _) => _closeTimer.Start();
        Closed += (_, _) => _closeTimer.Stop();
    }

    public void PlaceNear(Rect anchor)
    {
        const double margin = 8;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;

        var preferLeft = anchor.Left + (anchor.Width / 2) > screenLeft + (screenWidth / 2);
        var left = preferLeft
            ? anchor.Left - Width - margin
            : anchor.Right + margin;
        var top = anchor.Top + Math.Min(8, Math.Max(0, (anchor.Height - Height) / 2));

        if (left < screenLeft + margin)
        {
            left = anchor.Left + margin;
        }
        if (left + Width > screenLeft + screenWidth - margin)
        {
            left = screenLeft + screenWidth - Width - margin;
        }
        if (top < screenTop + margin)
        {
            top = screenTop + margin;
        }
        if (top + Height > screenTop + screenHeight - margin)
        {
            top = screenTop + screenHeight - Height - margin;
        }

        Left = left;
        Top = top;
    }
}
