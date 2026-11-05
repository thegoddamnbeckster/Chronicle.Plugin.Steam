using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Steam;

/// <summary>The one settings form, shown for both halves of the plugin. The account box is the one people get wrong, so it has its own numbered callout.</summary>
internal static class SteamSettingsSchema
{
    public static PluginSettingsSchema Build() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = "notice_key", Label = "Step 1 of 3: get a free Steam Web API key", Type = SettingType.Notice,
                Description = "1. Open https://steamcommunity.com/dev/apikey and sign in.\n" +
                              "2. In Domain Name type: localhost\n" +
                              "3. Tick the agreement box and click Register.\n" +
                              "4. Copy the 32-character key that Steam shows, and paste it in the first box below.\n" +
                              "The key is saved encrypted, never logged, and sent only to Steam.",
            },
            new SettingDefinition { Key = Settings.KeyApiKey, Label = "Steam Web API key (32 characters)", Type = SettingType.Password, Required = true,
                Description = "The key from step 1." },

            new SettingDefinition
            {
                Key = "notice_id", Label = "Step 2 of 3: find your Steam ID. It is a NUMBER, not your name.", Type = SettingType.Notice,
                Description = "Your Steam ID is a number with 17 digits, like 76561198012345678.\n" +
                              "1. In a web browser go to https://steamcommunity.com and sign in.\n" +
                              "2. Click your name at the top, then Profile.\n" +
                              "3. Look at the ADDRESS BAR. It reads steamcommunity.com/profiles/ followed by 17 digits, for example\n" +
                              "    steamcommunity.com/profiles/76561198012345678\n" +
                              "4. Copy those 17 digits into the box below. Pasting the whole address works too.\n" +
                              "If the address reads steamcommunity.com/id/ followed by a word instead, type that word.\n" +
                              "DO NOT type the name shown at the top of your profile (your display name). Steam cannot look that up, and the import will fail.",
            },
            new SettingDefinition { Key = Settings.KeySteamId, Label = "Steam ID (17 digits, NOT your name)", Type = SettingType.Text, Required = true,
                Description = "Example: 76561198012345678. Or the whole profile address." },

            new SettingDefinition
            {
                Key = "notice_public", Label = "Step 3 of 3: make your games visible to Steam's API", Type = SettingType.Notice,
                Description = "In the Steam app or on steamcommunity.com: your name, Edit Profile, Privacy Settings. Set My profile to Public and Game details to Public.\n" +
                              "Steam lists a library only for a public profile. You can set it back to private after the import finishes.",
            },

            new SettingDefinition { Key = Settings.KeyLanguage, Label = "Language", Type = SettingType.Text, DefaultValue = "english",
                Description = "The language for store text and achievement names, as Steam names it (english, french, german, ...)." },
            new SettingDefinition { Key = Settings.KeyCountry, Label = "Store country", Type = SettingType.Text, DefaultValue = "us",
                Description = "Two-letter country for store pricing and availability (us, ca, gb, ...)." },
        ],
    };
}
