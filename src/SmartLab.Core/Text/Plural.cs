namespace SmartLab.Core.Text;

/// <summary>
/// A count and the noun it counts, in English rather than in shorthand.
/// </summary>
/// <remarks>
/// <para>
/// Every line this application shows used to hedge: "3 file(s) match", "1 entr(ies)
/// run at logon", "5 categor(ies)". The bracket is the program admitting it did not
/// know the number when the sentence was written - but it does know it by the time
/// anybody reads it, and the reader is left doing agreement the machine could have
/// done. "1 file(s)" is also simply wrong in a way that makes a tool look unfinished.
/// </para>
/// <para>
/// Not a general pluraliser. It knows the regular -s, the -es that follows a sibilant,
/// and the -y that becomes -ies, which covers every noun this application counts.
/// A word with an irregular plural would have to be written out by whoever needs one.
/// </para>
/// </remarks>
public static class Plural
{
    /// <summary>"1 file", "3 files", "128,035 files".</summary>
    /// <remarks>
    /// Grouped, because a file count can run to six figures and 128035 is a number
    /// somebody has to stop and read digit by digit.
    /// </remarks>
    public static string Of(long count, string noun) => $"{count:N0} {Word(count, noun)}";

    /// <summary>The verb form that agrees with the count: Verb(1, "runs", "run").</summary>
    /// <remarks>
    /// Needed because English puts the -s on the other word: one entry runs, five
    /// entries run. Without it a sentence that counts correctly still reads "1 package
    /// have a newer version", which is the same fault the brackets had.
    /// </remarks>
    public static string Verb(long count, string singular, string plural) =>
        count == 1 ? singular : plural;

    /// <summary>The noun alone, in the number the count calls for.</summary>
    public static string Word(long count, string noun) =>
        count == 1 ? noun
        : noun.EndsWith('y') ? $"{noun[..^1]}ies"
        : Sibilant(noun) ? $"{noun}es"
        : $"{noun}s";

    /// <remarks>
    /// The endings that cannot take a bare -s without running two hisses together:
    /// fix becomes fixes, pass becomes passes.
    /// </remarks>
    private static bool Sibilant(string noun) =>
        noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith('z')
        || noun.EndsWith("ch", StringComparison.Ordinal)
        || noun.EndsWith("sh", StringComparison.Ordinal);
}
