using NeoSoft.Models;

namespace NeoSoft.Services;

/// <summary>
/// Les logiciels proposés par NeoSoft. Pour en ajouter un : son identifiant winget
/// (vérifiable avec « winget show --id &lt;Id&gt; -e »), une description et le site de l'éditeur.
/// </summary>
public static class SoftwareCatalog
{
    public static IReadOnlyList<string> Categories { get; } =
        ["Suite Neo", "Navigateurs", "Communication", "Jeux", "Multimédia", "Création", "Bureautique", "Compression", "Développement", "Utilitaires", "Sécurité"];

    /// <summary>Nom sous lequel Windows liste le logiciel, quand il diffère du nom affiché par NeoSoft.</summary>
    private static readonly Dictionary<string, string> WindowsNames = new()
    {
        ["Mozilla.Firefox"] = "Mozilla Firefox",
        ["VideoLAN.VLC"] = "VLC media player",
        ["Microsoft.VisualStudioCode"] = "Microsoft Visual Studio Code",
        ["Zoom.Zoom"] = "Zoom Workplace",
        ["Telegram.TelegramDesktop"] = "Telegram Desktop",
        ["CPUID.CPU-Z"] = "CPUID CPU-Z",
        ["CodecGuide.K-LiteCodecPack.Standard"] = "K-Lite Codec Pack",
        ["Adobe.Acrobat.Reader.64-bit"] = "Adobe Acrobat",
        ["Oracle.JavaRuntimeEnvironment"] = "Java 8",
        ["dotPDN.PaintDotNet"] = "paint.net",
        ["Python.Python.3.13"] = "Python 3.13",
    };

    public static List<SoftwareItem> Load()
    {
        var items = All();
        foreach (var item in items)
            item.DisplayName = WindowsNames.GetValueOrDefault(item.Id);
        return items;
    }

    private static List<SoftwareItem> All() =>
    [
        // Applis de la suite Neo (installées par NeoSuite, en priorité dans le catalogue)
        new()
        {
            Id = "NeoUninstaller",
            Name = "NeoUninstaller",
            Publisher = "Neo",
            Description = "Désinstalle proprement les programmes, retrouve les restes oubliés et signale les pilotes inutiles.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoBoost",
            Name = "NeoBoost",
            Publisher = "Neo",
            Description = "Voit ce qui consomme le processeur et la mémoire, et active le mode Boost en un clic.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoDriver",
            Name = "NeoDriver",
            Publisher = "Neo",
            Description = "Cherche et installe les mises à jour des pilotes de votre matériel.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoRar",
            Name = "NeoRar",
            Publisher = "Neo",
            Description = "Ouvre RAR, ZIP, 7z, ISO… et crée des archives 7z ou ZIP protégées. « Extraire ici » en clic droit.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoMedia",
            Name = "NeoMedia",
            Publisher = "Neo",
            Description = "Lit toutes les vidéos et musiques avec le moteur de VLC : sous-titres, plein écran, reprise de lecture.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoSound",
            Name = "NeoSound",
            Publisher = "Neo",
            Description = "Duplique le son du PC vers un casque filaire et un casque Bluetooth en même temps, sans latence gênante.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },
        new()
        {
            Id = "NeoTorrent",
            Name = "NeoTorrent",
            Publisher = "Neo",
            Description = "Lit les fichiers .torrent et les liens magnet, télécharge et partage.",
            Category = "Suite Neo",
            Domain = "",
            IsNeo = true,
        },

        // Navigateurs
        Item("Google.Chrome", "Google Chrome", "Google", "Le navigateur le plus utilisé, rapide et synchronisé avec votre compte Google.", "Navigateurs", "google.com",
             "Google.Chrome.EXE", "Google.Chrome.Beta", "Google.Chrome.Beta.EXE", "Google.Chrome.Dev", "Google.Chrome.Canary"),
        Item("Mozilla.Firefox", "Firefox", "Mozilla", "Navigateur libre et respectueux de la vie privée.", "Navigateurs", "firefox.com",
             "Mozilla.Firefox.fr", "Mozilla.Firefox.ESR", "Mozilla.Firefox.DeveloperEdition"),
        Item("Brave.Brave", "Brave", "Brave Software", "Navigateur qui bloque publicités et traqueurs par défaut.", "Navigateurs", "brave.com"),
        Item("Opera.OperaGX", "Opera GX", "Opera", "Navigateur pensé pour les joueurs, avec limiteur de ressources.", "Navigateurs", "opera.com"),

        // Communication
        Item("Discord.Discord", "Discord", "Discord Inc.", "Discussions vocales, vidéo et texte entre amis ou communautés.", "Communication", "discord.com",
             "XPDC2RH70K22MN", "Discord.Discord.PTB", "Discord.Discord.Canary"),
        Item("Telegram.TelegramDesktop", "Telegram", "Telegram", "Messagerie rapide, synchronisée avec votre téléphone.", "Communication", "telegram.org"),
        Item("Zoom.Zoom", "Zoom", "Zoom", "Visioconférences et réunions en ligne.", "Communication", "zoom.us"),
        Item("TeamViewer.TeamViewer", "TeamViewer", "TeamViewer", "Prise de contrôle à distance pour dépanner un proche.", "Communication", "teamviewer.com"),
        Item("AnyDesk.AnyDesk", "AnyDesk", "AnyDesk", "Bureau à distance léger et rapide.", "Communication", "anydesk.com"),

        // Jeux
        Item("Valve.Steam", "Steam", "Valve", "La plus grande boutique de jeux PC.", "Jeux", "steampowered.com"),
        Item("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Epic Games", "Boutique Epic, Fortnite et jeux offerts chaque semaine.", "Jeux", "epicgames.com"),
        Item("Ubisoft.Connect", "Ubisoft Connect", "Ubisoft", "Lanceur des jeux Ubisoft.", "Jeux", "ubisoft.com"),
        Item("ElectronicArts.EADesktop", "EA app", "Electronic Arts", "Lanceur des jeux EA.", "Jeux", "ea.com"),

        // Multimédia
        Item("VideoLAN.VLC", "VLC", "VideoLAN", "Lit toutes les vidéos et musiques, sans codec à installer.", "Multimédia", "videolan.org"),
        Item("Spotify.Spotify", "Spotify", "Spotify", "Musique et podcasts en streaming.", "Multimédia", "spotify.com"),
        Item("CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack", "Codec Guide", "Codecs et lecteur MPC-HC pour les formats vidéo rares.", "Multimédia", "codecguide.com"),
        Item("HandBrake.HandBrake", "HandBrake", "HandBrake", "Convertit et compresse vos vidéos.", "Multimédia", "handbrake.fr"),

        // Création
        Item("OBSProject.OBSStudio", "OBS Studio", "OBS Project", "Enregistrement d'écran et streaming en direct.", "Création", "obsproject.com"),
        Item("Audacity.Audacity", "Audacity", "Audacity", "Enregistrement et montage audio.", "Création", "audacityteam.org"),
        Item("GIMP.GIMP.3", "GIMP", "GIMP", "Retouche photo libre, alternative à Photoshop.", "Création", "gimp.org"),
        Item("dotPDN.PaintDotNet", "Paint.NET", "dotPDN", "Retouche d'images simple et rapide.", "Création", "getpaint.net"),
        Item("ShareX.ShareX", "ShareX", "ShareX", "Captures d'écran, GIF et enregistrements en un raccourci.", "Création", "getsharex.com"),

        // Bureautique
        Item("TheDocumentFoundation.LibreOffice", "LibreOffice", "The Document Foundation", "Suite bureautique gratuite compatible Word et Excel.", "Bureautique", "libreoffice.org"),
        Item("Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader", "Adobe", "Le lecteur PDF de référence.", "Bureautique", "adobe.com"),
        Item("SumatraPDF.SumatraPDF", "SumatraPDF", "Krzysztof Kowalczyk", "Lecteur PDF ultra-léger.", "Bureautique", "sumatrapdfreader.org"),
        Item("Notepad++.Notepad++", "Notepad++", "Don Ho", "Éditeur de texte avancé.", "Bureautique", "notepad-plus-plus.org"),

        // Compression
        Item("RARLab.WinRAR", "WinRAR", "RARLAB", "Crée et extrait les archives RAR et ZIP.", "Compression", "rarlab.com"),
        Item("7zip.7zip", "7-Zip", "Igor Pavlov", "Compression gratuite : 7z, ZIP, RAR et bien d'autres.", "Compression", "7-zip.org"),

        // Développement
        Item("Microsoft.VisualStudioCode", "Visual Studio Code", "Microsoft", "Éditeur de code léger et extensible.", "Développement", "code.visualstudio.com"),
        Item("Git.Git", "Git", "Git", "Gestion de versions du code source.", "Développement", "git-scm.com"),
        Item("Python.Python.3.13", "Python 3.13", "Python Software Foundation", "Langage de programmation polyvalent.", "Développement", "python.org"),
        Item("Oracle.JavaRuntimeEnvironment", "Java", "Oracle", "Environnement Java, requis par certains jeux et logiciels.", "Développement", "java.com"),

        // Utilitaires
        Item("Microsoft.PowerToys", "PowerToys", "Microsoft", "Outils Microsoft pour aller plus vite sous Windows.", "Utilitaires", "microsoft.com"),
        Item("voidtools.Everything", "Everything", "voidtools", "Trouve n'importe quel fichier instantanément.", "Utilitaires", "voidtools.com"),
        Item("qBittorrent.qBittorrent", "qBittorrent", "qBittorrent", "Client BitTorrent libre et sans publicité.", "Utilitaires", "qbittorrent.org"),
        Item("WinDirStat.WinDirStat", "WinDirStat", "WinDirStat", "Montre ce qui occupe la place sur le disque.", "Utilitaires", "windirstat.net"),
        Item("CPUID.CPU-Z", "CPU-Z", "CPUID", "Détails du processeur, de la mémoire et de la carte mère.", "Utilitaires", "cpuid.com"),
        Item("REALiX.HWiNFO", "HWiNFO", "REALiX", "Températures et capteurs de tout le matériel.", "Utilitaires", "hwinfo.com"),
        Item("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Crystal Dew World", "Santé et température des disques.", "Utilitaires", "crystalmark.info"),

        // Sécurité
        Item("Malwarebytes.Malwarebytes", "Malwarebytes", "Malwarebytes", "Détecte et supprime les logiciels malveillants.", "Sécurité", "malwarebytes.com"),
        Item("Bitwarden.Bitwarden", "Bitwarden", "Bitwarden", "Gestionnaire de mots de passe libre.", "Sécurité", "bitwarden.com"),
    ];

    private static SoftwareItem Item(string id, string name, string publisher, string description, string category, string domain, params string[] altIds) =>
        new() { Id = id, Name = name, Publisher = publisher, Description = description, Category = category, Domain = domain, AltIds = altIds };
}
