namespace WyvernCenter;

/// <summary>Where people can support the project (also in .github/FUNDING.yml and the README).</summary>
internal static class SupportLinks
{
    public const string BuyMeACoffee = "https://buymeacoffee.com/noptic";
    public const string Patreon = "https://www.patreon.com/Noptic";

    /// <summary>
    /// Opens a fixed link in the user's browser. Wyvern Center runs elevated, and a browser started straight from it would
    /// run elevated too; explorer.exe hands the link to the user's own (non-elevated) shell instead. Full path, so a
    /// bare name can't pick up an explorer.exe from the app's folder.
    /// </summary>
    public static void Open(string url)
    {
        if (url != BuyMeACoffee && url != Patreon) return; // only these two, never anything passed in from elsewhere
        try
        {
            System.Diagnostics.Process.Start(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{url}\"");
        }
        catch { }
    }
}
