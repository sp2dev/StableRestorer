using System.Text;

namespace StableRestorer.IO;

/// <summary>
/// Reproduces the naming osu!stable itself uses for song folders, keeping the rules in one place
/// so they can be swapped out without touching the restore logic.
/// </summary>
public static class StableNaming
{
    /// <summary>Maximum length of a single path component that stable can reliably handle.</summary>
    private const int max_component_length = 155;

    private static readonly char[] invalid_chars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Stable folder name: <c>{setId} {artist} - {title}</c>, e.g.
    /// <c>1012649 aran &amp; Kobaryo - While Shining feat yukacco</c>.
    /// </summary>
    /// <param name="setId">
    /// <c>BeatmapSetInfo.OnlineID</c> (the beatmap <i>set</i> id). Sets that were never submitted
    /// online have -1 and fall back to <c>0</c>, matching what osu!lazer writes in that case.
    /// </param>
    public static string BeatmapFolderName(int setId, string artist, string title)
    {
        string id = setId > 0 ? setId.ToString() : "0";

        artist = Sanitise(artist).Trim();
        title = Sanitise(title).Trim();

        if (artist.Length == 0 && title.Length == 0)
            return id;

        if (title.Length == 0)
            return $"{id} {artist}";

        if (artist.Length == 0)
            return $"{id} {title}";

        string combined = $"{id} {artist} - {title}";

        if (combined.Length <= max_component_length)
            return combined;

        // Trim the artist first (it is the least identifying part), then let the title use the rest.
        string trimmedArtist = Truncate(artist, 90);
        int titleBudget = Math.Max(10, max_component_length - id.Length - trimmedArtist.Length - 3);

        return $"{id} {trimmedArtist} - {Truncate(title, titleBudget)}";
    }

    /// <summary>
    /// Extracts the beatmap set id from an existing stable folder name. osu!stable prefixes the
    /// folder with the set id (<c>{setId} {artist} - {title}</c>); folders from the pre-2010 era are
    /// named without a prefix and yield <see langword="null"/>.
    /// </summary>
    public static int? TryParseSetIdFromFolderName(string folderName)
    {
        if (string.IsNullOrEmpty(folderName))
            return null;

        int i = 0;

        while (i < folderName.Length && char.IsAsciiDigit(folderName[i]))
            i++;

        // Require the id to be either the whole name or followed by a space, so "1a2b" and
        // "Erehamonika ..." are rejected while "1234" and "1234 Artist - Title" are accepted.
        if (i == 0 || (i < folderName.Length && folderName[i] != ' '))
            return null;

        return int.TryParse(folderName.AsSpan(0, i), out int setId) && setId > 0 ? setId : null;
    }

    /// <summary>
    /// Skin folder name. osu!stable uses the skin's display name verbatim, sanitised for the file
    /// system; a skin whose name sanitises to nothing falls back to its creator, then to a default.
    /// </summary>
    public static string SkinFolderName(string name, string creator)
    {
        string sanitised = Sanitise(name).Trim();

        if (sanitised.Length == 0)
            sanitised = Sanitise(creator).Trim();

        if (sanitised.Length == 0)
            sanitised = "未命名皮肤";

        return Truncate(sanitised, max_component_length).TrimEnd(' ', '.');
    }

    /// <summary>
    /// Replay filename, matching what osu!stable writes:
    /// <c>{player} - {artist} - {title} [{difficulty}] ({yyyy-MM-dd}) {ruleset}[-n]</c>.
    ///
    /// The ruleset token is derived from the ruleset's short name but mapped the way stable names
    /// its folders: <c>fruits</c> becomes <c>Catch</c>, <c>mania</c> becomes <c>OsuMania</c>.
    /// <paramref name="duplicateIndex"/> appends <c>-2</c>, <c>-3</c>… when several scores share the
    /// same player, beatmap and date.
    ///
    /// The ruleset token and the extension are reserved space before truncating, so a long title
    /// shortens the title instead of eating the part that identifies the ruleset.
    /// </summary>
    public static string ReplayFileName(
        string player,
        string artist,
        string title,
        string difficulty,
        DateTimeOffset date,
        string rulesetShortName,
        int duplicateIndex)
    {
        string ruleset = RulesetToken(rulesetShortName);

        string body = $"{Sanitise(player)} - {Sanitise(artist)} - {Sanitise(title)} " +
                      $"[{Sanitise(difficulty)}] ({date.LocalDateTime:yyyy-MM-dd})";

        string suffix = ruleset.Length > 0 ? $" {ruleset}" : string.Empty;

        if (duplicateIndex > 1)
            suffix += $"-{duplicateIndex}";

        suffix += ".osr";

        int bodyBudget = max_component_length - suffix.Length;

        if (bodyBudget < 10)
            bodyBudget = 10;

        return Truncate(body.Trim(), bodyBudget).TrimEnd(' ', '.') + suffix;
    }

    /// <summary>Maps a ruleset short name to the token osu!stable puts in replay filenames.</summary>
    public static string RulesetToken(string shortName) => shortName.ToLowerInvariant() switch
    {
        "" => string.Empty,
        "osu" => "Osu",
        "taiko" => "Taiko",
        "fruits" or "catch" => "Catch",
        "mania" => "OsuMania",
        _ => Sanitise(shortName),
    };

    /// <summary>
    /// 判断一个已存在的 stable 皮肤文件夹是否就是某个 lazer 皮肤。
    ///
    /// stable 自己会在导入时给文件夹名补上来源，例如 lazer 里叫 <c>X</c> 的皮肤在磁盘上可能是
    /// <c>X (hobby)</c> 或 <c>X (by someone)</c>。为了把文件补进同一个文件夹而不是另建一份，
    /// 这里做保守匹配：完全相同，或以 <c>X (</c> 开头（即后面紧跟一个括号补充说明）。
    /// </summary>
    public static bool SkinFolderMatchesName(string folderName, string skinName)
    {
        string target = Sanitise(skinName).Trim();

        if (target.Length == 0 || folderName.Length == 0)
            return false;

        if (folderName.Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;

        return folderName.StartsWith(target + " (", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes characters that cannot appear in a path component and trims the trailing dot/space
    /// combinations Windows silently strips (which would otherwise break link/path round-tripping).
    /// </summary>
    public static string Sanitise(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var sb = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            if (c < 32)
                continue;

            sb.Append(Array.IndexOf(invalid_chars, c) >= 0 ? '_' : c);
        }

        return sb.ToString().TrimEnd(' ', '.');
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
