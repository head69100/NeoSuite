# NeoSound — guide d'intégration

NeoSound duplique le son du PC vers deux casques en même temps (filaire + Bluetooth, ou deux casques
filaire/USB), sans matériel supplémentaire et **sans aucun pilote à installer**.

- Projet : `NeoSound\src\NeoSound\NeoSound.csproj`
- Application installée : `C:\Program Files\Neo\NeoSound\NeoSound.exe`
- Version : voir `<Version>` du `.csproj` et `NeoSound\CHANGELOG.md`

## Ce que fait le module

NeoSound capture en « loopback » ce qui est joué sur un périphérique de sortie (WASAPI), puis
renvoie le même son vers un ou deux casques choisis. La capture devient la source ; les casques
sélectionnés reçoivent chacun une copie via leur propre tampon corrigé des écarts d'horloge
(`DriftBuffer`), ce qui évite la dérive entre deux sorties physiques.

Quatre points de conception à connaître avant de toucher au code :

- **La source n'est jamais aussi une sortie.** Rejouer dans le périphérique qu'on capture créerait
  une boucle sonore. Cette règle est vérifiée dans `AudioDuplicator.Start` et dans l'interface.
- **Mode partagé WASAPI**, qui est le plus bas retard raisonnablement stable. La latence visée
  (10 à 150 ms, 30 par défaut) est réglable dans la fenêtre et enregistrée. C'est un **minimum** :
  un pilote réclame son son par blocs entiers (22 ms sur les casques de l'essai), et le tampon doit
  couvrir plusieurs blocs — sinon la sortie reçoit du silence à la fin de chaque bloc et le son
  grésille, quelle que soit la latence demandée.
- **Chaque casque vit sa vie.** Un casque qui lâche en cours de route (débranché, Bluetooth
  déconnecté, pilote réinitialisé) est retiré de la duplication, signalé dans la fenêtre, et les
  autres continuent de jouer. Quand plus rien ne reste à alimenter, NeoSound s'arrête de lui-même.
- **Aucun pilote n'est nécessaire, et aucun ne servirait.** Windows expose chaque casque comme un
  périphérique ordinaire : NeoSound les ouvre tous les deux directement. Un câble virtuel
  (VB-CABLE, Voicemeeter) crée une sortie *supplémentaire* ; il ne donne pas accès à un second
  casque Bluetooth pour autant (voir « Deux casques Bluetooth en même temps »).

## Prérequis

| Élément | Version / remarque |
|---|---|
| Windows | 10 1809 (build 17763) ou plus récent, **64 bits** |
| .NET SDK | 10.0 ou supérieur (le paquet publié est autonome : rien à installer chez l'utilisateur) |
| Windows App SDK | 2.5.1 (référence NuGet, inclus dans le paquet) |
| Moteur audio | `NAudio.Wasapi` 2.2.1 (licence MIT, incluse dans le paquet) |
| Plateforme | `x64` uniquement (`<Platforms>x64</Platforms>`) |

## Compiler

```powershell
dotnet build NeoSound\src\NeoSound\NeoSound.csproj -c Debug -p:Platform=x64
```

L'exécutable se trouve alors dans
`NeoSound\src\NeoSound\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\NeoSound.exe`.

Depuis la racine du dépôt, la solution entière se compile avec
`dotnet build NeoSuite.sln -c Debug -p:Platform=x64` (le projet NeoSound y est référencé).

## Lancer

Double-cliquez sur l'exécutable ci-dessus, ou lancez-le depuis NeoSuite une fois installé.
Rien à faire de particulier : NeoSound s'ouvre, propose la source et les casques détectés.

### Modes sans interface (scripts, tests, dépannage)

| Argument | Effet |
|---|---|
| `--list` | Affiche les périphériques de sortie actifs sur la console, puis quitte |
| `--dump <fichier>` | Écrit la même liste en JSON (`Id`, `Name`, `Description`, `IsDefault`, `SampleRate`, `Channels`, `IsPhoneMode`, `Transport`, `BetterSiblingName`), puis quitte |
| `--probe <secondes> [rapport.json]` | Duplique selon les réglages enregistrés pendant le temps demandé et rapporte ce que chaque casque a manqué (silence), jeté (écart d'horloge) et perdu en cours de route. Du son doit être joué sur la source pendant la mesure. |
| `--relaunched` | Usage interne : une seconde instance réaffiche la fenêtre existante (instance unique) |

La liste indique aussi par quel matériel Windows atteint chaque sortie (`Bluetooth`, `USB`,
`intégré`, `virtuel`) : c'est le nom de l'énumérateur du périphérique (`BTHENUM`, `USB`, `HDAUDIO`…)
lu dans ses propriétés. Aucune autre source d'information ne permet de distinguer un casque
Bluetooth d'un casque filaire : leurs noms se ressemblent.

Exemple :

```powershell
NeoSound.exe --list
NeoSound.exe --dump "$env:TEMP\neosound-devices.json"
NeoSound.exe --probe 20 "$env:TEMP\neosound-probe.json"
```

Ces modes n'ouvrent aucune fenêtre : utiles pour vérifier la détection des périphériques depuis un
script, ou pour mesurer la qualité d'une duplication sans se fier à son oreille.

## Utiliser

1. **Source** : le périphérique dont on veut dupliquer le son (par défaut, la sortie Windows
   active). Le casque source continue de jouer directement.
2. **Casque 1** et, si « Dupliquer aussi vers un second casque » est coché, **Casque 2**.
   Aucun des deux ne peut être le périphérique source.
3. **Latence** : 10 à 150 ms. 10–15 ms (réactif) pour du jeu, ~30 ms (défaut) pour un usage
   courant, 60 ms et plus si le Bluetooth décroche. La valeur demandée est un minimum : si le pilote
   d'un casque réclame de plus gros blocs, NeoSound s'y adapte et l'indique après le démarrage.
4. **Démarrer la duplication**. L'état s'affiche en haut de la fenêtre ; « Arrêter la duplication »
   rend la main.

Les sorties Bluetooth portent la mention « Bluetooth » dans les listes. Les profils « mains libres »
d'un casque Bluetooth (16 kHz mono, qualité d'appel) sont signalés et refusés quand le même casque
propose aussi son profil stéréo : c'est ce profil-là qu'il faut choisir.

« Actualiser les périphériques » relit la liste quand on branche un casque ou qu'on connecte un
appareil Bluetooth pendant que la fenêtre est ouverte. **Un casque qui a lâché en cours de route doit
être rebranché puis la duplication relancée** : NeoSound ne le récupère pas tout seul.

## Deux casques Bluetooth en même temps

Question légitime, réponse franche : **c'est Windows qui limite, pas NeoSound.**

- **Une seule liaison stéréo (A2DP) à la fois.** La pile Bluetooth de Windows ouvre et ferme les
  liaisons audio des appareils appairés selon ce qui joue ; deux casques Bluetooth ne sont donc pas
  exposés en même temps comme deux sorties actives. NeoSound ne peut dupliquer que vers des sorties
  que Windows lui montre : si le second casque n'apparaît pas, il n'y a rien à ouvrir. C'est
  exactement ce que dit la fenêtre quand deux sorties Bluetooth sont demandées et qu'une seule
  s'ouvre.
- **WASAPI et AudioGraph ne contournent pas cette limite**, aucune API audio ne le peut : la
  contrainte est dans la pile Bluetooth et dans le nombre de flux simultanés que l'adaptateur sait
  tenir.
- **Deux solutions réelles** :
  1. **Un second adaptateur Bluetooth** (clé USB). Windows dispose alors de deux radios, chacune
     gérant sa liaison ; les deux casques apparaissent comme deux sorties et NeoSound les duplique
     normalement. C'est la solution qui marche partout.
  2. **Le « partage audio » de Windows 11**, bâti sur le Bluetooth LE Audio : Windows diffuse alors
     lui-même vers deux accessoires compatibles LE Audio. C'est une fonction de Windows, pas de
     NeoSound, et elle est réservée aux PC compatibles (au départ les Copilot+ à processeur
     Snapdragon X) et aux accessoires LE Audio, sur les canaux Insider. Rien à coder côté NeoSound.
- **Un pilote audio virtuel n'y changerait rien.** VB-CABLE ou Voicemeeter créent une sortie
  *supplémentaire* (un câble) pour relier des applications entre elles, ou un mélangeur vers
  plusieurs sorties *que Windows expose déjà*. Ils ne créent pas de liaison Bluetooth. C'est pourquoi
  NeoSound n'embarque plus la moindre installation de pilote : elle ne répondait à aucun besoin réel.

## Réglages

Enregistrés dans `%LocalAppData%\NeoSound\settings.json` :

| Clé | Rôle |
|---|---|
| `SourceDeviceId` | Périphérique capturé |
| `Target1DeviceId`, `Target2DeviceId` | Casques de destination |
| `LatencyMs` | Latence visée (10 à 150) |
| `DuplicateToSecond` | Active le second casque |

Le fichier est relu au démarrage ; la duplication ne redémarre pas toute seule, elle se relance
d'un clic.

## Intégration à la suite Neo

NeoSound est intégré des deux côtés, contrairement à NeoRar et NeoMedia qui n'apparaissent que
dans NeoSoft :

- **NeoSuite** — `NeoSuite\Services\AppCatalog.cs` : tuile `NeoSound`, catégorie « Audio ».
  Le clic droit sur la tuile propose les actions déclarées par NeoSound :
  `open`, `start`, `stop`, `quit`.
- **NeoSoft** — `NeoSoft\src\NeoSoft\Services\SoftwareCatalog.cs` : carte `NeoSound`
  (`IsNeo = true`, catégorie « Suite Neo »).
- **Installateur** — `Setup\NeoSetup\Installer.cs` (`NeoApps`) et `tools\build-setup.ps1`
  (`$apps`) : NeoSound est installé et mis à jour comme les autres applis.
- **Communication** — `Shared\Neo.Shared\NeoIpc.cs`. NeoSound ouvre le serveur IPC dans
  `MainWindow` et publie son état via `NeoStatus` (« Duplication · 2 casque(s) »).

NeoSound n'a pas de fichier `neo-install-hook` / `neo-uninstall-hook` : rien à intégrer à
l'Explorateur, aucun service à arrêter.

## Droits administrateur

- **NeoSound tourne sans droits particuliers** (`app.manifest` :
  `requestedExecutionLevel level="asInvoker"`). La duplication passe par WASAPI, dans la session
  de l'utilisateur, et l'application n'installe rien : elle ne demande donc jamais d'élévation.
- L'installation de NeoSound depuis NeoSuite se fait en administrateur, comme les autres applis
  (`C:\Program Files\Neo\<App>`).

## Empaqueter et distribuer

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-packages.ps1 -Apps NeoSound
```

Produit `packages\NeoSound.zip` (publication Release autonome) et met à jour
`packages\catalog.json` (version, taille, SHA-256). NeoSuite et NeoSoft proposent alors la mise à
jour. Un seul appel par appli ; l'installateur complet se reconstruit avec
`tools\build-setup.ps1` (voir la racine `CLAUDE.md`).

## Logo et icônes

1. Déposez le logo dans `NeoSound\logo.jpg` (voir `NeoSound\PROMPT-LOGO.md` pour le générer).
2. ```powershell
   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -App NeoSound -Logo NeoSound\logo.jpg
   ```
3. Le script produit `Assets\AppIcon.png`, `Assets\AppIcon.ico` et les tuiles de NeoSuite et
   NeoSoft. Il attend une **tuile carrée posée sur un fond uni** : le fond est détecté puis retiré,
   et les coins sont arrondis automatiquement.

## Dépannage

| Symptôme | Cause probable / solution |
|---|---|
| « Aucun périphérique de sortie audio n'a été trouvé » | Sortie audio désactivée ou pilote audio absent : vérifier dans les Paramètres Windows, puis « Actualiser les périphériques ». |
| Un seul périphérique listé | Le second casque n'est pas branché ou pas connecté en Bluetooth. Le son ne peut pas être dupliqué vers un périphérique que Windows ne voit pas. |
| Deux casques Bluetooth, un seul joue | Limite de Windows : une seule liaison stéréo Bluetooth à la fois. Voir « Deux casques Bluetooth en même temps » (second adaptateur, ou partage audio LE Audio). Aucun pilote ne contourne cette limite. |
| Grésillements, coupures | Vérifier d'abord avec `--probe 20 <fichier>` : si « manque » grimpe, montez la latence ; si « jeté » grimpe, l'écart d'horloge entre les deux sorties est important (un casque Bluetooth peut en être la cause). Un éloignement du casque ou une liaison chargée dégradent aussi le son. |
| Un casque reste muet, la fenêtre indique « Casque non ouvert » | Deux sorties du **même** appareil ne peuvent pas être ouvertes en même temps (les canaux « jeu » et « chat » d'un casque gaming, par exemple) : choisissez-en une seule. Pour les autres causes, le message précise le refus du pilote. |
| La fenêtre signale qu'un casque « a lâché » | Le casque a disparu en cours de route : câble retiré, Bluetooth déconnecté, ou pilote réinitialisé. Les autres casques ont continué de jouer ; rebranchez le casque et relancez la duplication. Le détail reste lisible dans `--probe`. |
| Le casque Bluetooth se coupe après quelques minutes | Le pilote Bluetooth économise l'énergie ou la sortie a été modifiée par Windows : « Actualiser les périphériques », puis relancer la duplication. |
| Pas de son sur un casque | Vérifier qu'il n'est pas choisi comme source (interdit), et que son volume Windows n'est pas à zéro. |
| La duplication s'arrête seule | Plus aucun casque n'alimente la duplication : le message affiché vient de `AudioDuplicator.Faulted` et dit lequel a lâché et pourquoi. |
