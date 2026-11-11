# NeoDriver — Nouveautés

## 0.2.2 — 2026-10-18

### Corrections
- La fenêtre s'ouvre au premier plan au lieu d'apparaître derrière les autres quand le démarrage administrateur est activé.

## 0.2.1 — 2026-10-18

### Corrections
- Pilotes officiels : la liste des pilotes anciens est relue à chaque visite (un pilote mis à jour disparaît), avec un bouton « Réanalyser ».
- Un pilote que son fabricant ne met plus à jour peut être écarté avec « Ignorer » ; il revient si sa version change.

## 0.2.0 — 2026-10-18

### Nouveautés
- Installer un pilote fonctionne enfin : depuis un fichier .inf ou depuis un dossier entier (par exemple une sauvegarde), avec point de restauration avant, résultat clair et détails techniques de Windows.
- « Détecter » liste les périphériques que Windows signale en erreur, explique le problème en français, cherche un pilote sur Windows Update, et reconnaît le fabricant d'après l'identifiant matériel.
- Nouvelle page « Pilotes officiels » : BIOS de la carte mère (version, date, ancienneté, bouton vers la page de support du fabricant), pilote de chipset AMD ou Intel, version du pilote graphique (NVIDIA au numéro publié, ex. 560.94) avec lien vers le site du fabricant, et liste des pilotes de plus de 3 ans.
- Nouvelle page « Outils » : sauvegarde de tous les pilotes dans un dossier, nettoyage des anciennes versions de pilotes (avec l'espace libéré), et rapport du PC (matériel, BIOS, pilotes) à enregistrer ou à copier.
- Tableau de bord : carte « Santé des périphériques ».
- Mises à jour : « Ne plus proposer » pour écarter un pilote qui pose problème, et bouton « Masqués » pour les réafficher.
- Historique : « Revenir à l'ancien pilote » après une installation.
- Recherche automatique chaque semaine : NeoSuite affiche « 3 mises à jour » sur la tuile de NeoDriver, même fermé.
- Nouveau logo.

### Corrections
- Les boutons « Installer », « Choisir un dossier » et « Détecter » de la page Installer un pilote ne faisaient rien.

## 0.1.0 — 2026-10-17

Première version.

### Nouveautés
- Recherche des mises à jour de pilotes via Windows Update, et installation avec point de restauration.
- Inventaire du matériel et historique des installations.
- Démarrage en administrateur sans confirmation, réglable depuis NeoSuite.
- Intégré à NeoSuite : plus d'icône séparée près de l'horloge, et fermer la fenêtre quitte NeoDriver sans question.
