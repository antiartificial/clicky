using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicky.Windows.Providers;
using Clicky.Windows.ViewModels;

namespace Clicky.Windows.Shell;

public partial class CompanionWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long NoActivateExtendedStyle = 0x08000000L;
    private const long ToolWindowExtendedStyle = 0x00000080L;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const double DefaultDpi = 96;
    private const double WorkAreaMargin = 14;

    private readonly CompanionViewModel viewModel;
    private nint windowHandle;
    private bool isPermanentCloseRequested;

    public CompanionWindow(CompanionViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
        SyncWindowHeight();
        viewModel.QuestionEntryReady += HandleQuestionEntryReady;
        viewModel.CompactStateRequested += HandleCompactStateRequested;
        viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        SourceInitialized += HandleSourceInitialized;
        Closing += HandleClosing;
    }

    public void ShowNearPrimaryWorkArea()
    {
        windowHandle = new WindowInteropHelper(this).EnsureHandle();
        PositionNearPrimaryWorkArea();

        Show();
        ApplyNonActivatingStyle(shouldPreventActivation: !IsInteractivePaneVisible);
    }

    public void ShowSettings()
    {
        viewModel.SetSettingsVisible(true);
        SyncWindowHeight();
        ShowNearPrimaryWorkArea();
        ApplyNonActivatingStyle(shouldPreventActivation: false);
        Focusable = true;
        Activate();
    }

    public void HideCompanion()
    {
        viewModel.CancelCurrentInteraction();
        viewModel.SetSettingsVisible(false);
        viewModel.SetConversationVisible(false);
        SyncWindowHeight();
        Focusable = false;
        ApplyNonActivatingStyle(shouldPreventActivation: true);
        Hide();
    }

    public void ClosePermanently()
    {
        isPermanentCloseRequested = true;
        Close();
    }

    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        windowHandle = new WindowInteropHelper(this).Handle;
        ApplyNonActivatingStyle(shouldPreventActivation: true);
    }

    private void HandleClosing(object? sender, CancelEventArgs cancelEventArgs)
    {
        if (isPermanentCloseRequested)
        {
            viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
            return;
        }

        cancelEventArgs.Cancel = true;
        HideCompanion();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        if (mouseButtonEventArgs.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        var shouldShowSettings = !viewModel.IsSettingsVisible;
        viewModel.SetSettingsVisible(shouldShowSettings);
        if (shouldShowSettings && !viewModel.IsSettingsVisible)
        {
            return;
        }

        SyncWindowHeight();

        PositionNearPrimaryWorkArea();
        Focusable = shouldShowSettings;
        ApplyNonActivatingStyle(shouldPreventActivation: !shouldShowSettings);

        if (shouldShowSettings)
        {
            Activate();
        }
        else if (!viewModel.IsQuestionEntryVisible && !viewModel.IsConversationVisible)
        {
            Focusable = false;
        }
    }

    private void ConversationButton_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        var shouldShowConversation = !viewModel.IsConversationVisible;
        viewModel.SetConversationVisible(shouldShowConversation);
        SyncWindowHeight();
        PositionNearPrimaryWorkArea();
        Focusable = shouldShowConversation;
        ApplyNonActivatingStyle(shouldPreventActivation: !shouldShowConversation);

        if (shouldShowConversation)
        {
            Activate();
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                ConversationScrollViewer.ScrollToEnd);
        }
        else if (!viewModel.IsQuestionEntryVisible)
        {
            Focusable = false;
        }
    }

    private async void WorkerProviderButton_Click(object sender, RoutedEventArgs routedEventArgs) =>
        await SelectProviderAsync(AiProviderKind.Worker);

    private async void AnthropicProviderButton_Click(object sender, RoutedEventArgs routedEventArgs) =>
        await SelectProviderAsync(AiProviderKind.Anthropic);

    private async void OpenAIProviderButton_Click(object sender, RoutedEventArgs routedEventArgs) =>
        await SelectProviderAsync(AiProviderKind.OpenAI);

    private async Task SelectProviderAsync(AiProviderKind provider)
    {
        ProviderApiKeyPasswordBox.Clear();
        await viewModel.SelectProviderAsync(provider);
    }

    private async void SaveApiKeyButton_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        var securePassword = ProviderApiKeyPasswordBox.SecurePassword;
        try
        {
            var apiKey = new NetworkCredential(string.Empty, securePassword).Password;
            await viewModel.SaveSelectedProviderApiKeyAsync(apiKey);
        }
        finally
        {
            ProviderApiKeyPasswordBox.Clear();
            securePassword.Dispose();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        HideCompanion();
    }

    private void QuestionTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs keyEventArgs)
    {
        if (keyEventArgs.Key != Key.Enter ||
            !viewModel.SubmitQuestionCommand.CanExecute(parameter: null))
        {
            return;
        }

        keyEventArgs.Handled = true;
        viewModel.SubmitQuestionCommand.Execute(parameter: null);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs keyEventArgs)
    {
        if (keyEventArgs.Key != Key.Escape ||
            !viewModel.CancelSessionCommand.CanExecute(parameter: null))
        {
            return;
        }

        keyEventArgs.Handled = true;
        viewModel.CancelSessionCommand.Execute(parameter: null);
    }

    private void HandleQuestionEntryReady(object? sender, EventArgs eventArgs)
    {
        Focusable = true;
        ApplyNonActivatingStyle(shouldPreventActivation: false);
        Activate();

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            QuestionTextBox.Focus();
            Keyboard.Focus(QuestionTextBox);
            QuestionTextBox.SelectAll();
        });
    }

    private void HandleCompactStateRequested(object? sender, EventArgs eventArgs)
    {
        if (IsInteractivePaneVisible)
        {
            return;
        }

        Keyboard.ClearFocus();
        Focusable = false;
        ApplyNonActivatingStyle(shouldPreventActivation: true);
    }

    private void HandleViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs propertyChangedEventArgs)
    {
        if (!viewModel.IsConversationVisible ||
            propertyChangedEventArgs.PropertyName is not (
                nameof(CompanionViewModel.ConversationTurns) or
                nameof(CompanionViewModel.ActiveQuestionText)))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            ConversationScrollViewer.ScrollToEnd);
    }

    private void PositionNearPrimaryWorkArea()
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var primaryMonitor = NativeMethods.MonitorFromPoint(
            new NativePoint(0, 0),
            MonitorDefaultToPrimary);
        var monitorInfo = MonitorInfo.Create();
        if (primaryMonitor == nint.Zero ||
            !NativeMethods.GetMonitorInfo(primaryMonitor, ref monitorInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the primary monitor work area.");
        }

        var windowDpi = NativeMethods.GetDpiForWindow(windowHandle);
        var dpiScale = (windowDpi == 0 ? DefaultDpi : windowDpi) / DefaultDpi;
        var placement = WindowPlacementCalculator.Calculate(
            new PhysicalWorkArea(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top,
                monitorInfo.WorkArea.Right,
                monitorInfo.WorkArea.Bottom),
            dpiScale,
            Width,
            viewModel.WindowHeight,
            WorkAreaMargin);

        Left = placement.Left;
        Top = placement.Top;
    }

    private void SyncWindowHeight()
    {
        Height = viewModel.WindowHeight;
    }

    private bool IsInteractivePaneVisible =>
        viewModel.IsSettingsVisible || viewModel.IsConversationVisible;

    private void ApplyNonActivatingStyle(bool shouldPreventActivation)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetExtendedWindowStyle(windowHandle, ExtendedWindowStyleIndex);
        extendedStyle |= ToolWindowExtendedStyle;

        if (shouldPreventActivation)
        {
            extendedStyle |= NoActivateExtendedStyle;
        }
        else
        {
            extendedStyle &= ~NoActivateExtendedStyle;
        }

        NativeMethods.SetExtendedWindowStyle(windowHandle, ExtendedWindowStyleIndex, extendedStyle);
    }

    private static class NativeMethods
    {
        public static long GetExtendedWindowStyle(nint windowHandle, int index)
        {
            return nint.Size == 8
                ? GetWindowLongPtr64(windowHandle, index).ToInt64()
                : GetWindowLong32(windowHandle, index);
        }

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

        [DllImport("user32.dll")]
        public static extern nint MonitorFromPoint(
            NativePoint point,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(
            nint monitor,
            ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint windowHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new()
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
    }
}
