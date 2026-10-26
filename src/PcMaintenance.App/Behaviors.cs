using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PcMaintenance.App.Controls;

namespace PcMaintenance.App;

/// <summary>What an icon or panel does when its motion plays.</summary>
public enum MotionKind
{
    None,
    Pop,
    Pulse,
    Wiggle,
    Twist,
    Swing,
    Grow,
    Turn,
    Rewind,
    Squeeze,
    Bounce,
    Nudge,
    Reveal,
    Entrance,
}

/// <summary>
/// Short icon and page motions, played from a state change: the pointer arriving, a page being selected, a check box
/// being ticked. Only <see cref="SpinProperty"/> runs continuously, and only while its element is visible, so the tool
/// does not keep the processor busy while it sits idle. Nothing moves when Windows animation effects are turned off
/// or in a remote desktop session.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction Overshoot = Frozen(new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut });
    private static readonly KeySpline Smooth = Frozen(new KeySpline(0.3, 0, 0.3, 1));

    public static bool IsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(MotionKind), typeof(Motion), new PropertyMetadata(MotionKind.None));

    public static MotionKind GetKind(DependencyObject element) => (MotionKind)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, MotionKind value) => element.SetValue(KindProperty, value);

    /// <summary>Plays the motion each time this turns true, e.g. bound to IsMouseOver or IsSelected.</summary>
    public static readonly DependencyProperty PlayWhenProperty = DependencyProperty.RegisterAttached(
        "PlayWhen", typeof(object), typeof(Motion), new PropertyMetadata(null, (d, e) =>
        {
            if (e.NewValue is true) Play(d);
        }));

    public static object? GetPlayWhen(DependencyObject element) => element.GetValue(PlayWhenProperty);
    public static void SetPlayWhen(DependencyObject element, object? value) => element.SetValue(PlayWhenProperty, value);

    /// <summary>Plays the motion each time the bound value changes, e.g. the selected page; not for the first value.</summary>
    public static readonly DependencyProperty ReplayOnProperty = DependencyProperty.RegisterAttached(
        "ReplayOn", typeof(object), typeof(Motion), new PropertyMetadata(null, (d, e) =>
        {
            // Let the element's other bindings (such as Kind) take the new value first.
            if (e.OldValue is not null) d.Dispatcher.InvokeAsync(() => Play(d), DispatcherPriority.Render);
        }));

    public static object? GetReplayOn(DependencyObject element) => element.GetValue(ReplayOnProperty);
    public static void SetReplayOn(DependencyObject element, object? value) => element.SetValue(ReplayOnProperty, value);

    public static readonly DependencyProperty PlayOnLoadProperty = DependencyProperty.RegisterAttached(
        "PlayOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement element && e.NewValue is true) element.Loaded += PlayOnce;
        }));

    public static bool GetPlayOnLoad(DependencyObject element) => (bool)element.GetValue(PlayOnLoadProperty);
    public static void SetPlayOnLoad(DependencyObject element, bool value) => element.SetValue(PlayOnLoadProperty, value);

    /// <summary>Shrinks the element slightly while this is true, like a key being pressed.</summary>
    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement element || !IsEnabled) return;
            var pressed = e.NewValue is true;
            var (scale, _, _) = Transforms(element, new Point(0.5, 0.5));
            // Held down it stays small; let go, it grows back and the animation ends, leaving the resting size.
            var animation = new DoubleAnimation(pressed ? 0.96 : 1, TimeSpan.FromMilliseconds(pressed ? 70 : 160))
            {
                EasingFunction = EaseOut,
                FillBehavior = pressed ? FillBehavior.HoldEnd : FillBehavior.Stop,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }));

    public static bool GetPressed(DependencyObject element) => (bool)element.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject element, bool value) => element.SetValue(PressedProperty, value);

    /// <summary>Keeps turning the element while this is true and the element is on screen.</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement element) return;
            element.IsVisibleChanged -= OnSpinVisibilityChanged;
            if (e.NewValue is true) element.IsVisibleChanged += OnSpinVisibilityChanged;
            UpdateSpin(element);
        }));

    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);
    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);

    /// <summary>Moves a progress bar or ring to the bound value instead of jumping there.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(Motion), new PropertyMetadata(0.0, (d, e) =>
        {
            var property = d switch
            {
                RangeBase => RangeBase.ValueProperty,
                RingGauge => RingGauge.ValueProperty,
                _ => null,
            };
            if (property is null || d is not IAnimatable target) return;
            // The new figure becomes the base value; the animation only covers the way there and then ends.
            var from = (double)d.GetValue(property);
            d.SetValue(property, (double)e.NewValue);
            if (IsEnabled) FromTo(target, property, from, (double)e.NewValue, 650, EaseOut);
        }));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    private static void PlayOnce(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= PlayOnce;
        Play(element);
    }

    // Only on screen and only for changes the user sees: a page full of ticked boxes should not all pop as it opens.
    private static void Play(DependencyObject d)
    {
        if (d is FrameworkElement { IsLoaded: true } element && IsEnabled) Run(element, GetKind(element));
    }

    private static void Run(UIElement element, MotionKind kind)
    {
        if (kind == MotionKind.None) return;
        var (scale, rotate, move) = Transforms(element, kind == MotionKind.Grow ? new Point(0.5, 1) : new Point(0.5, 0.5));
        switch (kind)
        {
            case MotionKind.Pop:
                FromTo(scale, ScaleTransform.ScaleXProperty, 0.6, 1, 420, Overshoot);
                FromTo(scale, ScaleTransform.ScaleYProperty, 0.6, 1, 420, Overshoot);
                break;
            case MotionKind.Pulse:
                Keys(scale, ScaleTransform.ScaleXProperty, 420, (0.35, 1.2), (1, 1));
                Keys(scale, ScaleTransform.ScaleYProperty, 420, (0.35, 1.2), (1, 1));
                break;
            case MotionKind.Wiggle:
                Keys(rotate, RotateTransform.AngleProperty, 620, (0.18, -18), (0.4, 14), (0.6, -9), (0.8, 4), (1, 0));
                break;
            case MotionKind.Twist:
                Keys(rotate, RotateTransform.AngleProperty, 560, (0.3, -35), (0.65, 12), (1, 0));
                break;
            case MotionKind.Swing:
                Keys(rotate, RotateTransform.AngleProperty, 700, (0.25, 28), (0.55, -14), (0.8, 6), (1, 0));
                break;
            case MotionKind.Grow:
                FromTo(scale, ScaleTransform.ScaleYProperty, 0.3, 1, 460, Overshoot);
                break;
            case MotionKind.Turn:
                FromTo(rotate, RotateTransform.AngleProperty, 0, 360, 650, EaseInOut);
                break;
            case MotionKind.Rewind:
                FromTo(rotate, RotateTransform.AngleProperty, 0, -360, 700, EaseInOut);
                break;
            case MotionKind.Squeeze:
                Keys(scale, ScaleTransform.ScaleXProperty, 480, (0.3, 1.22), (0.6, 0.92), (1, 1));
                Keys(scale, ScaleTransform.ScaleYProperty, 480, (0.3, 0.8), (0.6, 1.06), (1, 1));
                break;
            case MotionKind.Bounce:
                Keys(move, TranslateTransform.YProperty, 560, (0.25, -6), (0.5, 0), (0.72, -2.5), (1, 0));
                break;
            case MotionKind.Nudge:
                Keys(move, TranslateTransform.XProperty, 360, (0.4, 3), (1, 0));
                break;
            case MotionKind.Reveal:
                FromTo(scale, ScaleTransform.ScaleXProperty, 0.15, 1, 300, EaseOut);
                break;
            case MotionKind.Entrance:
                FromTo(element, UIElement.OpacityProperty, 0, 1, 220, EaseOut);
                FromTo(move, TranslateTransform.YProperty, 10, 0, 320, EaseOut);
                break;
        }
    }

    private static void OnSpinVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateSpin((UIElement)sender);

    private static readonly DependencyProperty SpinClockProperty = DependencyProperty.RegisterAttached(
        "SpinClock", typeof(AnimationClock), typeof(Motion));

    private static void UpdateSpin(UIElement element)
    {
        var (_, rotate, _) = Transforms(element, new Point(0.5, 0.5));
        var clock = (AnimationClock?)element.GetValue(SpinClockProperty);
        var run = GetSpin(element) && element.IsVisible && IsEnabled;
        if (run == clock is not null) return;
        if (run)
        {
            clock = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock();
            rotate.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
            element.SetValue(SpinClockProperty, clock);
            return;
        }
        // Taking a clock off the property leaves it running, and WPF would keep drawing frames for it; stop it first.
        clock!.Controller!.Stop();
        rotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        element.ClearValue(SpinClockProperty);
    }

    // Each moving element gets its own scale, rotation and offset, so motions never share a frozen transform from a style.
    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Move) Transforms(UIElement element, Point origin)
    {
        element.RenderTransformOrigin = origin;
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform scale, RotateTransform rotate, TranslateTransform move] })
            return (scale, rotate, move);
        var group = new TransformGroup();
        var parts = (new ScaleTransform(), new RotateTransform(), new TranslateTransform());
        group.Children.Add(parts.Item1);
        group.Children.Add(parts.Item2);
        group.Children.Add(parts.Item3);
        element.RenderTransform = group;
        return parts;
    }

    private static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds, IEasingFunction easing) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        });

    // Key frames end on the resting value, so letting go of the animation afterwards leaves the element as it was.
    private static void Keys(IAnimatable target, DependencyProperty property, int milliseconds, params (double At, double Value)[] keys)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.Stop,
        };
        foreach (var (at, value) in keys) animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromPercent(at), Smooth));
        target.BeginAnimation(property, animation);
    }

    private static T Frozen<T>(T value) where T : class
    {
        if (value is Freezable freezable) freezable.Freeze();
        return value;
    }
}

/// <summary>An icon for buttons, tab headers and text boxes, drawn by their templates in Styles.xaml.</summary>
public static class Glyph
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Glyph), new PropertyMetadata(null));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>What the icon does when the pointer arrives.</summary>
    public static readonly DependencyProperty MotionProperty = DependencyProperty.RegisterAttached(
        "Motion", typeof(MotionKind), typeof(Glyph), new PropertyMetadata(MotionKind.Pop));

    public static MotionKind GetMotion(DependencyObject element) => (MotionKind)element.GetValue(MotionProperty);
    public static void SetMotion(DependencyObject element, MotionKind value) => element.SetValue(MotionProperty, value);

    /// <summary>Shows a turning ring in place of the icon, e.g. while the button's own command runs.</summary>
    public static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached(
        "Busy", typeof(bool), typeof(Glyph), new PropertyMetadata(false));

    public static bool GetBusy(DependencyObject element) => (bool)element.GetValue(BusyProperty);
    public static void SetBusy(DependencyObject element, bool value) => element.SetValue(BusyProperty, value);
}

/// <summary>Clips an element to rounded corners, so a list filling a card does not paint over the card's corners.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is not FrameworkElement element) return;
            element.SizeChanged -= OnSizeChanged;
            if ((double)e.NewValue > 0) element.SizeChanged += OnSizeChanged;
            Apply(element);
        }));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        element.Clip = radius > 0 ? new RectangleGeometry(new Rect(element.RenderSize), radius, radius) : null;
    }
}
