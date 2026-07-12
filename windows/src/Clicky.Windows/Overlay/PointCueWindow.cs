using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfPoint = System.Windows.Point;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Overlay;

internal sealed class PointCueWindow : Window
{
    private static readonly TimeSpan ArrivalOrbitDuration = TimeSpan.FromMilliseconds(300);
    private static readonly System.Windows.Media.Brush GuideBlueBrush = CreateBrush(0x3D, 0x8B, 0xFF);
    private static readonly System.Windows.Media.Brush GuideCyanBrush = CreateBrush(0x78, 0xDC, 0xFF);
    private static readonly System.Windows.Media.Brush CoralBrush = CreateBrush(0xFF, 0x6B, 0x5F);
    private static readonly System.Windows.Media.Brush WarmWhiteBrush = CreateBrush(0xF4, 0xF1, 0xE8);
    private static readonly System.Windows.Media.Brush GraphiteBrush = CreateBrush(0xF0, 0x17, 0x19, 0x1D);
    private static readonly System.Windows.Media.Brush GraphiteLineBrush = CreateBrush(0xF0, 0x3A, 0x3E, 0x45);

    private readonly Canvas surface;
    private readonly Grid target;
    private readonly Ellipse pulse;
    private readonly ScaleTransform pulseScale;
    private readonly Canvas character;
    private readonly ScaleTransform characterScale;
    private readonly TranslateTransform characterTranslate;
    private readonly Ellipse twinkle;
    private readonly ScaleTransform twinkleScale;
    private readonly Border labelSurface;
    private readonly TextBlock labelText;
    private readonly DispatcherTimer motionTimer;
    private HwndSource? hwndSource;
    private PointCueMotionPoint? currentCuePosition;
    private PointCueMotionPoint motionStart;
    private PointCueMotionPoint motionApproachEnd;
    private PointCueMotionPoint motionEnd;
    private PointCueLayout motionLayout;
    private nint motionWindowHandle;
    private long motionStartTimestamp;
    private TimeSpan motionDuration;
    private bool isArrivalOrbitActive;

    public PointCueWindow()
    {
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Left = -32_000;
        Top = -32_000;

        surface = new Canvas
        {
            Background = WpfBrushes.Transparent,
            ClipToBounds = true,
            IsHitTestVisible = false
        };

        pulseScale = new ScaleTransform(1, 1);
        pulse = new Ellipse
        {
            Width = 28,
            Height = 28,
            Stroke = GuideCyanBrush,
            StrokeThickness = 2,
            Opacity = 0.42,
            RenderTransform = pulseScale,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5)
        };

        var targetVisual = CreateClickletTarget(pulse);
        target = targetVisual.Target;
        character = targetVisual.Character;
        characterScale = targetVisual.CharacterScale;
        characterTranslate = targetVisual.CharacterTranslate;
        twinkle = targetVisual.Twinkle;
        twinkleScale = targetVisual.TwinkleScale;
        labelText = new TextBlock
        {
            Foreground = WarmWhiteBrush,
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = PointCueLayoutCalculator.LabelMaximumWidthDip - 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelSurface = new Border
        {
            Height = 24,
            MaxWidth = PointCueLayoutCalculator.LabelMaximumWidthDip,
            Padding = new Thickness(7, 2, 7, 2),
            Background = GraphiteBrush,
            BorderBrush = GraphiteLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = labelText,
            Visibility = Visibility.Collapsed
        };

        surface.Children.Add(target);
        surface.Children.Add(labelSurface);
        Content = surface;

        motionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        motionTimer.Tick += MotionTimerTick;
    }

    public void ShowCue(
        PointCueLayout layout,
        string? label,
        bool motionEnabled,
        DesktopPoint? motionOrigin = null)
    {
        var windowHandle = new WindowInteropHelper(this).EnsureHandle();
        var wasVisible = IsVisible;

        StopMotion();
        StopPulse();

        Width = layout.WindowWidthDip;
        Height = layout.WindowHeightDip;
        surface.Width = layout.WindowWidthDip;
        surface.Height = layout.WindowHeightDip;

        Canvas.SetLeft(target, layout.CueCenterXDip - (target.Width / 2));
        Canvas.SetTop(target, layout.CueCenterYDip - (target.Height / 2));
        ApplyLabel(layout, label);

        var destination = GetCuePosition(layout);
        var configuredOrigin = motionOrigin is { } origin
            ? new PointCueMotionPoint(origin.X, origin.Y)
            : (PointCueMotionPoint?)null;
        var start = PointCueMotionPathCalculator.ResolveStart(
            destination,
            currentCuePosition,
            configuredOrigin,
            wasVisible,
            layout.DpiScaleX,
            layout.DpiScaleY);

        if (!motionEnabled || start == destination)
        {
            PositionWindow(windowHandle, layout, showWindow: false);
            if (!wasVisible)
            {
                Show();
            }

            PositionWindow(windowHandle, layout, showWindow: true);
            currentCuePosition = destination;
            if (motionEnabled)
            {
                StartPulse();
                PlayLanding();
            }
            else
            {
                SetStaticPulse();
            }

            return;
        }

        PositionWindowAtCue(windowHandle, layout, start, showWindow: false);
        if (!wasVisible)
        {
            Show();
        }

        PositionWindowAtCue(windowHandle, layout, start, showWindow: true);
        if (!wasVisible)
        {
            PlayReveal();
        }

        currentCuePosition = start;
        motionStart = start;
        motionEnd = destination;
        motionLayout = layout;
        motionWindowHandle = windowHandle;
        var approachEnd = PointCueMotionPathCalculator.CalculateArrivalOrbit(
            destination,
            progress: 0,
            dpiScaleX: layout.DpiScaleX,
            dpiScaleY: layout.DpiScaleY);
        motionDuration = PointCueMotionPathCalculator.CalculateDuration(start, approachEnd);
        motionApproachEnd = approachEnd;
        isArrivalOrbitActive = false;
        motionStartTimestamp = Stopwatch.GetTimestamp();
        motionTimer.Start();
    }

    public void HideCue()
    {
        StopMotion();
        StopPulse();
        Hide();
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);

        var windowHandle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            windowHandle,
            NativeMethods.ExtendedWindowStyleIndex).ToInt64();
        extendedStyle |= NativeMethods.ExtendedStyleNoActivate |
            NativeMethods.ExtendedStyleTransparent |
            NativeMethods.ExtendedStyleToolWindow;
        _ = NativeMethods.SetWindowLongPtr(
            windowHandle,
            NativeMethods.ExtendedWindowStyleIndex,
            new nint(extendedStyle));

        hwndSource = HwndSource.FromHwnd(windowHandle);
        hwndSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        StopMotion();
        StopPulse();

        if (hwndSource is not null)
        {
            hwndSource.RemoveHook(WindowMessageHook);
            hwndSource = null;
        }

        base.OnClosed(eventArgs);
    }

    private static ClickletVisual CreateClickletTarget(Ellipse pulseElement)
    {
        var result = new Grid
        {
            Width = 32,
            Height = 32,
            IsHitTestVisible = false
        };

        result.Children.Add(pulseElement);

        var characterScale = new ScaleTransform(1, 1);
        var characterTranslate = new TranslateTransform();
        var characterTransform = new TransformGroup();
        characterTransform.Children.Add(characterScale);
        characterTransform.Children.Add(characterTranslate);
        var character = new Canvas
        {
            Width = 32,
            Height = 32,
            RenderTransform = characterTransform,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            IsHitTestVisible = false
        };

        var antenna = new Line
        {
            X1 = 9,
            Y1 = 5,
            X2 = 7,
            Y2 = 1.5,
            Stroke = GuideCyanBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        character.Children.Add(antenna);
        var antennaDot = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = CoralBrush
        };
        Canvas.SetLeft(antennaDot, 5);
        Canvas.SetTop(antennaDot, 0);
        character.Children.Add(antennaDot);

        character.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(
                "M 4,7 C 4,3 7,2 11,2 L 17,3 C 21,4 23,7 22,10 " +
                "C 22,13 20,15 17,16 L 16,19 L 13,16 L 9,16 " +
                "C 5,16 3,13 3,10 C 3,9 3.4,8 4,7 Z"),
            Fill = GuideBlueBrush,
            Stroke = GraphiteBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        });

        AddEye(character, left: 8, top: 7);
        AddEye(character, left: 15, top: 7.5);

        character.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 10,13 C 12,14.5 14,14.5 16,13"),
            Stroke = CoralBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        var twinkleScale = new ScaleTransform(0.6, 0.6);
        var twinkle = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = WarmWhiteBrush,
            Stroke = GuideCyanBrush,
            StrokeThickness = 1,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Opacity = 0,
            RenderTransform = twinkleScale,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5)
        };

        result.Children.Add(character);
        result.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = GraphiteBrush,
            Stroke = GuideCyanBrush,
            StrokeThickness = 1.5,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        result.Children.Add(new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = CoralBrush,
            Stroke = WarmWhiteBrush,
            StrokeThickness = 1,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        result.Children.Add(twinkle);

        return new ClickletVisual(
            result,
            character,
            characterScale,
            characterTranslate,
            twinkle,
            twinkleScale);
    }

    private static void AddEye(Canvas character, double left, double top)
    {
        var eye = new Ellipse
        {
            Width = 3.5,
            Height = 4.5,
            Fill = GraphiteBrush,
            Stroke = WarmWhiteBrush,
            StrokeThickness = 1
        };
        Canvas.SetLeft(eye, left);
        Canvas.SetTop(eye, top);
        character.Children.Add(eye);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(WpfColor.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void ApplyLabel(PointCueLayout layout, string? label)
    {
        labelText.Text = label;
        labelSurface.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;

        Canvas.SetLeft(labelSurface, double.NaN);
        Canvas.SetRight(labelSurface, double.NaN);
        Canvas.SetTop(labelSurface, (layout.WindowHeightDip - labelSurface.Height) / 2);

        if (layout.LabelPlacement == PointCueLabelPlacement.Right)
        {
            Canvas.SetLeft(
                labelSurface,
                PointCueLayoutCalculator.CueRegionDip + PointCueLayoutCalculator.LabelGapDip);
        }
        else if (layout.LabelPlacement == PointCueLabelPlacement.Left)
        {
            Canvas.SetRight(
                labelSurface,
                PointCueLayoutCalculator.CueRegionDip + PointCueLayoutCalculator.LabelGapDip);
        }
    }

    private void MotionTimerTick(object? sender, EventArgs eventArgs)
    {
        var elapsedSeconds = (Stopwatch.GetTimestamp() - motionStartTimestamp) /
            (double)Stopwatch.Frequency;
        var progress = elapsedSeconds /
            (isArrivalOrbitActive ? ArrivalOrbitDuration.TotalSeconds : motionDuration.TotalSeconds);
        if (progress >= 1)
        {
            if (isArrivalOrbitActive)
            {
                CompleteMotion();
            }
            else
            {
                BeginArrivalOrbit();
            }

            return;
        }

        var position = isArrivalOrbitActive
            ? PointCueMotionPathCalculator.CalculateArrivalOrbit(
                motionEnd,
                progress,
                motionLayout.DpiScaleX,
                motionLayout.DpiScaleY)
            : PointCueMotionPathCalculator.Calculate(motionStart, motionApproachEnd, progress);
        PositionWindowAtCue(motionWindowHandle, motionLayout, position, showWindow: true);
        currentCuePosition = position;
    }

    private void BeginArrivalOrbit()
    {
        PositionWindowAtCue(
            motionWindowHandle,
            motionLayout,
            motionApproachEnd,
            showWindow: true);
        currentCuePosition = motionApproachEnd;
        isArrivalOrbitActive = true;
        motionStartTimestamp = Stopwatch.GetTimestamp();
    }

    private void CompleteMotion()
    {
        motionTimer.Stop();
        isArrivalOrbitActive = false;
        PositionWindow(motionWindowHandle, motionLayout, showWindow: true);
        currentCuePosition = motionEnd;
        StartPulse();
        PlayLanding();
    }

    private void StartPulse()
    {
        StopPulse();

        var duration = new Duration(TimeSpan.FromMilliseconds(900));
        var scaleAnimation = new DoubleAnimation(0.92, 1.12, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var opacityAnimation = new DoubleAnimation(0.32, 0.08, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
        pulse.BeginAnimation(OpacityProperty, opacityAnimation);

        characterTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, -1.2, new Duration(TimeSpan.FromMilliseconds(720)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

        var twinkleOpacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(1600)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        twinkleOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        twinkleOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.16)));
        twinkleOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.34)));
        twinkleOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        twinkle.BeginAnimation(OpacityProperty, twinkleOpacity);

        var twinkleScaleAnimation = new DoubleAnimation(0.55, 1.15, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        twinkleScale.BeginAnimation(ScaleTransform.ScaleXProperty, twinkleScaleAnimation);
        twinkleScale.BeginAnimation(ScaleTransform.ScaleYProperty, twinkleScaleAnimation.Clone());
    }

    private void PlayReveal()
    {
        Opacity = 1;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });

        var auraScale = new DoubleAnimation(0.68, 1.45, TimeSpan.FromMilliseconds(190))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, auraScale);
        pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, auraScale.Clone());
        pulse.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.95, 0.3, TimeSpan.FromMilliseconds(190))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    private void PlayLanding()
    {
        var landing = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(260))
        };
        landing.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.78,
            KeyTime.FromTimeSpan(TimeSpan.Zero),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        landing.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.12,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }));
        landing.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        characterScale.BeginAnimation(ScaleTransform.ScaleXProperty, landing);
        characterScale.BeginAnimation(ScaleTransform.ScaleYProperty, landing.Clone());
    }

    private void SetStaticPulse()
    {
        StopPulse();
        pulse.Opacity = 0.28;
        pulseScale.ScaleX = 1;
        pulseScale.ScaleY = 1;
    }

    private void StopMotion()
    {
        motionTimer.Stop();
        isArrivalOrbitActive = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void StopPulse()
    {
        pulse.BeginAnimation(OpacityProperty, null);
        pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        characterScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        characterScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        characterTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        twinkle.BeginAnimation(OpacityProperty, null);
        twinkleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        twinkleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        characterScale.ScaleX = 1;
        characterScale.ScaleY = 1;
        characterTranslate.Y = 0;
        twinkle.Opacity = 0;
        twinkleScale.ScaleX = 0.6;
        twinkleScale.ScaleY = 0.6;
    }

    private readonly record struct ClickletVisual(
        Grid Target,
        Canvas Character,
        ScaleTransform CharacterScale,
        TranslateTransform CharacterTranslate,
        Ellipse Twinkle,
        ScaleTransform TwinkleScale);

    private static void PositionWindow(nint windowHandle, PointCueLayout layout, bool showWindow)
    {
        PositionWindow(
            windowHandle,
            layout,
            layout.WindowLeftPixels,
            layout.WindowTopPixels,
            showWindow);
    }

    private static void PositionWindowAtCue(
        nint windowHandle,
        PointCueLayout layout,
        PointCueMotionPoint cuePosition,
        bool showWindow)
    {
        var cueOffsetXPixels = layout.CueCenterXDip * layout.DpiScaleX;
        var cueOffsetYPixels = layout.CueCenterYDip * layout.DpiScaleY;
        PositionWindow(
            windowHandle,
            layout,
            RoundToNativePixel(cuePosition.X - cueOffsetXPixels),
            RoundToNativePixel(cuePosition.Y - cueOffsetYPixels),
            showWindow);
    }

    private static void PositionWindow(
        nint windowHandle,
        PointCueLayout layout,
        int leftPixels,
        int topPixels,
        bool showWindow)
    {
        var flags = NativeMethods.SetWindowPositionNoActivate;
        if (showWindow)
        {
            flags |= NativeMethods.SetWindowPositionShowWindow;
        }

        if (!NativeMethods.SetWindowPos(
                windowHandle,
                NativeMethods.HwndTopmost,
                leftPixels,
                topPixels,
                layout.WindowWidthPixels,
                layout.WindowHeightPixels,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position the point cue window.");
        }
    }

    private static PointCueMotionPoint GetCuePosition(PointCueLayout layout)
    {
        return new PointCueMotionPoint(
            layout.WindowLeftPixels + (layout.CueCenterXDip * layout.DpiScaleX),
            layout.WindowTopPixels + (layout.CueCenterYDip * layout.DpiScaleY));
    }

    private static int RoundToNativePixel(double value)
    {
        return (int)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue);
    }

    private static nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == NativeMethods.WindowMessageNonClientHitTest)
        {
            handled = true;
            return new nint(NativeMethods.HitTestTransparent);
        }

        if (message == NativeMethods.WindowMessageMouseActivate)
        {
            handled = true;
            return new nint(NativeMethods.MouseActivateNoActivate);
        }

        return nint.Zero;
    }
}
