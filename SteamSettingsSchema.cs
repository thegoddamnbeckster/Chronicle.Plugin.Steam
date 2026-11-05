using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Steam;

/// <summary>The one settings form, shown for both halves of the plugin.</summary>
internal static class SteamSettingsSchema
{
    public static PluginSettingsSchema Build() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = "notice", Label = "Getting a key", Type = SettingType.Notice,
                Description = "A Steam Web API key is free. Sign in at steamcommunity.com/dev/apikey, enter any domain name (for example localhost) and register. " +
                              "Then make your Steam profile and its Game details public (Steam: Edit Profile, Privacy Settings) at least while Chronicle imports: " +
                              "Steam lists a library only for a public profile. The key is a password: it is saved encrypted, never logged and sent only to Steam.",
            },
            new SettingDefinition { Key = Settings.KeyApiKey, Label = "Steam Web API key", Type = SettingType.Password, Required = true,
                Description = "The 32-character key from steamcommunity.com/dev/apikey." },
            new SettingDefinition { Key = Settings.KeySteamId, Label = "Steam account", Type = SettingType.Text, Required = true,
                Description = "Your 17-digit Steam ID, your profile address (steamcommunity.com/profiles/... or /id/...) or your custom profile name." },
            new SettingDefinition { Key = Settings.KeyLanguage, Label = "Language", Type = SettingType.Text, DefaultValue = "english",
                Description = "The language for store text and achievement names, as Steam names it (english, french, german, ...)." },
            new SettingDefinition { Key = Settings.KeyCountry, Label = "Store country", Type = SettingType.Text, DefaultValue = "us",
                Description = "Two-letter country for store pricing and availability (us, ca, gb, ...)." },
        ],
    };
}
