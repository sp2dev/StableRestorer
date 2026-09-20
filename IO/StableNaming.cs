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
