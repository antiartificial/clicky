using Clicky.Windows.Branding;
using WinForms = System.Windows.Forms;

namespace Clicky.Windows.Shell;

public sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.ContextMenuStrip contextMenu;
    private readonly WinForms.NotifyIcon notifyIcon;
    private readonly System.Drawing.Icon trayIcon;
    private bool isDisposed;

    public TrayIconHost(Action showCompanion, Action openSettings, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showCompanion);
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(exitApplication);

        contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add(CreateMenuItem(BrandText.ShowCompanionMenuItem, showCompanion));
        contextMenu.Items.Add(CreateMenuItem(BrandText.SettingsMenuItem, openSettings));
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add(CreateMenuItem(BrandText.ExitMenuItem, exitApplication));

        trayIcon = LoadTrayIcon();
        notifyIcon = new WinForms.NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = trayIcon,
            Text = BrandText.ApplicationName,
            Visible = true
        };

        notifyIcon.MouseClick += (_, mouseEventArgs) =>
        {
            if (mouseEventArgs.Button == WinForms.MouseButtons.Left)
            {
                RunOnApplicationDispatcher(showCompanion);
            }
        };
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        trayIcon.Dispose();
        contextMenu.Dispose();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var iconResourceUri = new Uri("pack://application:,,,/Assets/Clicky.ico", UriKind.Absolute);
        var iconResource = System.Windows.Application.GetResourceStream(iconResourceUri)
            ?? throw new InvalidOperationException("The embedded Clicky tray icon could not be loaded.");

        using var iconResourceStream = iconResource.Stream;
        using var embeddedIcon = new System.Drawing.Icon(iconResourceStream);
        return (System.Drawing.Icon)embeddedIcon.Clone();
    }

    private static WinForms.ToolStripMenuItem CreateMenuItem(string text, Action action)
    {
        var menuItem = new WinForms.ToolStripMenuItem(text);
        menuItem.Click += (_, _) => RunOnApplicationDispatcher(action);
        return menuItem;
    }

    private static void RunOnApplicationDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
