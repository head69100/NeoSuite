# NeoSuite — site vitrine

Site vitrine statique de **NeoSuite 0.4.3**, suite de neuf applications Windows.
Une seule page HTML, sans build, sans framework et sans aucune dépendance externe :
tout (polices, images, scripts) est servi localement et le site fonctionne hors ligne.

> **En ligne :** <https://head69100.github.io/NeoSuite/>, publié automatiquement par
> GitHub Pages (`.github/workflows/pages.yml`) à chaque modification de ce dossier.
> Les boutons « Télécharger » téléchargent directement `NeoSuite.exe` (un seul fichier)
> depuis la dernière release GitHub. La version et la taille affichées sont mises à
> jour par `tools\publish-release.ps1`.

---

## Aperçu rapide

- **9 applications** présentées : NeoSuite, NeoDriver, NeoBoost, NeoUninstaller,
  NeoSoft, NeoRar, NeoMedia, NeoSound, NeoTorrent.
- **Direction artistique** : dark néon cyan/bleu, typographie Space Grotesk + Inter.
- **Animations** : hero WebGL (aurora), préchargeur, curseur personnalisé, boutons
  magnétiques, maquette 3D du panneau, section « suite » épinglée en défilement
  horizontal, manifeste illuminé au scroll, compteurs animés, console NeoBoost
  interactive, terminal d’installation, palette de commandes.
- **Accessibilité** : navigation clavier, focus visibles, `prefers-reduced-motion`,
  repli complet sans JavaScript.

## Lancer le site

### En double-cliquant

Ouvrez simplement `index.html` : le site fonctionne en `file://` (aucun `fetch`,
aucun module ES). Le seul lien qui dépend du contexte est celui vers
`../NeoSuite/CHANGELOG.md`, valable depuis l’arborescence du dépôt.

### Avec un serveur local (recommandé)

```powershell
cd NeoSuite\NeoSite
python -m http.server 8123
```

Puis ouvrez <http://localhost:8123/>.

Pour servir aussi le changelog depuis la racine du dépôt :

```powershell
cd NeoSuite
python -m http.server 8123
```

Puis ouvrez <http://localhost:8123/NeoSite/>.

## Structure

```
NeoSite/
├─ index.html              # toute la page : sections, sprite SVG, palette, dialogue
├─ README.md
└─ assets/
   ├─ css/
   │  ├─ base.css          # tokens, polices, reset, boutons, curseur, boot, toasts
   │  ├─ layout.css        # nav, hero, panneau, manifeste, suite épinglée, sections, footer
   │  └─ components.css    # cartes d’apps, console, palette, dialogue, terminal, FAQ
   ├─ js/
   │  ├─ core.js           # boot, curseur, reveals, compteurs, nav, palette, dialogue, toasts, Konami
   │  ├─ shader.js         # aurora WebGL du hero (repli CSS si indisponible)
   │  ├─ scroll.js         # section épinglée, mots illuminés, scrollToApp
   │  └─ demo.js           # jauges, graphe, processus, séquence de boost réversible
   ├─ fonts/               # WOFF2 locaux (Inter, Space Grotesk)
   └─ img/                 # logos des 9 apps, favicon, og.jpg (1200×630)
```

## Interactions à essayer

| Action | Résultat |
| --- | --- |
| `Ctrl` + `K` (ou `⌘` + `K`) | Ouvre la palette de commandes : filtrer les apps, naviguer, ouvrir le dialogue |
| Saisir `neo` dans la palette | Filtre les applications par nom |
| `Entrée` dans la palette | Ouvre l’app sélectionnée |
| `↑` `↓` `↑` `↓` `←` `→` `←` `→` `B` `A` | Active le mode **Overdrive** (thème magenta/violet) |
| Boutons « Télécharger » | Télécharge `NeoSuite.exe` depuis la dernière release GitHub |
| Bouton « Nouveautés » | Ouvre le dialogue des notes de version |
| Console NeoBoost | Interrupteurs, suspension de processus, boost réversible |
| `?noboot` dans l’URL | Ignore le préchargeur (utile pour les captures) |

## Personnalisation

Les couleurs, rayons, ombres et polices sont centralisés dans les variables CSS
en tête de `assets/css/base.css` (`:root`). Les accents par application sont
définis directement sur chaque carte via `--accent` / `--accent-2` dans
`index.html`. Le mode Overdrive surcharge ces tokens dans `components.css`.

Les notes de version et les libellés de téléchargement se trouvent dans
`index.html` (sections « Nouveautés » et cartes d’applications).

## Limites

- **Téléchargements** : les liens `data-download` téléchargent `NeoSuite.exe` de la
  dernière release GitHub ; l’installateur n’est pas servi par le site lui-même.
- **Pas de back-end** : formulaire, compte, newsletter et statistiques sont absents.
- **Aucune requête réseau** : pas de CDN, pas de police distante, pas d’analytics.
- **Changelog** : les liens pointent vers `NeoSuite/CHANGELOG.md` sur GitHub.
- **Versions** : la version affichée (`softwareVersion`, fiche « Taille »…) est
  remplacée par `tools\publish-release.ps1` à chaque publication ; les entrées de
  la fenêtre « Nouveautés » s’ajoutent à la main.
- **WebGL** : l’aurora du hero se rabat sur un dégradé CSS si le contexte graphique
  est indisponible ; le reste du site reste identique.

## Vérifications effectuées

Testé sous Chrome (headless et fenêtré) en largeurs 1424 px et 390 px :
aucune erreur JavaScript, aucun débordement horizontal, préchargeur, navigation,
palette, dialogue, toasts, console, menu mobile et mode Overdrive fonctionnels.
`prefers-reduced-motion` et l’absence de JavaScript ont également été vérifiés.

---

© NeoSuite — maquette de démonstration.
