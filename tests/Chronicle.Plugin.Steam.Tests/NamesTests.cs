using System.Text.Json;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

public class NamesTests
{
    [Fact]
    public void Trademark_marks_inside_a_title_give_a_name_without_them()
    {
        var alt = SteamNames.Alternates("Dishonored®: Death of the Outsider™");
        Assert.Contains("Dishonored: Death of the Outsider", alt);
    }

    [Fact]
    public void A_roman_numeral_is_also_offered_as_a_number_and_back()
    {
        Assert.Contains("Sid Meier's Civilization 6", SteamNames.Alternates("Sid Meier's Civilization VI"));
        Assert.Contains("Baldur's Gate III", SteamNames.Alternates("Baldur's Gate 3"));
        Assert.Contains("Grand Theft Auto 5", SteamNames.Alternates("Grand Theft Auto V"));
        Assert.Contains("Final Fantasy XIII", SteamNames.Alternates("Final Fantasy 13"));
    }

    [Fact]
    public void Single_letters_that_are_words_are_never_swapped()
    {
        Assert.DoesNotContain(SteamNames.Alternates("Mega Man X"), n => n.Contains("10"));
        Assert.DoesNotContain(SteamNames.Alternates("Project I"), n => n.Contains('1'));
        Assert.Empty(SteamNames.Alternates("Half-Life"));
    }

    [Fact]
    public void An_ampersand_is_also_offered_as_and()
    {
        Assert.Contains("Sam and Max Save the World", SteamNames.Alternates("Sam & Max Save the World"));
    }

    [Fact]
    public void The_title_itself_is_never_repeated_and_nothing_is_dropped()
    {
        var t = "Civilization VI™";
        var alt = SteamNames.Alternates(t);
        Assert.DoesNotContain(t, alt);
        Assert.Equal(alt.Count, alt.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Civilization VI", alt);
        Assert.Contains("Civilization 6", alt);
    }

    [Fact]
    public void A_library_entry_and_the_store_record_both_carry_the_names()
    {
        var owned = JsonDocument.Parse("{\"appid\":289070,\"name\":\"Sid Meier's Civilization VI\",\"playtime_forever\":0}").RootElement;
        Assert.Contains("Sid Meier's Civilization 6", SteamMapper.Owned(owned).AlternateNames!);

        var store = JsonDocument.Parse("{\"name\":\"Sid Meier's Civilization VI\",\"type\":\"game\"}").RootElement;
        var m = SteamMapper.Details("289070", store, null);
        Assert.Contains("Sid Meier's Civilization 6", m.AlternateNames);
    }
}
