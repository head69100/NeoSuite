using NeoDriver.Models;

namespace NeoDriver.Services;

/// <summary>Mises à jour de pilotes masquées (« ne plus proposer ») : Windows Update les met de côté, on peut les réafficher.</summary>
public static class HiddenDriverService
{
    /// <summary>Masque une mise à jour. Windows peut exiger les droits administrateur pour cela.</summary>
    public static Task HideAsync(DriverItem item) => Task.Run(() =>
    {
        dynamic update = item.Update ?? throw new InvalidOperationException("Mise à jour introuvable.");
        update.IsHidden = true;
    });

    public static Task<List<DriverItem>> GetHiddenAsync() => Task.Run(() =>
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("Le service Windows Update est indisponible sur ce PC.");
        dynamic session = Activator.CreateInstance(sessionType)!;
        session.ClientApplicationID = "NeoDriver";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=1");

        var items = new List<DriverItem>();
        dynamic updates = result.Updates;
        int count = updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic update = updates[i];
            items.Add(new DriverItem
            {
                UpdateId = update.Identity.UpdateID,
                Title = update.Title,
                DeviceName = (string)(update.DriverModel ?? update.Title),
                Manufacturer = update.DriverManufacturer ?? update.DriverProvider ?? "Inconnu",
                NewDate = update.DriverVerDate is DateTime d ? d : null,
                Update = update,
            });
        }
        return items;
    });

    public static Task ShowAgainAsync(DriverItem item) => Task.Run(() =>
    {
        dynamic update = item.Update ?? throw new InvalidOperationException("Mise à jour introuvable.");
        update.IsHidden = false;
    });
}
