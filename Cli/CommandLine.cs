namespace StableRestorer.Cli;

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message)
    {
    }
}

public static class CommandLine
{
    public const string ToolName = "stablerestorer";

    public static string Version =>
        typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Usage => $$"""
        {{ToolName}} {{Version}} - restore an osu!stable file layout from an osu!lazer Realm database.

        USAGE
          {{ToolName}}                            interactive mode (also: {{ToolName}} interactive)
          {{ToolName}} restore --lazer <dir> --out <dir> [options]
          {{ToolName}} scan    --lazer <dir> [options]
          {{ToolName}} schemas --lazer <dir> [--schema-version <n>]

        COMMANDS
          (none)    With no arguments and a terminal attached, starts the interactive prompt.
                    This is what running the executable by double-clicking does.
          scan      Read client.realm read-only and report what could be restored (no writes).
          restore   Rebuild the stable Songs/ layout in --out.
          schemas   Probe which Realm schema version a client.realm uses; on failure, list the
                    declared properties that do not match the file.

        OPTIONS
          --lazer <dir>          osu!lazer data directory containing client.realm and files/.
          --out <dir>            Output directory (receives Songs/). Required for restore.
          --stable <dir>         An existing osu!stable install whose Songs/ layout should be
                                 consulted. Defaults to --out.
          --keep-unsubmitted     Also import beatmap sets that have no online id (never submitted to
                                 osu!). Such maps have no stable equivalent and would clutter the
                                 library, so they are skipped by default.
          --no-reuse-existing    Ignore folders that already exist in --stable and always create
                                 folders under this tool's own naming scheme.
          --mode <hardlink|copy> How files are materialised. Default: hardlink
                                 (falls back to copy when the volumes differ).
          --schema-version <n>   Realm schema version. Default: auto-detect (tries 52, 51, 50 ...).
          --dry-run              Plan everything, write nothing. Implies verification of mappings.
          --overwrite            Replace a destination file that is not already the same file.
                                 Default: leave it alone and report it.
          --no-verify-hash       Skip SHA-256 verification of source files against their names.
          --report <file>        Write a JSON report (default: <out>/stablerestorer-report.json).
          --verbose              Per-package and per-file detail.
          --quiet                Summary only.
          -h, --help             Show this help.

        IDENTIFIERS
          A beatmap set is identified by BeatmapSetInfo.OnlineID (the "set id"); each difficulty is
          identified by BeatmapInfo.OnlineID (the "map id"). Both are unique in osu!'s database.
          Stable names a folder "{setId} {artist} - {title}", so folders are keyed on the set id:
          when --stable already has a folder for that set id, that folder is reused (and merely
          filled in) instead of a second folder being created under a different name.

        SAFETY
          * The lazer data directory is opened read-only and is never written to.
          * --out must not be the lazer data directory, inside it, or a parent of it.
          * Destinations that are already the same file (an existing hard link) are skipped.
          * Nothing is ever deleted from --stable; folders are only added to or filled in.
          * Hard links share the file content with lazer's store: deleting one side does not
            remove the data, but editing one side edits both.
        """;
}
