using Realms;
using Realms.Exceptions;
using StableRestorer.RealmSchema;

namespace StableRestorer.Lazer;

/// <summary>
/// A single file that must exist inside a restored stable folder.
/// </summary>
/// <param name="Hash">SHA-256 content hash; also the filename inside the lazer store.</param>
/// <param name="Filename">Original filename as recorded by osu!lazer (may contain subdirectories).</param>
/// <param name="MapId">
/// <c>BeatmapInfo.OnlineID</c> (the beatmap/difficulty id) this file belongs to, or -1 when the
/// association is not a single difficulty (shared assets such as hitsounds are attached to the set).
/// </param>
public sealed record FileEntry(string Hash, string Filename, int MapId = -1)
{
    /// <summary>Original filename with forward slashes normalised to the platform separator.</summary>
    public string RelativePath { get; } = Filename.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>
/// A beatmap set with everything needed to rebuild its stable folder.
/// </summary>
/// <param name="Id">Realm primary key (a Guid); stable has no use for it, kept for reporting.</param>
/// <param name="SetId">
/// <c>BeatmapSetInfo.OnlineID</c> - the beatmap <b>set</b> id. This is the identifier stable uses,
/// and the one this tool keys everything on. It is -1 for sets that were never submitted online.
/// </param>
/// <param name="MapIds">
/// <c>BeatmapInfo.OnlineID</c> for every difficulty in the set - the individual beatmap ids.
/// </param>
/// <param name="Protected">
/// True when osu!lazer bundles this set with the game rather than having imported it from the user.
/// </param>
public sealed record SongSnapshot(
    Guid Id,
    int SetId,
    string Artist,
    string Title,
    IReadOnlyList<int> MapIds,
    IReadOnlyList<FileEntry> Files,
    bool Protected);

/// <summary>Immutable, thread-safe copy of everything read out of the Realm database.</summary>
public sealed record LazerSnapshot(
    int SchemaVersion,
    IReadOnlyList<SongSnapshot> SongSets,
    int DeletedSetCount,
    int EmptySetCount,
    int ProtectedSetCount,
    int TotalNamedFiles,
    int DistinctHashes,
    int DistinctSetIds,
    int DistinctMapIds,
    int DuplicateSetIds,
    IReadOnlyList<int> DuplicateSetIdList);

public sealed class LazerSchemaMismatchException : Exception
{
    public LazerSchemaMismatchException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Opens an osu!lazer <c>client.realm</c> strictly read-only and projects it into plain objects.
///
/// Realm objects are thread-confined and become invalid once the Realm closes, so every value the
/// restore stage needs is copied out here - the Realm instance is then disposed before any file
/// system work happens.
/// </summary>
public static class LazerDatabase
{
    /// <summary>Schema version written by the osu! models this tool mirrors.</summary>
    public const int SupportedSchemaVersion = 52;

    public static readonly Type[] SchemaTypes =
    {
        typeof(BeatmapSet),
        typeof(Beatmap),
        typeof(BeatmapMetadata),
        typeof(BeatmapDifficulty),
        typeof(BeatmapUserSettings),
        typeof(RealmFile),
        typeof(RealmNamedFileUsage),
        typeof(RealmUser),
        typeof(Ruleset),
        typeof(Skin),
        typeof(Score),
        typeof(BeatmapCollection),
        typeof(ModPreset),
    };

    /// <summary>
    /// Resolves the directory holding <c>client.realm</c> and <c>files/</c>.
    /// Accepts either that directory itself or a direct path to the realm file.
    /// </summary>
    public static string ResolveDataDirectory(string path)
    {
        string full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            if (!string.Equals(Path.GetFileName(full), "client.realm", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{full}' is a file but not a client.realm database.");

            return Path.GetDirectoryName(full)!;
        }

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"osu!lazer data directory not found: {full}");

        if (!File.Exists(Path.Combine(full, "client.realm")))
            throw new FileNotFoundException($"client.realm not found inside '{full}'.");

        return full;
    }

    public static string HashPath(string dataDirectory, string hash)
    {
        if (hash.Length < 2)
            throw new ArgumentException($"'{hash}' is not a valid SHA-256 hash.");

        return Path.Combine(dataDirectory, "files", hash[..1], hash[..2], hash);
    }

    /// <summary>
    /// Opens the database read-only and copies the beatmap sets out of it.
    /// </summary>
    /// <param name="schemaVersion">Schema version to declare; must equal the version inside the file.</param>
    public static LazerSnapshot Read(string dataDirectory, int schemaVersion)
    {
        string dbPath = Path.Combine(dataDirectory, "client.realm");

        var config = new RealmConfiguration(dbPath)
        {
            IsReadOnly = true,
            SchemaVersion = (ulong)schemaVersion,
            Schema = SchemaTypes,
        };

        Realm realm;
        try
        {
            realm = Realm.GetInstance(config);
        }
        catch (RealmException ex)
        {
            throw new LazerSchemaMismatchException(ex.Message, ex);
        }

        try
        {
            return Project(realm, schemaVersion);
        }
        finally
        {
            realm.Dispose();
        }
    }

    private static LazerSnapshot Project(Realm realm, int schemaVersion)
    {
        var sets = new List<SongSnapshot>();
        int deleted = 0;
        int empty = 0;
        int protectedSets = 0;
        int totalNamedFiles = 0;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var setIdsSeen = new HashSet<int>();
        var setIdsSeenWithDuplicates = new List<int>();
        var mapIdsSeen = new HashSet<int>();

        foreach (var set in realm.All<BeatmapSet>())
        {
            if (set.DeletePending)
            {
                deleted++;
                continue;
            }

            // osu!lazer marks its own bundled content (tutorial, internal test maps) as protected.
            // A real stable install has no equivalent, so these are counted for reporting.
            if (set.Protected)
                protectedSets++;

            // Map each difficulty's .osu file hash to its beatmap id, so files can be attributed
            // back to the difficulty they belong to (osu!lazer stores the .osu file itself under
            // BeatmapInfo.Hash).
            var mapIdByHash = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var beatmap in set.Beatmaps)
            {
                if (!string.IsNullOrWhiteSpace(beatmap.Hash))
                    mapIdByHash[beatmap.Hash] = beatmap.OnlineID;
            }

            var files = new List<FileEntry>(set.Files.Count);

            foreach (var usage in set.Files)
            {
                string? hash = usage.File?.Hash;

                // Users can end up with entries that have a blank filename in the Realm;
                // osu!lazer's own exporter skips these, so we do too.
                if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(usage.Filename))
                    continue;

                files.Add(new FileEntry(hash, usage.Filename, mapIdByHash.GetValueOrDefault(hash, -1)));
                hashes.Add(hash);
            }

            if (files.Count == 0)
            {
                empty++;
                continue;
            }

            // Every difficulty of a set shares the set metadata in practice; take the first one
            // that actually carries metadata, falling back to empty strings.
            var firstMeta = set.Beatmaps.Select(b => b.Metadata).FirstOrDefault(m => m != null);

            int[] mapIds = set.Beatmaps.Select(b => b.OnlineID).ToArray();

            sets.Add(new SongSnapshot(
                set.ID,
                set.OnlineID,
                firstMeta?.Artist ?? string.Empty,
                firstMeta?.Title ?? string.Empty,
                mapIds,
                files,
                set.Protected));

            foreach (int mapId in mapIds)
            {
                if (mapId > 0)
                    mapIdsSeen.Add(mapId);
            }

            if (set.OnlineID > 0)
            {
                setIdsSeen.Add(set.OnlineID);
                setIdsSeenWithDuplicates.Add(set.OnlineID);
            }

            totalNamedFiles += files.Count;
        }

        // Online ids are unique in osu!'s database, so a duplicate here means the local Realm holds
        // something unusual (e.g. two entries for one mapset). Report it rather than assume.
        // Sets with OnlineID <= 0 (never submitted online) are excluded - they share -1 by design.
        var duplicateIds = setIdsSeenWithDuplicates
                           .GroupBy(id => id)
                           .Where(g => g.Count() > 1)
                           .Select(g => g.Key)
                           .OrderBy(id => id)
                           .ToArray();

        sets.Sort((a, b) => a.SetId != b.SetId
            ? a.SetId.CompareTo(b.SetId)
            : string.CompareOrdinal(a.Artist, b.Artist));

        return new LazerSnapshot(schemaVersion, sets, deleted, empty, protectedSets, totalNamedFiles,
            hashes.Count, setIdsSeen.Count, mapIdsSeen.Count, duplicateIds.Length, duplicateIds);
    }
}
