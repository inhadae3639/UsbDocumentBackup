using UsbDocumentBackup.Storage;

namespace UsbDocumentBackup.Devices;

/// <summary>
/// One mounted volume as seen at a point in time. <see cref="VolumeId"/> is the stable identity;
/// <see cref="RootPath"/> is only the current mount point and may change between connections.
/// </summary>
public sealed record VolumeInfo(
    string RootPath,
    string? VolumeId,
    string Fingerprint,
    string DisplayName,
    DriveType DriveType,
    BusKind BusKind,
    bool IsReady)
{
    /// <summary>
    /// Whether the volume should be scanned. A reported USB bus is the only positive signal;
    /// when the bus type is unknown we fall back to the removable flag, which covers plain USB
    /// sticks on machines where the storage WMI provider is unavailable. Unknown plus fixed is
    /// never scanned, because that is indistinguishable from an internal disk.
    /// </summary>
    public bool IsBackupTarget => BusKind switch
    {
        BusKind.Usb => true,
        BusKind.Internal => false,
        _ => DriveType == DriveType.Removable,
    };

    /// <summary>True when the classification was a guess worth recording for the user.</summary>
    public bool ClassificationUncertain => BusKind == BusKind.Unknown;
}

public interface IVolumeProvider
{
    IReadOnlyList<VolumeInfo> GetVolumes();
}
