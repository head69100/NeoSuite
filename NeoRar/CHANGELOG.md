# NeoRar — Nouveautés

## 0.2.1 — 2026-10-24

### Corrections
- Sélection au simple clic : cliquer sur une ligne la sélectionne de nouveau, avec la surbrillance visible, et « Supprimer » s'active dans une archive modifiable — y compris en cliquant sur le nom ou l'icône. Le lasso ne démarre plus qu'en glissant dans une zone vide de la liste.

## 0.2.0 — 2026-10-18

### Nouveautés
- **Volumes découpés** : à la création, choix d'une taille de tome (100 Mo, 700 Mo, 4 700 Mo pour un DVD, 25 000 Mo ou aucune). Le moteur produit `archive.7z.001`, `.002`… ; NeoRar les renomme, nettoie les tomes d'une ancienne archive remplacée, affiche « N tomes » dans la barre d'état quand une telle archive est ouverte et, au résultat, le nombre de tomes et la taille totale. La découpé n'existe qu'au format 7z (le moteur refuse ailleurs) : la case se désactive pour ZIP et TAR.
- **Vérification CRC affinée** : « Tester » contrôle toute l'archive comme avant, mais aussi uniquement la sélection (dossiers compris avec tout leur contenu) — chaque fichier est relu et son empreinte comparée, sans rien charger en mémoire. Le résultat nomme les fichiers endommagés s'il y en a, et chaque vérification rejoint l'historique.
- **Extraction en arrière-plan** : bouton « Continuer en arrière-plan » pendant toute opération — NeoRar se réduit, l'opération continue, et à la fin la fenêtre revient avec une notification « bulle » de Windows. Les extractions lancées depuis l'Explorateur (qui ferment la fenêtre d'elles-mêmes) et les ajouts à une archive notifient aussi leur fin.
- **Historique des opérations** : nouvel entrée « Historique » — créations, extractions, ajouts, suppressions, vérifications et sauvegardes avec heure, cible, détail et issue (les 200 dernières sont conservées dans `%LocalAppData%\NeoRar\history.json`), bouton « Effacer ».
- **Planificateur de sauvegardes** : nouvel entrée « Sauvegardes » — on décrit des dossiers, une archive de destination, un format et une fréquence (quotidienne ou hebdomadaire, à l'heure choisie). Chaque sauvegarde devient une vraie tâche Windows (planificateur de tâches, pour l'utilisateur connecté) qui relance NeoRar avec `--backup` : NeoRar crée l'archive — l'ancienne version est remplacée, tomes compris — puis notifie le résultat. La liste montre la planification et la dernière exécution ; chaque sauvegarde peut être lancée à la demande ou supprimée (suppression en deux clics pour éviter les regrets). Exécution sans mot de passe, puisque la tâche tourne sans interaction.

## 0.1.1 — 2026-10-18

### Corrections
- Progression de création honnête : plus de « 100 % » affiché pendant 10 secondes. Sur un gros fichier, le pourcentage du moteur comptait les octets lus (faits en moins d'une seconde) alors que la compression continuait ~10 s : au-delà de 99 %, la barre repasse en animation avec le temps écoulé jusqu'au retour réel du moteur, puis 100 % à la fin.
- Phases affichées pendant la création : « Analyse des fichiers… » avec compteur pendant l'inventaire, puis « Compression en cours… ».
- L'analyse d'un grand dossier est plus rapide : un seul passage récursif au lieu de deux énumérations complètes.

## 0.1.0 — 2026-10-18

Première version.

### Nouveautés
- Explorateur de fichiers comme WinRAR : on parcourt ses dossiers, on compresse la sélection, on ouvre une archive d'un double-clic.
- Lecture de RAR/RAR5, ZIP, 7z, TAR, GZ, ISO… ; création de 7z, ZIP et TAR avec mot de passe AES-256.
- Glisser des fichiers hors d'une archive vers l'Explorateur, ou dans une archive 7z/ZIP ouverte.
- Sélection au lasso, retour arrière (flèche, Retour arrière, Alt+←, bouton « précédent » de la souris).
- Clic droit dans l'Explorateur : « Extraire ici », « Extraire dans un dossier », « Ajouter à une archive », « Ouvrir avec NeoRar ».
- Messages clairs pour une archive protégée, abîmée ou incomplète.
