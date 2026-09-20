using System.Text;

namespace StableRestorer.Cli;

/// <summary>
/// Best-effort reader for osu!stable's <c>osu!.db</c>, used to learn which folder name stable itself
/// recorded for a beatmap set (helpful for the few folders whose names carry no set id).
///
/// STATUS: experimental and not reliable yet. osu! writes two different string encodings and a
/// 7-vs-8 byte timestamp depending on build, and getting it wrong silently shifts every following
/// field, so this reader validates itself and refuses to report anything it is not sure about
/// rather than handing back wrong folder names.
/// </summary>
public static class OsuDb
{
    public sealed record Entry(string FolderName, int BeatmapId, int SetId, string DifficultyName, string OsuFileName);

    public sealed record Summary(int Version, int FolderCount, string PlayerName, int BeatmapCount, IReadOnlyList<Entry> Entries)
    {
        /// <summary>Distinct folder names, in the order stable stored them.</summary>
        public IEnumerable<string> FolderNames => Entries.Select(e => e.FolderName).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Thrown when the file does not parse cleanly; the caller should fall back.</summary>
    public sealed class FormatNotUnderstoodException : Exception
    {
        public FormatNotUnderstoodException(string message) : base(message)
        {
        }
    }

    public static Summary Read(string osuDbPath)
    {
        using var stream = File.OpenRead(osuDbPath);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int version = reader.ReadInt32();

        if (version < 20191106)
            throw new FormatNotUnderstoodException($"osu!.db version {version} predates the layout this reader understands (20191106).");

        int folderCount = reader.ReadInt32(); // folder count (matches the folder count on disk)
        reader.ReadInt32(); // account unlocked
        reader.ReadBytes(8); // timestamp

        // The player name is length-prefixed. Anything other than a plausible name here means the
        // header layout differs from what this reader assumes, so stop instead of shifting every
        // subsequent field.
        string playerName = ReadString(reader);

        if (playerName.Length is 0 or > 64 || playerName.Any(c => char.IsControl(c)))
            throw new FormatNotUnderstoodException($"player name field looks wrong ('{playerName}'), header layout not understood.");

        int beatmapCount = reader.ReadInt32();

        if (beatmapCount <= 0 || beatmapCount > 1_000_000)
            throw new FormatNotUnderstoodException($"beatmap count {beatmapCount} is implausible, header layout not understood.");

        var entries = new List<Entry>(beatmapCount);

        for (int i = 0; i < beatmapCount; i++)
        {
            Entry entry;

            try
            {
                entry = ReadEntry(reader);
            }
            catch (Exception ex)
            {
                throw new FormatNotUnderstoodException(
                    $"entry {i + 1}/{beatmapCount} could not be decoded at offset {stream.Position}: {ex.Message}");
            }

            entries.Add(entry);
        }

        // A sanity check that catches a silently shifted layout: stable's own folder count must
        // agree with the distinct folders actually decoded.
        int distinct = entries.Select(e => e.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (folderCount != distinct)
            throw new FormatNotUnderstoodException($"decoded {distinct} distinct folders but the header declares {folderCount}; layout not understood.");

        return new Summary(version, folderCount, playerName, beatmapCount, entries);
    }

    private static Entry ReadEntry(BinaryReader reader)
    {
        ReadString(reader); // artist
        ReadString(reader); // artist (unicode)
        ReadString(reader); // title
        ReadString(reader); // title (unicode)
        ReadString(reader); // creator

        reader.ReadByte(); // ranked status
        reader.ReadInt16(); // circle count
        reader.ReadInt16(); // slider count
        reader.ReadInt16(); // spinner count
        reader.ReadInt64(); // last modified
        reader.ReadSingle(); // approach rate
        reader.ReadSingle(); // circle size
        reader.ReadSingle(); // hp drain
        reader.ReadSingle(); // overall difficulty

        reader.ReadDouble(); // slider velocity
        ReadIntDoublePairs(reader); // slider velocity per difficulty setting
        ReadIntDoublePairs(reader); // slider tick rate per difficulty setting

        reader.ReadInt32(); // drain time
        reader.ReadInt32(); // total time
        reader.ReadInt32(); // preview time

        int timingPoints = reader.ReadInt32();

        for (int i = 0; i < timingPoints; i++)
        {
            reader.ReadDouble(); // bpm
            reader.ReadDouble(); // offset
            reader.ReadBoolean(); // inherited
        }

        reader.ReadInt32(); // difficulty id
        int beatmapId = reader.ReadInt32();
        int setId = reader.ReadInt32();
        reader.ReadInt32(); // thread id
        reader.ReadByte(); // grade standard
        reader.ReadByte(); // grade taiko
        reader.ReadByte(); // grade catch
        reader.ReadByte(); // grade mania
        reader.ReadInt16(); // local offset
        reader.ReadSingle(); // stack leniency
        reader.ReadByte(); // ruleset
        ReadString(reader); // source
        ReadString(reader); // tags
        reader.ReadInt16(); // online offset
        ReadString(reader); // title font
        reader.ReadBoolean(); // unplayed
        reader.ReadInt64(); // last played
        reader.ReadBoolean(); // is osz2
        string folderName = ReadString(reader);
        reader.ReadInt64(); // last checked against online

        reader.ReadBoolean(); // ignore beatmap sounds
        reader.ReadBoolean(); // ignore beatmap skin
        reader.ReadBoolean(); // disable storyboard
        reader.ReadBoolean(); // disable video
        reader.ReadBoolean(); // visual override
        reader.ReadInt32(); // last modification (unused by this version)
        reader.ReadByte(); // mania scroll speed

        string difficultyName = ReadNullTerminated(reader);
        ReadNullTerminated(reader); // audio file
        string osuFileName = ReadNullTerminated(reader);

        return new Entry(folderName, beatmapId, setId, difficultyName, osuFileName);
    }

    private static void ReadIntDoublePairs(BinaryReader reader)
    {
        int count = reader.ReadInt32();

        for (int i = 0; i < count; i++)
        {
            reader.ReadInt32();
            reader.ReadDouble();
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        byte length = reader.ReadByte();

        return length switch
        {
            0x00 => string.Empty,
            0x0b => Encoding.UTF8.GetString(reader.ReadBytes(Read7BitEncodedInt(reader))),
            _ => Encoding.UTF8.GetString(reader.ReadBytes(length)),
        };
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int result = 0;
        int shift = 0;

        while (true)
        {
            byte b = reader.ReadByte();
            result |= (b & 0x7f) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }
    }

    private static string ReadNullTerminated(BinaryReader reader)
    {
        var bytes = new List<byte>();

        while (true)
        {
            int b = reader.BaseStream.ReadByte();

            if (b <= 0)
                return Encoding.UTF8.GetString(bytes.ToArray());

            bytes.Add((byte)b);
        }
    }
}
