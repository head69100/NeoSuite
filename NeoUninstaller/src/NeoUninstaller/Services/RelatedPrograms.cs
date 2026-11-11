using System.Text.RegularExpressions;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

public sealed record RelatedProgram(InstalledProgram Program, string Reason, bool Suggested);

/// <summary>
/// Programmes qui vont avec ceux qu'on désinstalle : Chrome et Google Update, Office et ses modules,
/// les composants NVIDIA ou Adobe… Jamais de pilote, de runtime (.NET, Visual C++) ni de composant Microsoft générique.
/// </summary>
public static partial class RelatedPrograms
{
    public static List<RelatedProgram> Find(IReadOnlyList<InstalledProgram> selected, IReadOnlyList<InstalledProgram> all)
    {
        var chosen = selected.Select(p => p.Key).ToHashSet();
        var found = new Dictionary<string, RelatedProgram>();

        foreach (var program in selected.Where(p => !p.IsForced))
        {
            foreach (var other in all)
            {
                if (chosen.Contains(other.Key) || found.ContainsKey(other.Key) || IsProtected(other))
                    continue;
                if (Related(program, other) is { } match)
                    found[other.Key] = new RelatedProgram(other, match.Reason, match.Suggested);
            }
        }
        return found.Values.OrderBy(r => r.Program.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(10).ToList();
    }

    private static (string Reason, bool Suggested)? Related(InstalledProgram a, InstalledProgram b)
    {
        var an = a.DisplayName;
        var bn = b.DisplayName;

        // Règles connues : proposées d'office
        if (Has(an, "chrome") && Has(bn, "google update", "chrome remote", "google chrome"))
            return ("Composant de Google Chrome", true);
        if (OfficeRegex().IsMatch(an) && OfficeRegex().IsMatch(bn))
            return ("Module de Microsoft Office", true);
        if (Has(an, "nvidia") && !Has(an, "driver", "pilote") && Has(bn, "nvidia") && !Has(bn, "driver", "pilote", "graphics"))
            return ("Composant NVIDIA", true);
        if (Has(a.Publisher, "adobe") && Has(b.Publisher, "adobe") && FamilyKey(an) == FamilyKey(bn))
            return ("Même famille Adobe", true);

        // Règle générale : même éditeur et même premier mot, à vérifier
        if (!string.IsNullOrWhiteSpace(a.Publisher) && string.Equals(a.Publisher, b.Publisher, StringComparison.OrdinalIgnoreCase)
            && !Has(a.Publisher, "microsoft") && FamilyKey(an).Length >= 4 && FamilyKey(an) == FamilyKey(bn))
            return ("Même éditeur et même nom", false);
        return null;
    }

    private static bool IsProtected(InstalledProgram p) =>
        Has(p.DisplayName, "visual c++", ".net", "redistributable", "runtime", "driver", "pilote", "chipset", "directx", "vulkan")
        || p.IsForced;

    private static string FamilyKey(string name) =>
        name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";

    private static bool Has(string? text, params string[] words) =>
        text is not null && words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(microsoft (365|office)|office 16|microsoft onenote|microsoft outlook|microsoft access|microsoft excel|microsoft word|microsoft powerpoint)", RegexOptions.IgnoreCase)]
    private static partial Regex OfficeRegex();
}