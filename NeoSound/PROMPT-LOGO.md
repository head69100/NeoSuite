# NeoSound — prompt de logo (Midjourney / DALL·E)

Ce document sert à générer `NeoSound\logo.jpg`, la tuile qui devient l'icône de l'application, la
tuile de NeoSuite et la carte de NeoSoft.

## Contraintes imposées par `tools\make-icon.ps1`

Le script d'icônes ne recadre pas au hasard : il cherche les bords d'une **tuile carrée** sur une
**ligne et une colonne centrales**, en comparant au pixel du coin (2, 2). Il faut donc :

- une **image carrée** (1024 × 1024 conseillé), une seule tuile bien centrée ;
- un **fond parfaitement uni** autour de la tuile — pas de dégradé, pas de texture, pas de
  vignettage, pas d'ombre portée qui déborde, pas de « halo » qui touche les bords ;
- une **marge régulière** de 6 à 10 % sur les quatre côtés : le fond est retiré, puis les coins
  de la tuile sont arrondis (rayon 20 %) ;
- un **sujet lisible à 16 px** : peu de détails, formes pleines, contraste franc ;
- **aucun texte**, aucun filigrane, aucune signature, aucune bordure décorative.

Couleurs de la suite Neo, à réutiliser pour rester cohérent avec les autres tuiles
(`NeoRar`, `NeoMedia`, `NeoBoost`, `NeoDriver`) :

| Rôle | Valeur |
|---|---|
| Fond de la tuile | `#101010` (anthracite quasi noir, comme la barre de titre des applis) |
| Accent principal | `#1E9BFF` |
| Accent clair (reflets, néon) | `#47B0FF`, `#6CC6FF`, `#9EDCFF` |
| Accent profond (ombres, socle) | `#0A7BD6`, `#0062B0`, `#004A87` |

Thème : **sombre, néon / cyberpunk maîtrisé** — le néon reste *dans* la tuile (aucune lueur ni halo
qui déborde sur le fond, sinon le recadrage échoue), formes plates avec un léger relief, aucun style
« cartoon », aucun réalisme photographique, pas de « lens flare ».

## Idée directrice

Le sujet : **le routage d'un son vers deux casques**. Un **casque filaire et un casque sans fil
côte à côte**, reliés par **une seule onde sonore** qui se dédouble entre eux (l'onde entre dans
l'image, se sépare, ressort vers chaque casque) : l'image dit exactement ce que fait l'application —
le même son envoyé aux deux. Une lecture alternative et plus simple : **deux arcs de casque qui
s'entrecroisent**, et au centre une onde sonore verticale dont les deux moitiés partent vers la
gauche et vers la droite.

## Prompt principal — Midjourney

```
app icon, centered square tile, single simple emblem on a flat solid #101010 background,
two minimalist headphones side by side slightly overlapping, one wired one wireless,
audio routing emblem, one shared sound wave splitting into two symmetrical streams,
one stream toward each headphone, thin neon electric blue lines (#1E9BFF) with soft cyan
highlights (#6CC6FF) and deep blue shading (#0062B0), subtle cyberpunk neon glow kept
strictly inside the tile, flat vector style, clean geometric shapes, high contrast,
symmetric composition, generous uniform margin around the tile,
software suite icon, dark modern UI, crisp edges, minimal detail, no text
--ar 1:1 --style raw --v 6.1 --q 2 --no text, letters, words, logo text, watermark,
signature, border, frame, background texture, gradient background, drop shadow, photo,
realism, 3d render, clutter, busy details, lens flare, halo, glow spilling outside the tile
```

## Prompt principal — DALL·E (ou autre générateur en langage naturel)

```
A modern neon app icon for an audio-routing application: a single centered square tile on a
completely flat solid #101010 dark charcoal background, with an even margin on all four sides.
The emblem: two minimalist headphones standing side by side and slightly overlapping — one wired,
one wireless — linked by one sound wave that starts in the middle and splits into two symmetrical
streams, one toward each headphone: the same sound being routed to both at once.
Style: flat vector with a controlled neon / cyberpunk glow that stays entirely inside the tile,
geometric, dark UI theme, crisp edges, high contrast.
Colours: electric blue #1E9BFF for the main lines, lighter cyan #6CC6FF for highlights,
deep blue #0062B0 for the shadows, on the #101010 background.
Absolutely no text, no letters, no numbers, no watermark, no signature, no frame or border.
The background must stay uniform: no gradient, no texture, no vignette, no glow or shadow
spilling outside the tile.
The icon must stay readable when reduced to a very small size, so keep the shapes simple and bold.
```

## Variantes (même esprit, à essayer si la première ne convient pas)

1. **Onde unique, deux départs** — une seule onde sonore verticale au centre ; sa moitié gauche se
   transforme en un arc de casque filaire, sa moitié droite en un casque à arceau plus fin
   (Bluetooth). Lecture très directe de « un son, deux casques ».
2. **Deux arcs entrecroisés** — deux cercles/arcs entrelacés (comme deux maillons) avec une petite
   onde sonore au point de croisement. Très lisible en 16 px, très « icône ».
3. **Casque unique qui se dédouble** — un casque plein à gauche, son reflet en néon à droite,
   reliés par une onde lumineuse. Plus élégant, un peu moins explicite.
4. **Spectre sonore en V** — un égaliseur de cinq barres dont les deux barres extérieures
   deviennent les arceaux de deux casques. Bon compromis entre « audio » et « duplication ».
5. **Deux casques sans fil** — deux casques identiques (donc deux sorties Bluetooth, cas rare mais
   que NeoSound sait alimenter avec deux adaptateurs) séparés par une onde partagée. À préférer si
   l'icône doit être symétrique à la perfection : deux formes identiques, une seule onde.

## Réglages recommandés

| Générateur | Conseils |
|---|---|
| Midjourney | `--ar 1:1 --style raw --v 6.1 --q 2` ; ajouter `--no text, watermark, gradient background` ; si la tuile n'est pas assez centrée, relancer avec `--seed` identique et `--sameseed`. |
| DALL·E / ChatGPT | Demander explicitement « carré, fond uni, aucune texte » ; relancer 2 ou 3 fois et garder la proposition la plus symétrique. |
| Autres | Chercher un rendu « vector icon », « flat design », « dark theme », « app tile ». |

## Vérifications avant de générer les icônes

1. L'image est **carrée** et le fond autour de la tuile est **d'une seule couleur**.
2. Aucun texte, filigrane ou signature n'apparaît (même minuscule).
3. La tuile est **centrée** et ne touche pas les bords.
4. L'image reste lisible en la réduisant à 32 px.
5. Le bleu dominant correspond bien à l'accent de la suite (`#1E9BFF`).

## Générer les icônes

```powershell
# 1. Enregistrer l'image choisie dans NeoSound\logo.jpg
# 2. Générer les icônes de l'appli, la tuile NeoSuite et la carte NeoSoft
powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -App NeoSound -Logo NeoSound\logo.jpg
```

Le script affiche la tuile détectée (`Tuile détectée : ...`) : si les valeurs semblent étranges,
c'est le signe que le fond n'est pas assez uni ou que la tuile n'est pas centrée — reprendre une
image et relancer. Ne rien laisser traîner à la racine du dépôt : le logo vit dans
`NeoSound\logo.jpg`.
