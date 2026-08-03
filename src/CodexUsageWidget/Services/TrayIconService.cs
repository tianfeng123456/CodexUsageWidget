using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageWidget.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip contextMenu;
    private readonly ToolStripMenuItem showItem;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem settingsItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly Action<bool> changeStartup;
    private Icon? usageIcon;
    private double? remainingPercent;
    private bool useLightShellTheme;
    private HollowLineIconRenderer.VisualStatus renderedStatus;
    private bool renderedWithLightShellTheme;
    private bool hasRenderedIcon;
    private bool windowVisible = true;
    private bool synchronizingStartup;
    private bool disposed;

    public TrayIconService(
        Action showOrHide,
        Action refresh,
        Action openSettings,
        Action<bool> changeStartup,
        Action exit,
        bool useLightTheme)
    {
        this.changeStartup = changeStartup;
        useLightShellTheme =
            ThemeService.IsSystemShellLightTheme(useLightTheme);
        showItem = new ToolStripMenuItem(
            LocalizationService.Instance.Get("Loc.TrayShowHide"),
            null,
            (_, _) => showOrHide());
        refreshItem = new ToolStripMenuItem(
            LocalizationService.Instance.Get("Loc.RefreshNow"),
            null,
            (_, _) => refresh());
        settingsItem = new ToolStripMenuItem(
            LocalizationService.Instance.Get("Loc.Settings"),
            null,
            (_, _) => openSettings());
        startupItem = new ToolStripMenuItem(
            LocalizationService.Instance.Get("Loc.TrayStartup"))
        {
            CheckOnClick = true,
        };
        startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        exitItem = new ToolStripMenuItem(
            LocalizationService.Instance.Get("Loc.TrayExit"),
            null,
            (_, _) => exit());

        contextMenu = new ContextMenuStrip();
        contextMenu.Items.AddRange(
        [
            showItem,
            refreshItem,
            settingsItem,
            new ToolStripSeparator(),
            startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);

        notifyIcon = new NotifyIcon
        {
            Text = LocalizationService.Instance.Get("Loc.TrayDefaultTooltip"),
            ContextMenuStrip = contextMenu,
            Visible = false,
        };
        SetUsagePercent(null);
        notifyIcon.Visible = true;
        notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                showOrHide();
            }
        };
    }

    public void SetWindowVisible(bool visible)
    {
        if (disposed)
        {
            return;
        }

        windowVisible = visible;
        showItem.Text = LocalizationService.Instance.Get(
            visible ? "Loc.TrayHide" : "Loc.TrayShow");
    }

    public void SetStartupEnabled(bool enabled)
    {
        if (disposed)
        {
            return;
        }

        synchronizingStartup = true;
        try
        {
            startupItem.Checked = enabled;
        }
        finally
        {
            synchronizingStartup = false;
        }
    }

    public void SetTooltip(string text)
    {
        if (disposed)
        {
            return;
        }

        notifyIcon.Text = string.IsNullOrWhiteSpace(text)
            ? LocalizationService.Instance.Get("Loc.TrayDefaultTooltip")
            : text[..Math.Min(text.Length, 63)];
    }

    public void ApplyLocalization()
    {
        if (disposed)
        {
            return;
        }

        showItem.Text = LocalizationService.Instance.Get(
            windowVisible ? "Loc.TrayHide" : "Loc.TrayShow");
        refreshItem.Text = LocalizationService.Instance.Get("Loc.RefreshNow");
        settingsItem.Text = LocalizationService.Instance.Get("Loc.Settings");
        startupItem.Text = LocalizationService.Instance.Get("Loc.TrayStartup");
        exitItem.Text = LocalizationService.Instance.Get("Loc.TrayExit");
        notifyIcon.Text = LocalizationService.Instance.Get(
            "Loc.TrayDefaultTooltip")[..Math.Min(
                LocalizationService.Instance.Get(
                    "Loc.TrayDefaultTooltip").Length,
                63)];
    }

    public void SetUsagePercent(double? remainingPercent)
    {
        if (disposed)
        {
            return;
        }

        this.remainingPercent = remainingPercent;
        ReplaceIcon();
    }

    public void ApplyTheme(bool useLightTheme)
    {
        if (disposed)
        {
            return;
        }

        var nextShellTheme =
            ThemeService.IsSystemShellLightTheme(useLightTheme);
        if (useLightShellTheme == nextShellTheme)
        {
            return;
        }

        useLightShellTheme = nextShellTheme;
        ReplaceIcon();
    }

    private void ReplaceIcon()
    {
        var nextStatus =
            HollowLineIconRenderer.GetVisualStatus(remainingPercent);
        if (hasRenderedIcon &&
            renderedStatus == nextStatus &&
            renderedWithLightShellTheme == useLightShellTheme)
        {
            return;
        }

        var nextIcon = HollowLineIconRenderer.CreateTrayIcon(
            remainingPercent,
            useLightShellTheme);
        var previousIcon = usageIcon;
        usageIcon = nextIcon;
        renderedStatus = nextStatus;
        renderedWithLightShellTheme = useLightShellTheme;
        hasRenderedIcon = true;
        notifyIcon.Icon = nextIcon;
        previousIcon?.Dispose();
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (disposed)
        {
            return;
        }

        notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip = null;
        notifyIcon.Dispose();
        startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
        contextMenu.Dispose();
        usageIcon?.Dispose();
        usageIcon = null;
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs args)
    {
        if (!synchronizingStartup)
        {
            changeStartup(startupItem.Checked);
        }
    }

}
