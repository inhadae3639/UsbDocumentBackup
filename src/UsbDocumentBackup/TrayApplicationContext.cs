using System.Runtime.Versioning;
using UsbDocumentBackup.Backup;
using UsbDocumentBackup.UI;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup;

/// <summary>
/// The resident part of the app. It shows a tray icon and nothing else: no console, no progress
/// window, no balloon notifications, and it never steals focus while someone is presenting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly DeviceChangeWindow _deviceWindow;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly SynchronizationContext _uiContext;

    private StatusForm? _statusForm;
    private SettingsForm? _settingsForm;

    public TrayApplicationContext(AppHost host)
    {
        _host = host;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _pauseItem = new ToolStripMenuItem("일시정지", null, (_, _) => TogglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("상태 열기", null, (_, _) => ShowStatus()));
        menu.Items.Add(new ToolStripMenuItem("백업 폴더 열기", null, (_, _) => AutoStart.OpenFolder(_host.Paths.ArchiveRoot)));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("설정", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "USB 문서 백업",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();

        _deviceWindow = new DeviceChangeWindow();
        _deviceWindow.VolumesMayHaveArrived += () => _host.Coordinator.RequestScan(ScanReason.NewConnection);
        _deviceWindow.VolumeRemoved += () => _host.Coordinator.CancelActiveDeviceScan();

        _host.Coordinator.StatusChanged += OnStatusChanged;

        UpdatePauseLabel();
        _host.Coordinator.Start();
    }

    private void OnStatusChanged() => _uiContext.Post(_ => _statusForm?.Reload(), null);

    private void ShowStatus()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_host);
            _statusForm.FormClosed += (_, _) => _statusForm = null;
            _statusForm.Show();
        }
        else
        {
            _statusForm.Reload();
            _statusForm.Activate();
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_host);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        else
        {
            _settingsForm.Activate();
        }
    }

    private void TogglePause()
    {
        var paused = !_host.Coordinator.Paused;
        _host.Coordinator.SetPaused(paused);
        _host.Settings.Paused = paused;
        _host.SettingsStore.Save(_host.Settings);
        UpdatePauseLabel();
    }

    private void UpdatePauseLabel() => _pauseItem.Text = _host.Coordinator.Paused ? "재개" : "일시정지";

    private void ExitApp()
    {
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.Coordinator.StatusChanged -= OnStatusChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _deviceWindow.Dispose();
        }

        base.Dispose(disposing);
    }
}
