# Architecture du Taikeron Launcher

## Principe fondamental

Le launcher est le point d’entrée permanent de l’écosystème Taikeron sur Windows. Les applications gérées par le launcher sont considérées comme du code remplaçable.

L’objectif est d’éviter qu’une mise à jour ajoute du nouveau code au-dessus d’un ancien runtime. Pour TL, une mise à jour publique doit supprimer réellement l’ancien code applicatif avant de remettre un paquet neuf et vérifié.

Les données utilisateur appartiennent au **Data Vault**. Elles doivent rester indépendantes du code, de la version de TL et du disque choisi pour les applications.

## Le Launcher comme source de vérité

Le Launcher ne fait pas confiance au seul numéro de version déclaré par Taikeron Lab.

À chaque démarrage du Launcher :

1. lire le manifeste de release officiel ;
2. détecter la version TL réellement installée ;
3. récupérer le manifeste d’intégrité correspondant à cette version ;
4. vérifier localement la taille et le SHA-256 de chaque fichier de code attendu ;
5. détecter les résidus connus d’anciennes activations ;
6. afficher séparément l’état d’intégrité et l’état de mise à jour ;
7. bloquer le lancement si le code est connu comme altéré.

Une version ancienne peut donc être **intacte mais pas à jour**. Ce sont deux états différents.

Le Data Vault, les cartes utilisateur et les sauvegardes ne font jamais partie de cette comparaison de code. Aucun contenu personnel n’est envoyé au site : seuls les petits manifestes publics sont téléchargés et les hash sont calculés localement.

Les manifestes publics sont publiés sous deux formes :

```text
releases/tl/integrity-stable.json
releases/tl/integrity/<version>/windows-x64.json
```

Chaque entrée contient au minimum le chemin relatif, la taille et le SHA-256 du fichier construit. WDS génère ces valeurs directement depuis `dist/win-unpacked` de la release réellement publiée.

Les anciennes releases publiées avant ce mécanisme peuvent être affichées comme `non certifiées` tant qu’un manifeste historique fiable n’existe pas. Le Launcher ne doit jamais inventer une certification.

## Distribution publique

Pour Windows, le site public Taikeron doit distribuer **Taikeron Launcher**, pas Taikeron Lab directement.

Flux public cible :

```text
Site Taikeron
   ↓
Télécharger Taikeron Launcher
   ↓
Launcher
   ├─ Installer / mettre à jour Taikeron Lab
   ├─ Installer / mettre à jour Taikeron Map Builder
   └─ Gérer les futures applications Windows Taikeron
```

Règles :

- le bouton principal Windows du site pointe vers la dernière release stable du Launcher ;
- Taikeron Lab reste publié sous forme de paquet/release afin que le Launcher puisse le télécharger, mais le site ne doit plus proposer son EXE comme chemin public principal ;
- Taikeron Map Builder pourra lui aussi être installé depuis le Launcher ;
- Taikeron App Android reste distribuée séparément, car elle n’est pas installée par le Launcher Windows ;
- le dépôt source `Taikeron-Launcher` peut rester privé ; le binaire stable du Launcher doit être publié sur un canal public accessible sans compte GitHub ;
- le site ne bascule vers le Launcher qu’après publication d’une première release stable réellement téléchargeable, afin de ne jamais remplacer un lien TL fonctionnel par un lien mort.

Le canal public cible du Launcher doit exposer au minimum : version, URL, nom de fichier, taille, SHA-256 et date de publication, sur le même principe que le manifeste stable TL actuel.

## Emplacements configurables

Le launcher conserve une configuration centrale dans :

```text
%LOCALAPPDATA%\Taikeron\Launcher\launcher-settings.json
```

Les racines suivantes sont configurables depuis le launcher :

```text
Applications       -> ex. C:\Taikeron\Apps
Data Vault         -> ex. F:\Taikeron\DataVault
Cartes             -> ex. F:\Taikeron\Maps
Téléchargements    -> ex. C:\Taikeron\Downloads
Sauvegardes Vault  -> ex. G:\TaikeronBackup
```

Changer un chemin ne doit jamais supprimer ou déplacer silencieusement des données existantes. Les migrations entre disques doivent être explicites, vérifiées puis seulement nettoyer l’ancienne source.

## Arborescence cible

Exemple avec plusieurs disques :

```text
C:\Taikeron\
├─ Launcher\
└─ Apps\
   ├─ Lab\
   └─ MapBuilder\

F:\Taikeron\
├─ DataVault\
└─ Maps\

G:\TaikeronBackup\
└─ DataVault\
   ├─ 2026-09-18_020000\
   ├─ 2026-09-17_020000\
   └─ ...
```

`Apps` contient uniquement du code applicatif remplaçable. `DataVault` contient les données utilisateur persistantes et ne doit jamais être supprimé lors d’une mise à jour d’application.

## Sauvegarde du Data Vault

Le launcher peut créer des snapshots versionnés du Data Vault vers une destination distincte.

Pour chaque snapshot :

1. vérifier que le Data Vault existe ;
2. vérifier que le disque de destination est disponible ;
3. refuser une destination identique ou imbriquée dans le Vault ;
4. copier les fichiers dans un snapshot horodaté ;
5. vérifier les tailles ;
6. vérifier facultativement chaque fichier par SHA-256 ;
7. écrire `_backup-manifest.json` ;
8. appliquer la politique de rétention ;
9. enregistrer la date et l’état de la dernière sauvegarde.

Un disque externe absent n’est pas une panne du launcher. L’état devient `Sauvegarde en attente — disque absent` et une nouvelle vérification est faite ultérieurement.

Dans la v0.4.0, les vérifications automatiques ont lieu au démarrage du launcher et périodiquement tant qu’il reste ouvert. Un worker autonome pourra ensuite assurer les sauvegardes planifiées même lorsque l’interface du launcher est fermée.

## Remplacement propre de TL

Le Launcher utilise un worker externe indépendant de TL.

1. Lire le manifeste stable.
2. Télécharger le paquet dans le dossier de téléchargements configuré.
3. Vérifier la taille attendue.
4. Vérifier le SHA-256 du paquet.
5. Copier le paquet dans le staging du worker et revérifier son SHA-256.
6. Fermer complètement TL.
7. Protéger les cartes locales encore présentes dans le dossier historique.
8. Supprimer entièrement le dossier de code TL.
9. Vérifier que l’ancien dossier a réellement disparu.
10. Lancer l’installateur officiel avec le dossier cible explicite.
11. Vérifier `Taikeron Lab.exe`, `resources/app.asar` et la version installée.
12. Restaurer les cartes protégées.
13. Relancer le contrôle d’intégrité du Launcher.
14. Ne lancer TL que si aucune corruption connue n’est détectée.

Si l’ancien code ne peut pas être supprimé, l’installation s’arrête. Une mise à jour ne doit jamais fusionner deux runtimes.

## Invariants publics

- Une seule installation canonique de chaque application gérée par le launcher.
- Le site public Windows distribue le Launcher et non TL directement.
- Aucun raccourci public ne doit viser une copie historique de TL.
- Une mise à jour ne fusionne jamais deux runtimes.
- Toute différence de version ou d’intégrité déclenche une réinstallation propre : l’ancien dossier code est supprimé avant installation du paquet officiel ; aucun patch incrémental n’est appliqué sur le runtime existant.
- Les anciens fichiers applicatifs doivent être réellement supprimés avant installation du nouveau code.
- Le Launcher distingue `intact`, `non certifié`, `altéré` et `mise à jour disponible`.
- Un TL dont l’intégrité est connue comme invalide n’est pas lancé par le Launcher.
- Les données utilisateur sont physiquement séparées du code.
- Le Data Vault ne doit jamais être supprimé par une mise à jour d’application.
- Le Data Vault n’est jamais envoyé au serveur pour vérifier TL.
- Un paquet dont la taille ou le SHA-256 ne correspond pas au manifeste n’est jamais installé.
- Une sauvegarde du Vault ne doit jamais être stockée à l’intérieur du Vault lui-même.
- Un support de sauvegarde amovible peut être absent sans empêcher le launcher ou TL de fonctionner.
- En cas d’échec, le launcher reste fonctionnel et propose réparation/réinstallation.
- Un éventuel paquet précédent peut être conservé comme archive de récupération, mais pas comme deuxième installation lançable.

## État v0.4.0

La v0.4.0 met en place :

- UI WPF du launcher ;
- détection de Taikeron Lab ;
- lecture correcte du manifeste public `platforms/windows-x64` ;
- normalisation correcte des versions TL `2.302.x-y` ↔ `2.302.x.y` ;
- téléchargement et vérification du paquet officiel ;
- worker externe de remplacement propre ;
- suppression stricte de l’ancien dossier code avant réinstallation ;
- contrôle post-installation de l’EXE et de `app.asar` ;
- manifests d’intégrité WDS générés automatiquement fichier par fichier ;
- manifests d’intégrité archivés par version ;
- vérification d’intégrité à chaque démarrage du Launcher ;
- blocage du lancement si une corruption connue est détectée ;
- configuration persistante Applications / Data Vault / Maps / Téléchargements ;
- sauvegardes manuelles et automatiques du Data Vault ;
- snapshots versionnés et SHA-256 de sauvegarde ;
- gestion d’un disque externe absent ;
- règle de distribution publique Launcher-first.

La prochaine couche de durcissement sera la signature cryptographique du manifeste d’intégrité avec une clé publique embarquée dans le Launcher. Les anciennes releases sans manifeste fiable restent explicitement `non certifiées` plutôt que faussement déclarées saines.
