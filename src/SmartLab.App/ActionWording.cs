namespace SmartLab.App;

/// <summary>
/// The words on a button that acts on whatever the operator ticked.
/// </summary>
/// <remarks>
/// <para>
/// Named for the act, not for the mechanic that chose what it acts on. Every one of
/// these buttons used to read "<i>verb</i> ticked" - Clean ticked, Apply ticked, Empty
/// ticked - which describes the checkboxes rather than the machine. Somebody deciding
/// whether to press it is asking what will happen to their computer, and "ticked" is a
/// word about the list.
/// </para>
/// <para>
/// The count goes in the label because it is the part of the answer the verb cannot
/// carry. "Empty" is a promise of unknown size; "Empty 3 bins" is a decision somebody
/// can make without counting the rows themselves - and when nothing is ticked the
/// number would be a nought, so the bare verb is shown and the button stays quiet.
/// </para>
/// </remarks>
public static class ActionWording
{
    /// <summary>"Empty", or "Empty 1 bin", or "Empty 3 bins".</summary>
    public static string For(string verb, int count, string noun) =>
        count == 0 ? verb : $"{verb} {count} {Plural(noun, count)}";

    /// <remarks>
    /// Enough English for the nouns this application actually uses - file, entry, bin,
    /// place, item, app, driver, program. Not a general pluraliser: one that guesses at
    /// every English noun is a large thing to maintain for eight words.
    /// </remarks>
    private static string Plural(string noun, int count) =>
        count == 1 ? noun
        : noun.EndsWith('y') ? $"{noun[..^1]}ies"
        : $"{noun}s";
}
