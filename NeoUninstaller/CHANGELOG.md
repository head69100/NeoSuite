# NeoUninstaller - Nouveautés

## 0.2.6 - 2026-10-18

### Corrections
- La fenêtre s'ouvre au premier plan au lieu d'apparaître derrière les autres quand le démarrage administrateur est activé.

## 0.2.5 - 2026-10-18

### Changements
- Le badge « jamais utilisé » disparaît de la liste des programmes. Il repose sur le dossier Prefetch de Windows, dont les informations sont trop approximatives pour être utiles.

## 0.2.4 - 2026-10-18

### Corrections
- Le badge « jamais utilisé » ne s'affiche plus sur les éléments dont le PC a besoin sans être « lancé » : bibliothèques Visual C++, .NET SDK, Anti-Cheat, composants AMD, correctifs Windows (KB…). Il ne s'affiche plus non plus sur un programme dont on ne trouve que l'installateur ou le programme de mise à jour.

## 0.2.3 - 2026-10-18

### Corrections
- Le badge « jamais utilisé » ne s'affiche plus sur les entrées de pilotes sans exécutable identifiable (AMD Chipset Software, AMD_Chipset_Drivers…) : sans preuve mesurable, aucun jugement n'est affiché.

## 0.2.2 - 2026-10-18

### Corrections
- Pilotes orphelins : la détection prend maintenant en compte tous les appareils du PC, même déconnectés ou masqués. Certains pilotes encore utilisés (AMD, Realtek, DTS, SteelSeries…) étaient proposés à tort : ils ne le sont plus.
- Pilotes orphelins : boutons « Tout cocher » et « Tout décocher ».
- Le badge « jamais utilisé » ne s'affiche plus sur les pilotes et composants système : un pilote n'est pas un programme que l'on lance à la souris, l'information n'avait aucun sens pour eux.

## 0.2.1 - 2026-10-18

### Corrections
- Correction d'un plantage au démarrage (fenêtre qui s'ouvrait puis se refermait immédiatement) dû à une couleur de badge inexistante dans le thème Windows.

## 0.2.0 - 2026-10-18

### Nouveautés
- Moniteur d'installation : photographiez le PC avant et après l'installation d'un logiciel ; sa désinstallation retrouvera alors tout ce que l'installation avait créé.
- Programmes liés : à la désinstallation de Chrome, d'Office ou d'un composant NVIDIA/Adobe, les programmes qui vont avec sont proposés pour éviter les orphelins.
- Programmes fantômes (dossier ou désinstalleur disparu) repérés dans la liste, avec un bouton pour les retirer proprement.
- Désinstallation silencieuse automatique quand le désinstalleur la propose (Inno Setup, NSIS, Windows Installer), y compris en lot.
- Tri par dernière utilisation et liste des programmes pas utilisés depuis 6 mois (droits administrateur requis).
- Alerte en direct quand un programme s'ajoute au démarrage de Windows (clés Run, dossiers Démarrage, tâches planifiées).
- Nouvelle page Pilotes orphelins : suppression des anciens pilotes qu'aucun appareil n'utilise plus.
- Tâches planifiées détectées dans l'analyse des restes, au même titre que les services.
- Historique : « Tout annuler » remet aussi les fichiers à leur place depuis la Corbeille (7 jours maximum), en plus des clés de registre.

## 0.1.1 - 2026-10-18

### Corrections
- Désinstallation en lot : tous les programmes sont d'abord désinstallés, puis une seule analyse des restes (une seule liste) et un seul nettoyage à la fin, au lieu d'une analyse après chaque programme.

## 0.1.0 - 2026-10-17

Première version.

### Nouveautés
- Désinstallation complète des programmes, avec recherche et suppression des restes.
- Applis Windows, programmes au démarrage, nettoyage des fichiers inutiles, historique et sauvegardes.
- Démarrage en administrateur sans confirmation, réglable depuis NeoSuite.
