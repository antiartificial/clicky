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
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Clicky.Windows.Overlay;

internal sealed class PointCueWindow : Window
{
    private static readonly System.Windows.Media.Brush SignalLimeBrush = CreateBrush(0xB8, 0xE3, 0x4A);
    private static readonly System.Windows.Media.Brush CoralBrush = CreateBrush(0xFF, 0x6B, 0x5F);
    private static readonly System.Windows.Media.Brush WarmWhiteBrush = CreateBrush(0xF4, 0xF1, 0xE8);
    private static readonly System.Windows.Media.Brush GraphiteBrush = CreateBrush(0xF0, 0x17, 0x19, 0x1D);
    private static readonly System.Windows.Media.Brush GraphiteLineBrush = CreateBrush(0xF0, 0x3A, 0x3E, 0x45);

    private readonly Canvas surface;
    private readonly Grid target;
    private readonly Ellipse pulse;
    private readonly ScaleTransform pulseScale;
    private readonly Border labelSurface;
    private readonly TextBlock labelText;
    private readonly DispatcherTimer motionTimer;
    private HwndSource? hwndSource;
    private PointCueMotionPoint? currentCuePosition;
    private PointCueMotionPoint motionStart;
    private PointCueMotionPoint motionEnd;
    private PointCueLayout motionLayout;
    private nint motionWindowHandle;
    private long motionStartTimestamp;
    private TimeSpan motionDuration;

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
            Stroke = SignalLimeBrush,
            StrokeThickness = 1.5,
            Opacity = 0.25,
            RenderTransform = pulseScale,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5)
        };

        target = CreateTarget(pulse);
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

    public void ShowCue(PointCueLayout layout, string? label, bool motionEnabled)
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
        var start = wasVisible && currentCuePosition is { } currentPosition
            ? currentPosition
            : PointCueMotionPathCalculator.CreateRevealStart(
                destination,
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
        currentCuePosition = start;
        motionStart = start;
        motionEnd = destination;
        motionLayout = layout;
        motionWindowHandle = windowHandle;
        motionDuration = PointCueMotionPathCalculator.CalculateDuration(start, destination);
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

    private static Grid CreateTarget(Ellipse pulseElement)
    {
        var result = new Grid
        {
            Width = 32,
            Height = 32,
            IsHitTestVisible = false
        };

        result.Children.Add(pulseElement);
        result.Children.Add(new WpfRectangle
        {
            Width = 30,
            Height = 2,
            Fill = SignalLimeBrush,
            RadiusX = 1,
            RadiusY = 1,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        result.Children.Add(new WpfRectangle
        {
            Width = 2,
            Height = 30,
            Fill = SignalLimeBrush,
            RadiusX = 1,
            RadiusY = 1,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        result.Children.Add(new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = GraphiteBrush,
            Stroke = SignalLimeBrush,
            StrokeThickness = 2,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        result.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = CoralBrush,
            Stroke = WarmWhiteBrush,
            StrokeThickness = 1,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return result;
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
        var progress = elapsedSeconds / motionDuration.TotalSeconds;
        if (progress >= 1)
        {
            CompleteMotion();
            return;
        }

        var position = PointCueMotionPathCalculator.Calculate(motionStart, motionEnd, progress);
        PositionWindowAtCue(motionWindowHandle, motionLayout, position, showWindow: true);
        currentCuePosition = position;
    }

    private void CompleteMotion()
    {
        motionTimer.Stop();
        PositionWindow(motionWindowHandle, motionLayout, showWindow: true);
        currentCuePosition = motionEnd;
        StartPulse();
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
    }

    private void StopPulse()
    {
        pulse.BeginAnimation(OpacityProperty, null);
        pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

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
