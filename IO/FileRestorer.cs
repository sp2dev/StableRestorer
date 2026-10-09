using System.Security.Cryptography;
using StableRestorer.Engine;

namespace StableRestorer.IO;

public enum RestoreMode
{
    HardLink,
    Copy,
}

public enum FileOutcome
{
    /// <summary>A new hard link was created.</summary>
    Linked,

    /// <summary>The file was copied (copy mode, or the link target is on another volume).</summary>
    Copied,

    /// <summary>A hard link was requested but the destination could not be linked, so it was copied.</summary>
    CopiedAfterLinkFailure,

    /// <summary>The destination already existed and is the very same file (an existing hard link).</summary>
    AlreadyLinked,

    /// <summary>The destination already existed and was left alone.</summary>
    SkippedExisting,

    /// <summary>The destination already existed and was replaced.</summary>
    Replaced,

    /// <summary>The hashed file is missing from the lazer file store.</summary>
    SourceMissing,

    /// <summary>The hashed file exists but its content does not match its name.</summary>
    SourceHashMismatch,

    /// <summary>The file could not be written.</summary>
    Failed,
}

public sealed record FileResult(string Package, string RelativePath, string Hash, FileOutcome Outcome, string? Detail = null);

/// <summary>
/// Creates one destination file from one hashed lazer file: hard link when possible,
/// copy when necessary, and never by deleting or modifying the source.
/// </summary>
public sealed class FileRestorer
{
    private readonly RestoreOptions _options;
    private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);

    public FileRestorer(RestoreOptions options) => _options = options;

    /// <summary>Creates the destination's parent directory once per run.</summary>
    private void EnsureParentDirectory(string destinationPath)
    {
        string? directory = Path.GetDirectoryName(destinationPath);

        if (string.IsNullOrEmpty(directory) || !_createdDirectories.Add(directory))
            return;

        Directory.CreateDirectory(directory);
    }

    /// <param name="relativeLabel">
    /// Human-readable location used in reports. It is passed in rather than derived, because a run
    /// writes into several target trees (Songs/, Skins/, Replays/) and the CLI can point them
    /// anywhere.
    /// </param>
    public FileResult Restore(string packageName, string destinationPath, string sourcePath, string expectedHash, string relativeLabel)
    {
        string relative = relativeLabel;

        if (!File.Exists(sourcePath))
            return new FileResult(packageName, relative, expectedHash, FileOutcome.SourceMissing, sourcePath);

        if (_options.VerifyHashes && !MatchesHash(sourcePath, expectedHash))
            return new FileResult(packageName, relative, expectedHash, FileOutcome.SourceHashMismatch, sourcePath);

        bool destinationExists = File.Exists(destinationPath);

        // 目标已经在，而且不是"覆盖"模式：同 inode 就是已经链好了，直接跳过；
        // 内容不同的目标文件留原样并记录（只有 --overwrite 才替换）。
        if (destinationExists && !_options.Overwrite)
        {
            if (FileSystem.IsSameFile(sourcePath, destinationPath))
                return new FileResult(packageName, relative, expectedHash, FileOutcome.AlreadyLinked);

            return new FileResult(packageName, relative, expectedHash, FileOutcome.SkippedExisting, destinationPath);
        }

        // Both CreateHardLink and File.Copy need the parent directory to exist, and Realm
        // filenames can nest (storyboard assets), so this cannot be done once per package.
        try
        {
            EnsureParentDirectory(destinationPath);
        }
        catch (Exception ex)
        {
            return new FileResult(packageName, relative, expectedHash, FileOutcome.Failed,
                $"cannot create directory '{Path.GetDirectoryName(destinationPath)}': {ex.Message}");
        }

        if (_options.Mode == RestoreMode.HardLink)
        {
            // A hard link cannot be created over an existing path, so clear it first.
            if (destinationExists)
                Replace(destinationPath);

            try
            {
                FileSystem.CreateHardLink(destinationPath, sourcePath);

                // An existing destination is unlinked first, so this is always "a fresh hard link
                // was created" - never a replacement that consumed new space.
                return new FileResult(packageName, relative, expectedHash, FileOutcome.Linked);
            }
            catch (Exception ex)
            {
                // Two expected causes: the output volume differs from the lazer store, or this
                // particular file already holds NTFS's maximum of 1024 hard links. Either way a
                // copy keeps the restore complete.
                var fallback = Copy(packageName, relative, destinationPath, sourcePath, expectedHash);

                return fallback.Outcome switch
                {
                    FileOutcome.Copied => fallback with
                    {
                        Outcome = destinationExists ? FileOutcome.Replaced : FileOutcome.CopiedAfterLinkFailure,
                        Detail = $"{fallback.Detail} (hard link failed: {ex.Message})",
                    },
                    FileOutcome.Failed => fallback with
                    {
                        Detail = $"hard link failed ({ex.Message}) and the fallback copy also failed: {fallback.Detail}",
                    },
                    _ => fallback,
                };
            }
        }

        var copied = Copy(packageName, relative, destinationPath, sourcePath, expectedHash);

        return copied.Outcome == FileOutcome.Copied && destinationExists
            ? copied with { Outcome = FileOutcome.Replaced }
            : copied;
    }

    private FileResult Copy(string packageName, string relative, string destinationPath, string sourcePath, string expectedHash)
    {
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            FileSystem.EnsureWritable(destinationPath);

            var sourceInfo = new FileInfo(sourcePath);
            var destinationInfo = new FileInfo(destinationPath);

            if (sourceInfo.Length != destinationInfo.Length)
            {
                return new FileResult(packageName, relative, expectedHash, FileOutcome.Failed,
                    $"size mismatch after copy ({sourceInfo.Length} != {destinationInfo.Length})");
            }

            if (_options.VerifyHashes && !MatchesHash(destinationPath, expectedHash))
            {
                return new FileResult(packageName, relative, expectedHash, FileOutcome.Failed,
                    "hash mismatch after copy");
            }

            return new FileResult(packageName, relative, expectedHash, FileOutcome.Copied);
        }
        catch (Exception ex)
        {
            return new FileResult(packageName, relative, expectedHash, FileOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Removes an existing destination in the output directory so a fresh link can be made.
    /// Safe because the restore only ever happens inside a directory this tool owns or was pointed at,
    /// and the source file in the lazer store is never touched.
    /// </summary>
    private static void Replace(string destinationPath)
    {
        FileSystem.EnsureWritable(destinationPath);
        File.Delete(destinationPath);
    }

    private static bool MatchesHash(string path, string expectedHash)
    {
        if (expectedHash.Length != 64)
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(stream, digest);
            return Convert.ToHexStringLower(digest) == expectedHash.ToLowerInvariant();
        }
        catch
        {
            return false;
        }
    }
}
