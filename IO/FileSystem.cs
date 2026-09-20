using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StableRestorer.IO;

/// <summary>Identity of an open file (device + inode on Unix, volume serial + file id on Windows).</summary>
public readonly record struct FileIdentity(ulong Device, ulong Inode);

/// <summary>Result of checking whether two directories can hold hard links to each other.</summary>
public enum HardLinkVerdict
{
    /// <summary>Same volume: hard links will work.</summary>
    SameVolume,

    /// <summary>Different volumes: links are impossible and the restore must copy.</summary>
    DifferentVolume,

    /// <summary>Could not be determined; the restore will just try and fall back to copying.</summary>
    Unknown,
}

/// <summary>
/// Helpers for the two file-system facts this tool depends on:
/// whether two paths are the same file (a hard link), and how to create a hard link.
/// </summary>
public static class FileSystem
{
    /// <summary>
    /// Compares the volumes holding two directories. A hard link cannot span volumes, so an
    /// osu!lazer store and an output directory on different drives force copy mode (and with it a
    /// full duplicate of the data), which is worth warning about before a long run.
    /// </summary>
    public static HardLinkVerdict CanHardLinkBetween(string sourceDirectory, string targetDirectory)
    {
        if (!TryGetVolumeSerial(sourceDirectory, out uint source) || !TryGetVolumeSerial(targetDirectory, out uint target))
            return HardLinkVerdict.Unknown;

        return source == target ? HardLinkVerdict.SameVolume : HardLinkVerdict.DifferentVolume;
    }

    /// <summary>
    /// Reads the volume serial number that owns a path. Uses the volume root, which works for a
    /// directory that already exists as well as for a file; falls back to opening a file handle
    /// when the root itself cannot be queried (e.g. a UNC path).
    /// </summary>
    public static bool TryGetVolumeSerial(string path, out uint serial)
    {
        serial = 0;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(root) && GetVolumeInformationW(root, null, 0, out uint rootSerial, out _, out _, null, 0))
            {
                serial = rootSerial;
                return true;
            }
        }
        catch
        {
            // Fall through to the handle-based probe below.
        }

        return TryGetVolumeSerialFromHandle(path, out serial);
    }

    /// <summary>
    /// Reads the volume serial by opening an existing file. Only usable on a file that is present.
    /// </summary>
    public static bool TryGetVolumeSerialFromHandle(string path, out uint serial)
    {
        serial = 0;

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                return false;

            serial = info.VolumeSerialNumber;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the volume serial for a directory by creating a throwaway file inside it. Used when
    /// the volume root cannot be identified directly; requires the directory to exist.
    /// </summary>
    public static bool TryGetVolumeSerialOfDirectory(string directory, out uint serial)
    {
        serial = 0;

        if (!Directory.Exists(directory))
            return false;

        if (TryGetVolumeSerial(directory, out serial))
            return true;

        string probe = Path.Combine(directory, $".stablerestorer-volume-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return TryGetVolumeSerialFromHandle(probe, out serial);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                    File.Delete(probe);
            }
            catch
            {
                // Probe file is best-effort; a leftover is harmless and DeleteOnClose usually wins.
            }
        }
    }

    public static bool TryGetIdentity(string path, out FileIdentity identity)
    {
        identity = default;

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                return false;

            identity = new FileIdentity(((ulong)info.VolumeSerialNumber << 32) | info.FileIndexHigh, info.FileIndexLow);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when both paths resolve to the same file on disk.</summary>
    public static bool IsSameFile(string a, string b)
        => TryGetIdentity(a, out var ida) && TryGetIdentity(b, out var idb) && ida == idb;

    public static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            throw new IOException($"CreateHardLink failed for '{linkPath}' -> '{existingPath}' (win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Clears the read-only attribute if the source file carries one (osu!lazer never does).</summary>
    public static void EnsureWritable(string path)
    {
        var attrs = File.GetAttributes(path);

        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        char[]? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char[]? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
