using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Steam;

/// <summary>
/// The other names a person might type for a Steam title. Chronicle's search matches a title's name and its aliases, and a store
/// title carries marks and numerals people do not type: "Dishonored®: Death of the Outsider™", "Sid Meier's Civilization VI",
/// "Sam &amp; Max". The title itself is kept exactly as Steam gives it; these are extra names beside it, never a replacement.
/// </summary>
internal static class SteamNames
{
    private static readonly Regex Marks = new("[™®©]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex Roman = new("\\b(IX|IV|V?I{1,3}|VI{0,3}|XI{1,3}|XIV|XV|XVI{0,3}|XIX|XX)\\b", RegexOptions.Compiled);
    private static readonly Regex Arabic = new("\\b([2-9]|1[1-9]|20)\\b", RegexOptions.Compiled);

    // "I" and "X" alone are ordinary words and letters ("Mega Man X", "Project I.G.I."), so neither is swapped; 1 and 10 likewise.
    private static readonly string[] RomanForm = ["", "", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX"];

    public static List<string> Alternates(string title)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title.Trim() };

        void Add(string s)
        {
            s = Spaces.Replace(s, " ").Trim();
            if (s.Length >= 2 && seen.Add(s)) result.Add(s);
        }

        var bases = new List<string> { title };
        var clean = Marks.Replace(title, "");
        if (clean != title) { Add(clean); bases.Add(clean); }

        foreach (var b in bases)
        {
            if (b.Contains('&')) Add(b.Replace("&", "and"));
            Add(Roman.Replace(b, m => ToArabic(m.Value) is { } n ? n.ToString() : m.Value));
            Add(Arabic.Replace(b, m => RomanForm[int.Parse(m.Value)] is { Length: > 0 } r ? r : m.Value));
        }
        return result;
    }

    private static int? ToArabic(string roman) => roman switch
    {
        "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6, "VII" => 7, "VIII" => 8, "IX" => 9,
        "XI" => 11, "XII" => 12, "XIII" => 13, "XIV" => 14, "XV" => 15, "XVI" => 16, "XVII" => 17, "XVIII" => 18, "XIX" => 19, "XX" => 20,
        _ => null,
    };
}
