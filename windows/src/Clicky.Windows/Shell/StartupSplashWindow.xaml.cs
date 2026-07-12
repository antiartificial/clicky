using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Clicky.Windows.Shell;

public partial class StartupSplashWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long NoActivateExtendedStyle = 0x08000000L;
    private const long ToolWindowExtendedStyle = 0x00000080L;

    private readonly bool animationsEnabled;
    private bool isClosing;

    public StartupSplashWindow()
    {
        InitializeComponent();
        animationsEnabled = SystemParameters.ClientAreaAnimation;
        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
    }

    public async Task CloseWithAnimationAsync()
    {
        if (isClosing)
        {
            return;
        }

        isClosing = true;
        if (!animationsEnabled)
        {
            Close();
            return;
        }

        var storyboard = (Storyboard)FindResource("OutroStoryboard");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleCompleted(object? sender, EventArgs eventArgs)
        {
            completion.TrySetResult();
        }

        void HandleClosed(object? sender, EventArgs eventArgs)
        {
            completion.TrySetResult();
        }

        storyboard.Completed += HandleCompleted;
        Closed += HandleClosed;
        storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: false);
        await completion.Task;
        storyboard.Completed -= HandleCompleted;
        Closed -= HandleClosed;
        if (IsVisible)
        {
            Close();
        }
    }

    private void HandleLoaded(object sender, RoutedEventArgs routedEventArgs)
    {
        if (animationsEnabled)
        {
            ((Storyboard)FindResource("IntroStoryboard")).Begin(
                this,
                HandoffBehavior.SnapshotAndReplace,
                isControllable: false);
            return;
        }

        Opacity = 1;
        SplashScale.ScaleX = 1;
        SplashScale.ScaleY = 1;
        FocusRing.Opacity = 0;
    }

    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetExtendedWindowStyle(handle, ExtendedWindowStyleIndex);
        NativeMethods.SetExtendedWindowStyle(
            handle,
            ExtendedWindowStyleIndex,
            extendedStyle | NoActivateExtendedStyle | ToolWindowExtendedStyle);
    }

    private static class NativeMethods
    {
        public static long GetExtendedWindowStyle(nint windowHandle, int index) =>
            nint.Size == 8
                ? GetWindowLongPtr64(windowHandle, index).ToInt64()
                : GetWindowLong32(windowHandle, index);

        public static void SetExtendedWindowStyle(nint windowHandle, int index, long value)
        {
            if (nint.Size == 8)
            {
                _ = SetWindowLongPtr64(windowHandle, index, new nint(value));
            }
            else
            {
                _ = SetWindowLong32(windowHandle, index, unchecked((int)value));
            }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(nint windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(nint windowHandle, int index, int newStyle);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint newStyle);
    }
}
