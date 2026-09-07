namespace Sim.Save;

/// Turns a player-typed save name into a file name.
///
/// Save names come from a text box, and they become paths. Anything that could
/// climb out of the save directory or confuse the filesystem is stripped rather
/// than escaped: escaping invites an argument about whether the escaping is
/// complete, and no legitimate save name needs a slash or a dot in it.
public static class SaveNames
{
    public const string Fallback = "save";

    /// Longer than this and the name stops being a name and starts being a way
    /// to hit a path length limit on some platform.
    public const int MaxLength = 64;

    public static string Sanitise(string name)
    {
        var kept = new System.Text.StringBuilder();

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
                kept.Append(c);

            if (kept.Length == MaxLength) break;
        }

        var cleaned = kept.ToString().Trim();
        return cleaned.Length == 0 ? Fallback : cleaned;
    }
}
