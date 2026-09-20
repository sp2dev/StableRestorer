using Microsoft.Win32;

namespace StableRestorer.Cli;

/// <summary>
/// Locates an osu!lazer data directory and an osu!stable installation, mirroring what osu! itself
/// does so the interactive mode can pre-fill sensible paths instead of asking the user to type them.
///
/// References (local checkout):
/// <list type="bullet">
/// <item><c>osu.Desktop/OsuGameDesktop.getStableInstallPath()</c> - registry-then-well-known-path
/// lookup for stable, validating with <c>Songs/</c> or <c>osu!.cfg</c>;</item>
/// <item><c>osu.Game/IO/StableStorage.locateSongsDirectory()</c> - honours a custom
/// <c>BeatmapDirectory</c> in <c>osu!.{user}.cfg</c>;</item>
/// <item><c>BeatmapExporterCore.LazerDatabase.GetDefaultDirectories()</c> - per-platform lazer
/// data directories.</item>
/// </list>
/// </summary>
public static class InstallLocator
{
    private sealed record Candidate(string Path, string Source);

    /// <summary>Per-platform osu!lazer data directories, most likely first.</summary>
    public static IEnumerable<string> LazerDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/osu");
        }
        else
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/osu");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var/app/sh.ppy.osu/data/osu");
        }
    }

    /// <summary>
    /// Returns the first lazer data directory that actually contains a client.realm, checking the
    /// platform default first and then the top level of each drive, since osu!lazer is often
    /// installed next to osu!stable on a data drive instead of in <c>%APPDATA%</c>.
    /// </summary>
    public static string? DetectLazer()
    {
        foreach (string directory in LazerDirectories())
        {
            if (File.Exists(Path.Combine(directory, "client.realm")))
                return directory;
        }

        foreach (string driveRoot in DriveRoots())
        {
            foreach (string candidate in TopLevelDirectories(driveRoot))
            {
                if (File.Exists(Path.Combine(candidate, "client.realm")))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Well-known osu!stable install locations, in the order osu! itself checks them after the
    /// registry. Kept public so the interactive mode can list what it tried when detection fails.
    /// </summary>
    public static IEnumerable<string> StableDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
            yield return @"C:\osu!";
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osu");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "osu!");
    }

    /// <summary>
    /// Finds an osu!stable installation and returns its root along with a short description of how
    /// it was found, for display in the interactive mode.
    /// </summary>
    public static (string? Path, string Source) DetectStable()
    {
        foreach (var candidate in StableCandidates())
        {
            if (IsUsableStableInstall(candidate.Path))
                return (candidate.Path, candidate.Source);
        }

        // Nothing in the well-known places: osu!stable is commonly unpacked straight onto a data
        // drive under any folder name, so look at the top level of each drive. Only one level is
        // scanned, so this stays cheap even on a full disk.
        foreach (string driveRoot in DriveRoots())
        {
            foreach (string candidate in TopLevelDirectories(driveRoot))
            {
                if (IsUsableStableInstall(candidate))
                    return (candidate, $"磁盘 {driveRoot} 下找到");
            }
        }

        return (null, "未找到");
    }

    private static IEnumerable<Candidate> StableCandidates()
    {
        // Registry first: this is how osu! finds an install that was moved somewhere custom.
        if (OperatingSystem.IsWindows())
        {
            foreach (string progId in new[] { "osustable.File.osz", "osu!" })
            {
                string? fromRegistry = null;

                try
                {
                    fromRegistry = ReadInstallPathFromRegistry(progId);
                }
                catch
                {
                    // No access to the registry, or the key does not exist - fall through.
                }

                if (!string.IsNullOrWhiteSpace(fromRegistry))
                    yield return new Candidate(fromRegistry!, $"注册表 {progId}");
            }
        }

        foreach (string wellKnown in StableDirectories())
            yield return new Candidate(wellKnown, "默认安装位置");

        // Reuse lazer's own configured stable path, if the user ever pointed lazer at stable.
        string? fromLazer = ReadStablePathFromLazerConfig();

        if (!string.IsNullOrWhiteSpace(fromLazer))
            yield return new Candidate(fromLazer!, "osu!lazer 配置记录");
    }

    /// <summary>
    /// A stable install is only useful to this tool when its Songs folder (or osu!.db) is actually
    /// there; an empty leftover folder such as a bare <c>%LOCALAPPDATA%\osu!</c> must not be offered
    /// as if it were an installation.
    /// </summary>
    public static bool IsUsableStableInstall(string path)
    {
        if (!LooksLikeStableInstall(path))
            return false;

        if (Directory.Exists(ResolveStableSongsDirectory(path)))
            return true;

        return File.Exists(Path.Combine(path, "osu!.db"));
    }

    /// <summary>Enumerates immediate subdirectories of a drive root, ignoring unreadable ones.</summary>
    private static IEnumerable<string> TopLevelDirectories(string driveRoot)
    {
        string[] entries;

        try
        {
            entries = Directory.GetDirectories(driveRoot);
        }
        catch
        {
            yield break;
        }

        foreach (string entry in entries)
        {
            // Skip the Windows-managed folders; an osu! install never lives inside them.
            string name = Path.GetFileName(entry);

            if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith('$'))
                continue;

            yield return entry;
        }
    }

    private static IEnumerable<string> DriveRoots()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool ready;

            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network;
            }
            catch
            {
                continue;
            }

            if (ready)
                yield return drive.RootDirectory.FullName;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadInstallPathFromRegistry(string progId)
    {
        using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(progId);
        using RegistryKey? command = key?.OpenSubKey(@"shell\open\command");

        string? value = command?.GetValue(string.Empty)?.ToString();

        if (string.IsNullOrEmpty(value))
            return null;

        // The command looks like: "C:\osu!\osu!.exe" "%1"
        string[] parts = value.Split('"', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return null;

        return Path.GetDirectoryName(parts[0].Replace("osu!.exe", string.Empty).TrimEnd('\\', '/'));
    }

    /// <summary>
    /// osu!lazer stores the last known stable path in its own config (game.ini) once the user has
    /// been through first-run setup, which is how a non-default install can still be recovered.
    /// </summary>
    public static string? ReadStablePathFromLazerConfig()
    {
        string? lazer = DetectLazer();

        if (lazer == null)
            return null;

        foreach (string configName in new[] { "game.ini", "framework.ini" })
        {
            string configPath = Path.Combine(lazer, configName);

            if (!File.Exists(configPath))
                continue;

            try
            {
                foreach (string line in File.ReadLines(configPath))
                {
                    int separator = line.IndexOf('=');

                    if (separator < 0)
                        continue;

                    string key = line[..separator].Trim();

                    if (!key.Equals("StablePath", StringComparison.OrdinalIgnoreCase)
                        && !key.Equals("LegacyStablePath", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string value = line[(separator + 1)..].Trim();

                    if (value.Length > 0 && Directory.Exists(value))
                        return value;
                }
            }
            catch
            {
                // A malformed config just means detection falls back to the well-known paths.
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a stable root the way osu! does, and additionally accepts an install whose
    /// BeatmapDirectory points elsewhere.
    /// </summary>
    public static bool LooksLikeStableInstall(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "Songs"))
                   || File.Exists(Path.Combine(path, "osu!.cfg"))
                   || File.Exists(Path.Combine(path, "osu!.exe"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the Songs directory for a stable root, honouring a custom <c>BeatmapDirectory</c>
    /// in <c>osu!.{username}.cfg</c> exactly like <c>StableStorage.locateSongsDirectory()</c>.
    /// Falls back to <c>&lt;root&gt;/Songs</c>.
    /// </summary>
    public static string ResolveStableSongsDirectory(string stableRoot)
    {
        try
        {
            string[] configs = Directory.GetFiles(stableRoot, $"osu!.{Environment.UserName}.cfg");

            string? usable = configs.FirstOrDefault(f => f.Contains(Environment.UserName, StringComparison.Ordinal))
                             ?? configs.FirstOrDefault();

            if (usable != null)
            {
                foreach (string line in File.ReadLines(usable))
                {
                    if (!line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? value = line.Split('=').LastOrDefault()?.Trim();

                    if (!string.IsNullOrEmpty(value))
                    {
                        // A relative value is relative to the stable root.
                        return Path.IsPathFullyQualified(value) ? value : Path.Combine(stableRoot, value);
                    }

                    break;
                }
            }
        }
        catch
        {
            // Fall through to the default layout.
        }

        return Path.Combine(stableRoot, "Songs");
    }

    /// <summary>Reports checks a stable root should pass, for the interactive summary.</summary>
    public static IEnumerable<string> DescribeStableProblems(string stableRoot)
    {
        if (!Directory.Exists(stableRoot))
        {
            yield return $"目录不存在：{stableRoot}";
            yield break;
        }

        string songs = ResolveStableSongsDirectory(stableRoot);

        if (!Directory.Exists(songs))
            yield return $"找不到 Songs 目录：{songs}";

        if (!File.Exists(Path.Combine(stableRoot, "osu!.db")))
            yield return "找不到 osu!.db（stable 可能还没运行过，或这不是 stable 根目录）";
    }
}
