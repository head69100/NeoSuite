# NeoSound — Nouveautés

## 0.2.0 — 2026-10-18

Chaque casque vit sa vie, et ce que Windows ne sait pas faire est dit franchement.

### Corrections
- **Le pilote audio virtuel optionnel a été retiré.** Le bouton « Installer Voicemeeter Banana », le service qui l'installait et le script `tools\install-vbaudio.ps1` ont disparu : un câble virtuel crée une sortie *supplémentaire*, pas une liaison Bluetooth, et n'ajoutait donc aucune sortie utile. NeoSound n'installe rien, et n'en a pas besoin.
- **Un casque qui lâche en cours de route n'arrête plus la duplication.** Il est retiré de la duplication, la fenêtre dit lequel et pourquoi, et les autres casques continuent de jouer sans interruption. Quand plus rien ne reste à alimenter, NeoSound s'arrête de lui-même en disant lequel a lâché et pourquoi (câble retiré, Bluetooth déconnecté, pilote réinitialisé).
- Un casque Bluetooth est reconnu comme tel **même quand son nom ne le dit pas** : c'est le nom de l'énumérateur de son périphérique (`BTHENUM`) qui le révèle. Les sorties portent désormais leur transport (« Bluetooth », « USB », « intégré », « virtuel ») dans la fenêtre, dans `--list` et dans `--dump`.

### Nouveautés
- **Deux sorties Bluetooth demandées** : quand la seconde ne s'ouvre pas — le cas courant, Windows ne tenant qu'une seule liaison stéréo à la fois — la fenêtre le dit et donne les vraies solutions (deux adaptateurs Bluetooth, ou le partage audio LE Audio des PC compatibles), au lieu de laisser croire à une panne de NeoSound. Aucun pilote virtuel n'y changerait rien.
- `--probe` rapporte aussi les casques perdus en cours de mesure, et `--dump` donne le transport de chaque sortie.
- Nouvelle icône, adaptée du logo déposé à la racine du dépôt (`NeoSound\logo.jpg`).
- `NeoSound\PROMPT-LOGO.md` revu : prompt néon / cyberpunk maîtrisé, ondes sonores, routage du son vers deux casques.

## 0.1.1 — 2026-10-18

Correction du grésillement constaté à l'essai.

### Corrections
- **Plus de coupures ni de grésillements.** Chaque casque recevait son son par blocs entiers (22 ms sur les casques de l'essai), alors que le tampon était ramené à la latence demandée à chaque lecture : il manquait donc de quoi remplir le bloc suivant, et le moteur complétait avec du silence — un grésillement continu, surtout au réglage le plus réactif. Le tampon tient désormais compte de la taille réelle des blocs, et les écarts d'horloge entre deux sorties ne sont corrigés qu'au-delà d'une marge de 25 ms. Mesuré sur soixante secondes : plus aucune trame jetée, seul reste le court remplissage du tout début.
- **Casques à plus de deux canaux** (la sortie « jeu » d'un casque gaming, par exemple) : ils ne s'ouvraient pas du tout. Leur format est maintenant décrit comme Windows l'exige au-delà de la stéréo.
- **Un casque qui ne s'ouvre pas est signalé** : la fenêtre indique lequel et pourquoi, au lieu d'annoncer une duplication active en laissant ce casque muet.
- La fenêtre rappelle, après le démarrage, le retard réellement tenu et le plus petit bloc que le pilote accepte.

### Nouveautés
- Latence réglable jusqu'à 150 ms (au lieu de 60) : la valeur demandée est un minimum, certains pilotes en imposent davantage.
- Diagnostic en ligne de commande : `--probe <secondes> [rapport.json]` mesure ce que chaque casque manque ou jette, et `--dump <fichier>` liste les sorties (fréquence, canaux, profil « mains libres »).

## 0.1.0 — 2026-10-18

Première version.

### Nouveautés
- Duplique le son du PC vers un casque filaire et un casque Bluetooth en même temps : choisissez la source, un ou deux casques, puis démarrez.
- Latence réglable de 10 à 60 ms (10–15 ms pour le jeu, plus haut si le Bluetooth décroche) : chaque casque reçoit sa propre copie, corrigée des écarts d'horloge entre les deux sorties.
- « Actualiser les périphériques » relit la liste sans fermer la fenêtre : branchez un casque, connectez un appareil Bluetooth, puis actualisez.
- Vos choix (source, casques, latence) sont conservés d'une fois sur l'autre.
- Aucun pilote à installer. Le bouton « Installer Voicemeeter Banana » reste disponible pour qui veut un vrai mélangeur à plusieurs sorties, mais il est facultatif.
- Pilotable depuis NeoSuite : clic droit sur la tuile pour ouvrir NeoSound, démarrer ou arrêter la duplication, installer le pilote optionnel, ou quitter.
- La duplication s'arrête d'elle-même avec un message clair si un casque est débranché ou si son pilote tombe en erreur.
