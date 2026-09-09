using System.Runtime.Versioning;

namespace UsbDocumentBackup.Windows;

/// <summary>
/// A hidden top-level window that listens for volume arrival/removal and resume-from-sleep.
/// Reacting to the message is what makes a plugged-in stick get picked up immediately; the
/// periodic sweep exists only to cover messages we never see.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_POWERBROADCAST = 0x0218;

    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public DeviceChangeWindow()
    {
        // A default CreateParams gives a top-level window, which is required to receive the
        // broadcast WM_DEVICECHANGE; a message-only window would never see it.
        CreateHandle(new CreateParams());
    }

    /// <summary>A volume appeared, or the machine woke up. Time to look for work.</summary>
    public event Action? VolumesMayHaveArrived;

    /// <summary>A volume went away. Any read in flight against it should stop.</summary>
    public event Action? VolumeRemoved;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_DEVICECHANGE:
                switch ((int)m.WParam)
                {
                    case DBT_DEVICEARRIVAL:
                    case DBT_DEVNODES_CHANGED:
                        VolumesMayHaveArrived?.Invoke();
                        break;
                    case DBT_DEVICEREMOVECOMPLETE:
                        VolumeRemoved?.Invoke();
                        break;
                }

                break;

            case WM_POWERBROADCAST:
                if ((int)m.WParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC)
                {
                    VolumesMayHaveArrived?.Invoke();
                }

                break;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
