using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UsbDocumentBackup.Storage;

namespace UsbDocumentBackup.Devices;

/// <summary>
/// Enumerates mounted volumes and works out which ones are attached over USB.
/// <see cref="DriveType.Removable"/> alone is not enough: a USB-attached external SSD reports as
/// a fixed disk, so the storage provider's bus type is the primary signal.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeProvider : IVolumeProvider
{
    /// <summary>MSFT_Disk.BusType values. 0 means the provider itself does not know.</summary>
    private const ushort BusTypeUnknown = 0;
    private const ushort BusTypeUsb = 7;

    private readonly Action<string, Exception?> _onProviderFailure;

    public WindowsVolumeProvider(Action<string, Exception?>? onProviderFailure = null) =>
        _onProviderFailure = onProviderFailure ?? ((_, _) => { });

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var busByDriveLetter = QueryBusTypes();
        var volumes = new List<VolumeInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            var letter = root.Length > 0 ? char.ToUpperInvariant(root[0]) : '\0';

            var ready = false;
            string? label = null;
            long capacity = 0;
            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    label = drive.VolumeLabel;
                    capacity = drive.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            var volumeId = TryGetVolumeId(root);
            var serial = TryGetVolumeSerial(root);
            var fingerprint = string.Join(
                '|',
                serial?.ToString("X8", CultureInfo.InvariantCulture) ?? "-",
                label ?? "-",
                capacity.ToString(CultureInfo.InvariantCulture));

            var busKind = busByDriveLetter.TryGetValue(letter, out var kind) ? kind : BusKind.Unknown;

            var name = string.IsNullOrWhiteSpace(label)
                ? $"{root.TrimEnd('\\')} ({drive.DriveType})"
                : $"{label} ({root.TrimEnd('\\')})";

            volumes.Add(new VolumeInfo(root, volumeId, fingerprint, name, drive.DriveType, busKind, ready));
        }

        return volumes;
    }

    /// <summary>
    /// Maps drive letter to bus kind by joining MSFT_Partition (drive letter, disk number) with
    /// MSFT_Disk (disk number, bus type). Returns an empty map when the provider is unavailable,
    /// which leaves every volume classified as unknown rather than silently as a success.
    /// </summary>
    private Dictionary<char, BusKind> QueryBusTypes()
    {
        var result = new Dictionary<char, BusKind>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var busByDiskNumber = new Dictionary<uint, BusKind>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, BusType FROM MSFT_Disk")))
            using (var disks = searcher.Get())
            {
                foreach (var disk in disks.Cast<ManagementBaseObject>())
                {
                    using (disk)
                    {
                        if (disk["Number"] is null || disk["BusType"] is null)
                        {
                            continue;
                        }

                        var number = Convert.ToUInt32(disk["Number"], CultureInfo.InvariantCulture);
                        var busType = Convert.ToUInt16(disk["BusType"], CultureInfo.InvariantCulture);
                        // A successful query that reports bus type 0 has told us nothing. Calling
                        // that "internal" would silently drop a stick the provider could not
                        // identify, so it stays Unknown and falls back to the removable check.
                        busByDiskNumber[number] = busType switch
                        {
                            BusTypeUsb => BusKind.Usb,
                            BusTypeUnknown => BusKind.Unknown,
                            _ => BusKind.Internal,
                        };
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
            using (var partitions = searcher.Get())
            {
                foreach (var partition in partitions.Cast<ManagementBaseObject>())
                {
                    using (partition)
                    {
                        var letter = ReadDriveLetter(partition["DriveLetter"]);
                        if (letter is null || partition["DiskNumber"] is null)
                        {
                            continue;
                        }

                        var diskNumber = Convert.ToUInt32(partition["DiskNumber"], CultureInfo.InvariantCulture);
                        if (busByDiskNumber.TryGetValue(diskNumber, out var kind))
                        {
                            result[letter.Value] = kind;
                        }
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            _onProviderFailure("Storage WMI provider query failed", ex);
            return new Dictionary<char, BusKind>();
        }
        catch (COMException ex)
        {
            _onProviderFailure("Storage WMI provider unavailable", ex);
            return new Dictionary<char, BusKind>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _onProviderFailure("Storage WMI provider access denied", ex);
            return new Dictionary<char, BusKind>();
        }

        return result;
    }

    /// <summary>MSFT_Partition.DriveLetter is a char16; an unassigned letter comes back as NUL.</summary>
    private static char? ReadDriveLetter(object? value) => value switch
    {
        null => null,
        char c when c != '\0' => char.ToUpperInvariant(c),
        string s when s.Length > 0 && s[0] != '\0' => char.ToUpperInvariant(s[0]),
        ushort u when u != 0 => char.ToUpperInvariant((char)u),
        _ => null,
    };

    private static string? TryGetVolumeId(string rootPath)
    {
        var buffer = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(rootPath, buffer, buffer.Capacity)
            ? buffer.ToString()
            : null;
    }

    private static uint? TryGetVolumeSerial(string rootPath)
    {
        var name = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        return GetVolumeInformationW(rootPath, name, name.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity)
            ? serial
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
