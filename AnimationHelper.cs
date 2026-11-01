using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

public static class AnimationHelper
{
    public static readonly IEasingFunction SmoothEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction QuickEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction SnapEase = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };

    // 确保元素有 RenderTransform（TransformGroup 包含 ScaleTransform 和 TranslateTransform）
    public static void EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is TransformGroup group &&
            group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform &&
            group.Children[1] is TranslateTransform)
        {
            return;
        }

        var existingTransform = element.RenderTransform;
        var transforms = new TransformCollection
        {
            new ScaleTransform(1, 1),
            new TranslateTransform(0, 0)
        };
        if (existingTransform != null && !ReferenceEquals(existingTransform, Transform.Identity))
        {
            transforms.Add(existingTransform);
        }
        element.RenderTransform = new TransformGroup
        {
            Children = transforms
        };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    public static ScaleTransform GetScaleTransform(UIElement element)
    {
        EnsureTransform(element);
        return ((TransformGroup)element.RenderTransform).Children[0] as ScaleTransform
               ?? new ScaleTransform(1, 1);
    }

    public static TranslateTransform GetTranslateTransform(UIElement element)
    {
        EnsureTransform(element);
        return ((TransformGroup)element.RenderTransform).Children[1] as TranslateTransform
               ?? new TranslateTransform(0, 0);
    }

    // 淡入
    public static void FadeIn(UIElement element, double duration = 200, EventHandler? onComplete = null)
    {
        var anim = new DoubleAnimation(element.Opacity, 1, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = QuickEase
        };
        if (onComplete != null) anim.Completed += onComplete;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // 淡出
    public static void FadeOut(UIElement element, double duration = 150, EventHandler? onComplete = null)
    {
        var anim = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = QuickEase
        };
        if (onComplete != null) anim.Completed += onComplete;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // 缩放到指定比例
    public static void ScaleTo(UIElement element, double scale, double duration = 200, IEasingFunction? easing = null)
    {
        var transform = GetScaleTransform(element);
        var anim = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing ?? SmoothEase
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // 平移到指定位置
    public static void TranslateTo(UIElement element, double x, double y, double duration = 200, IEasingFunction? easing = null, EventHandler? onComplete = null)
    {
        var transform = GetTranslateTransform(element);
        var animX = new DoubleAnimation(x, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing ?? SmoothEase
        };
        var animY = new DoubleAnimation(y, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing ?? SmoothEase
        };
        if (onComplete != null) animY.Completed += onComplete;

        transform.BeginAnimation(TranslateTransform.XProperty, animX);
        transform.BeginAnimation(TranslateTransform.YProperty, animY);
    }

    // 颜色过渡
    public static void TransitionColor(Brush brush, Color toColor, double duration = 300)
    {
        if (brush is not SolidColorBrush solidBrush) return;

        var anim = new ColorAnimation(toColor, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = SmoothEase
        };
        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // 取消所有动画
    public static void StopAllAnimations(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TransformGroup group)
        {
            foreach (var transform in group.Children)
            {
                if (transform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
                else if (transform is TranslateTransform tt)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                }
            }
        }
    }

    // 快速弹跳（用于强调）
    public static void QuickBounce(UIElement element, double scale = 1.03, double duration = 100)
    {
        var transform = GetScaleTransform(element);
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = scale,
            Duration = TimeSpan.FromMilliseconds(duration),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // 闪烁高亮（用于撤销提示）
    public static void FlashHighlight(Border element, Color highlightColor, double duration = 120)
    {
        var originalBg = element.Background;
        var highlightBrush = new SolidColorBrush(Colors.Transparent);
        element.Background = highlightBrush;

        var flashAnim = new ColorAnimation
        {
            From = Colors.Transparent,
            To = Color.FromArgb((byte)(highlightColor.A * 0.4), highlightColor.R, highlightColor.G, highlightColor.B),
            Duration = TimeSpan.FromMilliseconds(duration),
            AutoReverse = true,
            EasingFunction = new QuadraticEase()
        };
        flashAnim.Completed += (s, e) => element.Background = originalBg;

        highlightBrush.BeginAnimation(SolidColorBrush.ColorProperty, flashAnim);
    }
}
