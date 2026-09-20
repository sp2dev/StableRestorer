// StableRestorer - restore osu!stable file layout from an osu!lazer Realm database.
//
// The Realm schema below is a faithful mirror of the persisted models in ppy/osu (osu.Game) at
// schema version 52. Realm validates the declared schema against the schema stored inside the
// .realm file, so the type names, [MapTo] target names and property names/types must match the
// game's models exactly - including properties this tool never reads.
//
// Reference: the corresponding model files in the ppy/osu repository (osu.Game), e.g.
// osu.Game/Beatmaps/BeatmapSetInfo.cs, osu.Game/Models/RealmNamedFileUsage.cs and so on.

using Realms;

namespace StableRestorer.RealmSchema;

#region Beatmaps

[MapTo("BeatmapSet")]
public class BeatmapSet : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; } = Guid.NewGuid();

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public DateTimeOffset DateAdded { get; set; }

    public DateTimeOffset? DateSubmitted { get; set; }

    public DateTimeOffset? DateRanked { get; set; }

    public IList<Beatmap> Beatmaps { get; } = null!;

    public IList<RealmNamedFileUsage> Files { get; } = null!;

    public int Status { get; set; } = -3;

    public bool DeletePending { get; set; }

    public string Hash { get; set; } = string.Empty;

    public bool Protected { get; set; }
}

[MapTo("Beatmap")]
public class Beatmap : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; } = Guid.NewGuid();

    public string DifficultyName { get; set; } = string.Empty;

    public Ruleset Ruleset { get; set; } = null!;

    public BeatmapDifficulty Difficulty { get; set; } = null!;

    public BeatmapMetadata Metadata { get; set; } = null!;

    public BeatmapUserSettings UserSettings { get; set; } = null!;

    public BeatmapSet? BeatmapSet { get; set; }

    public int Status { get; set; } = -3;

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public double Length { get; set; }

    public double BPM { get; set; }

    public string Hash { get; set; } = string.Empty;

    public double StarRating { get; set; } = -1;

    [Indexed]
    public string MD5Hash { get; set; } = string.Empty;

    public string OnlineMD5Hash { get; set; } = string.Empty;

    public DateTimeOffset? LastLocalUpdate { get; set; }

    public DateTimeOffset? LastOnlineUpdate { get; set; }

    public bool Hidden { get; set; }

    public int EndTimeObjectCount { get; set; } = -1;

    public int TotalObjectCount { get; set; } = -1;

    public DateTimeOffset? LastPlayed { get; set; }

    public int BeatDivisor { get; set; } = 4;

    public double? EditorTimestamp { get; set; }
}

[MapTo("BeatmapMetadata")]
public class BeatmapMetadata : RealmObject
{
    public string Title { get; set; } = string.Empty;

    public string TitleUnicode { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string ArtistUnicode { get; set; } = string.Empty;

    public RealmUser Author { get; set; } = null!;

    public string Source { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public IList<string> UserTags { get; } = null!;

    public int PreviewTime { get; set; } = -1;

    public string AudioFile { get; set; } = string.Empty;

    public string BackgroundFile { get; set; } = string.Empty;
}

[MapTo("BeatmapDifficulty")]
public class BeatmapDifficulty : EmbeddedObject
{
    public float DrainRate { get; set; } = 5;

    public float CircleSize { get; set; } = 5;

    public float OverallDifficulty { get; set; } = 5;

    public float ApproachRate { get; set; } = 5;

    public double SliderMultiplier { get; set; } = 1.4;

    public double SliderTickRate { get; set; } = 1;
}

public class BeatmapUserSettings : EmbeddedObject
{
    public double Offset { get; set; }
}

#endregion

#region Shared / files

[MapTo("File")]
public class RealmFile : RealmObject
{
    [PrimaryKey]
    public string Hash { get; set; } = string.Empty;
}

public class RealmNamedFileUsage : EmbeddedObject
{
    public RealmFile File { get; set; } = null!;

    public string Filename { get; set; } = null!;
}

public class RealmUser : EmbeddedObject
{
    public int OnlineID { get; set; } = 1;

    public string Username { get; set; } = string.Empty;

    // osu!lazer names the C# property CountryString but stores it as "CountryCode".
    [MapTo("CountryCode")]
    public string CountryCode { get; set; } = string.Empty;
}

[MapTo("Ruleset")]
public class Ruleset : RealmObject
{
    [PrimaryKey]
    public string ShortName { get; set; } = string.Empty;

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public string Name { get; set; } = string.Empty;

    public string InstantiationInfo { get; set; } = string.Empty;

    public int LastAppliedDifficultyVersion { get; set; }

    public bool Available { get; set; }
}

#endregion

#region Skins / scores / collections (declared for schema fidelity and optional future use)

[MapTo("Skin")]
public class Skin : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Creator { get; set; } = string.Empty;

    public string InstantiationInfo { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public bool Protected { get; set; }

    public IList<RealmNamedFileUsage> Files { get; } = null!;

    public bool DeletePending { get; set; }
}

[MapTo("Score")]
public class Score : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public Beatmap? BeatmapInfo { get; set; }

    public string ClientVersion { get; set; } = string.Empty;

    [Indexed]
    public string BeatmapHash { get; set; } = string.Empty;

    public Ruleset Ruleset { get; set; } = null!;

    public IList<RealmNamedFileUsage> Files { get; } = null!;

    public string Hash { get; set; } = string.Empty;

    public bool DeletePending { get; set; }

    public long TotalScore { get; set; }

    public long TotalScoreWithoutMods { get; set; }

    public int TotalScoreVersion { get; set; }

    public long? LegacyTotalScore { get; set; }

    public bool BackgroundReprocessingFailed { get; set; }

    public int MaxCombo { get; set; }

    public double Accuracy { get; set; }

    public DateTimeOffset Date { get; set; }

    public double? PP { get; set; }

    [Indexed]
    public long OnlineID { get; set; } = -1;

    [Indexed]
    public long LegacyOnlineID { get; set; } = -1;

    [MapTo("User")]
    public RealmUser RealmUser { get; set; } = null!;

    [MapTo("Mods")]
    public string ModsJson { get; set; } = string.Empty;

    [MapTo("Statistics")]
    public string StatisticsJson { get; set; } = string.Empty;

    [MapTo("MaximumStatistics")]
    public string MaximumStatisticsJson { get; set; } = string.Empty;

    public IList<int> Pauses { get; } = null!;

    // osu!lazer names the C# property RankInt but stores it as "Rank".
    [MapTo("Rank")]
    public int Rank { get; set; }
}

public class BeatmapCollection : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public string Name { get; set; } = string.Empty;

    public IList<string> BeatmapMD5Hashes { get; } = null!;

    public DateTimeOffset LastModified { get; set; }
}

public class ModPreset : RealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; } = Guid.NewGuid();

    public Ruleset Ruleset { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [MapTo("Mods")]
    public string ModsJson { get; set; } = string.Empty;

    public bool DeletePending { get; set; }
}

#endregion
