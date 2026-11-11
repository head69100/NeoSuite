# NeoSuite

Suite d'applications Windows (WinUI 3, .NET 10, x64), interface en français.
NeoSuite est le panneau qui installe et lance les autres applis ; NeoSoft propose en plus NeoRar, NeoMedia, NeoSound et des logiciels tiers (winget).

| Appli | Projet | Rôle |
|---|---|---|
| NeoSuite | `NeoSuite/NeoSuite.csproj` | Panneau latéral (Ctrl+Alt+N), installe les applis, droits administrateur |
| NeoDriver | `NeoDriver/src/NeoDriver/` | Mises à jour et installation des pilotes |
| NeoUninstaller | `NeoUninstaller/src/NeoUninstaller/` | Désinstallation complète, démarrage, nettoyage |
| NeoBoost | `NeoBoost/src/NeoBoost/` | Mode Boost, programmes gourmands, réglages Windows |
| NeoSoft | `NeoSoft/src/NeoSoft/` | Catalogue de logiciels (winget + applis Neo) |
| NeoRar | `NeoRar/src/NeoRar/` | Archives (moteur 7-Zip) |
| NeoMedia | `NeoMedia/src/NeoMedia/` | Lecteur vidéo/audio (moteur VLC) |
| NeoSound | `NeoSound/src/NeoSound/` | Duplication du son vers deux casques (filaire + Bluetooth) |
| Installateur | `Setup/NeoSetup/` | `NeoSuite-Setup.exe` pour un nouvel utilisateur |
| Code commun | `Shared/Neo.Shared/` | IPC NeoSuite, instance unique, démarrage admin… |

## Versionnage : à faire à CHAQUE modification

Toute modification d'une appli, même petite, se termine par ces trois étapes, dans la même réponse :

1. **Nouvelle version** dans le `<Version>` du `.csproj` de chaque appli touchée (format `MAJEUR.MINEUR.CORRECTIF`) :
   - **correctif** (0.1.0 → 0.1.1) : correction de bug, texte, détail visuel ;
   - **mineur** (0.1.3 → 0.2.0) : nouvelle fonction, nouvel écran, nouveau réglage ;
   - **majeur** (0.x → 1.0.0) : refonte ou changement incompatible — seulement sur demande de l'utilisateur.

   Une seule montée de version par appli et par demande, même si plusieurs fichiers changent.
2. **Nouveautés** : une entrée en tête du `CHANGELOG.md` de l'appli (dans son dossier, ex. `NeoRar/CHANGELOG.md`), au format ci-dessous.
3. **Paquet** : `powershell -ExecutionPolicy Bypass -File tools\build-packages.ps1 -Apps <App>` (une appli par appel), pour que NeoSuite / NeoSoft proposent la mise à jour.

Qui monte de version :
- Changement dans `Shared/Neo.Shared` → **toutes les applis qui utilisent le code modifié**.
- Changement dans `NeoSuite/Services/InstallWorker.cs` (installation des applis) → NeoSuite.
- L'installateur (`Setup/NeoSetup`) suit la version de NeoSuite : on les monte ensemble, et on reconstruit `dist\NeoSuite-Setup.exe` avec `tools\build-setup.ps1 -SkipPackages` quand NeoSuite ou l'installateur change.
- Simple outil de développement (`tools/`) ou documentation : pas de version.

Installateur et mises à jour de NeoSuite lui-même :
- `tools\build-setup.ps1` produit **un seul fichier**, `dist\NeoSuite.exe` : le programme d'installation suivi de NeoSuite et d'`info.json` (version et nouveautés, tirées du **premier bloc du `NeoSuite/CHANGELOG.md`**, donc le garder à jour et au format ci-dessous). Il **ne contient pas les applis** : NeoSuite et NeoSoft les téléchargent depuis la dernière release GitHub (`<App>.zip` + `catalog.json`) quand l'utilisateur les installe. L'utilisateur ne veut qu'un seul fichier à télécharger : ne pas revenir à un `.pak` à côté, ni remettre les applis dans l'installateur (Windows analyse en entier un `.exe` inconnu à son premier lancement : 19 s pour 731 Mo).
- Quand NeoSuite ou l'installateur change : monter la version des deux (même numéro), puis `tools\build-setup.ps1 -SkipPackages` (sans `-SkipPackages` si une appli a changé).
- NeoSuite cherche lui-même une version plus récente (`NeoSuite/Services/UpdateService.cs`) : un installateur déjà sur le PC (Téléchargements, Bureau, Documents, `dist`), sinon la dernière **release GitHub** (`GitHubReleases.cs`, dépôt public `head69100/NeoSuite`), téléchargée puis lancée. Les tuiles des applis prennent aussi les paquets plus récents de la release (`AppCatalog.MergeOnlineCatalogAsync`).

## GitHub : dépôt, releases, site

- Dépôt public **https://github.com/head69100/NeoSuite** (branche `main`). Les produits de build (`dist/`, `packages/`) ne sont pas versionnés : ils vont dans les releases.
- **Publier** une version (après le versionnage ci-dessus et `tools\build-setup.ps1`) : `powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1`. Le tag est `v<version de NeoSuite>` ; la release contient `NeoSuite.exe`, chaque `<App>.zip`, `catalog.json` et un petit `NeoSuite.pak` de compatibilité (sans lui, NeoSuite 0.5.0 ne voit pas la mise à jour ; à ne pas retirer) (c'est ce que lisent l'installation des applis et les mises à jour automatiques ; toujours reconstruire les paquets des applis changées avant de publier). Une release déjà publiée ne se republie pas : monter la version de NeoSuite.
- Une appli seule a changé ? Monter sa version, reconstruire son paquet, puis monter aussi NeoSuite (correctif, entrée « NeoX passe en 0.y.z » dans son CHANGELOG) pour pouvoir publier une nouvelle release.
- **Site** : `NeoSite/` est publié par GitHub Pages (`.github/workflows/pages.yml`) à chaque push qui le modifie : https://head69100.github.io/NeoSuite/. `publish-release.ps1` y met à jour la version et la taille ; ajouter à la main l'entrée de la fenêtre « Nouveautés » (`index.html`, `#releaseDialog`).

Format du `CHANGELOG.md` (le plus récent en haut, rédigé pour l'utilisateur, pas pour le développeur) :

```markdown
## 0.2.0 — 2026-10-18

### Nouveautés
- Ce que l'utilisateur peut faire de nouveau, en une phrase.

### Corrections
- Le problème qu'il rencontrait, et qu'il ne rencontrera plus.
```

N'utiliser que les rubriques utiles (Nouveautés, Améliorations, Corrections). Date du jour au format AAAA-MM-JJ.

Dans la réponse finale à l'utilisateur, indiquer la ou les nouvelles versions (ex. « NeoRar 0.2.0 »).

## Conventions

- **Textes et commentaires en français**, tutoiement exclu dans l'interface (vouvoiement : « Glissez une vidéo… »).
- **Encodage** : fichiers en UTF-8. Windows PowerShell 5.1 relit en ANSI un fichier sans BOM : pour modifier un fichier par script, utiliser `[IO.File]::ReadAllText(path, [Text.Encoding]::UTF8)` / `WriteAllText(..., New-Object Text.UTF8Encoding $true)`, jamais `Get-Content`/`Set-Content` seuls (accents abîmés). Les `.ps1` de `tools/` doivent garder leur BOM.
- **Icônes** : logo dans `<App>/logo.jpg`, puis `tools\make-icon.ps1 -App <App> -Logo <App>\logo.jpg` (icônes de l'appli, tuile NeoSuite et carte NeoSoft). La racine du dépôt ne doit garder que `NeoSuite.sln`, `CLAUDE.md`, `README.md`, `.gitignore` et `.github/` : ranger tout logo déposé à la racine.
- **Installation des applis** : NeoSuite les installe dans `C:\Program Files\Neo\<App>` (mode « worker » en administrateur). Un fichier `neo-install-hook` / `neo-uninstall-hook` dans l'appli donne des arguments lancés après l'installation / avant la désinstallation (ex. intégration à l'Explorateur de NeoRar et NeoMedia).
- **Nouvelle appli** : entrée dans `NeoSuite/Services/AppCatalog.cs` (tuile NeoSuite) ou dans `NeoSoft/.../SoftwareCatalog.cs` avec `IsNeo = true` (catalogue NeoSoft), ajout à `$apps` dans `tools\build-setup.ps1`, à `NeoApps` dans `Setup/NeoSetup/Installer.cs`, et un `CHANGELOG.md`.
- **Tests** : ne jamais simuler la souris ou le clavier (`SetCursorPos`, `mouse_event`, `keybd_event`, `SendKeys`) : l'utilisateur travaille sur ce PC et les actions partent dans ses propres fenêtres. Tester sans interface (petit programme console qui appelle les services) ou se contenter de captures d'écran.
- **Compiler** : `dotnet build <projet> -c Debug -p:Platform=x64`. Si NeoSuite tourne, ses fichiers sont verrouillés : le quitter et le relancer (`NeoSuite\bin\x64\Debug\...\NeoSuite.exe`) quand l'utilisateur le demande.
