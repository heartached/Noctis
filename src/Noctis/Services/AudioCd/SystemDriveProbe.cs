namespace Noctis.Services.AudioCd;

/// <summary>
/// Optical-drive discovery through what the OS gives away for free. Windows lists
/// CD-ROM drives with a ready flag (an audio CD counts as "ready": Explorer shows it
/// with .cda entries). Linux only exposes the device node, so readiness is unknown
/// until a read is attempted.
/// </summary>
public sealed class SystemDriveProbe : IAudioCdDriveProbe
{
    public bool SupportsReadyProbe => OperatingSystem.IsWindows();

    public IReadOnlyList<string> GetOpticalDriveRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var roots = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.DriveType == DriveType.CDRom) roots.Add(d.Name); }
                catch { /* a drive that vanished mid-enumeration */ }
            }
            return roots;
        }

        if (OperatingSystem.IsLinux())
        {
            var roots = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var node = $"/dev/sr{i}";
                if (File.Exists(node)) roots.Add(node);
            }
            return roots;
        }

        return Array.Empty<string>();
    }

    public bool IsDiscReady(string driveRoot)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try { return new DriveInfo(driveRoot).IsReady; }
        catch { return false; }
    }
}
