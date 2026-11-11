namespace NeoDriver.Services;

public static class WebLinks
{
    /// <summary>Ouvre une adresse dans le navigateur par défaut.</summary>
    public static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Pas de navigateur par défaut : rien à faire
        }
    }

    /// <summary>Recherche web de ce texte.</summary>
    public static void Search(string query) => Open("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
}
