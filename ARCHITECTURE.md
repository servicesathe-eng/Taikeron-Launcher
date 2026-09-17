# Architecture du Taikeron Launcher

## Principe fondamental

Le launcher est le point d’entrée permanent de l’écosystème Taikeron sur Windows. Les applications gérées par le launcher sont considérées comme du code remplaçable.

L’objectif est d’éviter qu’une mise à jour ajoute du nouveau code au-dessus d’un ancien runtime. Pour TL, une mise à jour publique doit supprimer réellement l’ancien code applicatif avant de remettre un paquet neuf et vérifié.

Les données utilisateur appartiennent au **Data Vault**. Elles doivent rester indépendantes du code, de la version de TL et du disque choisi pour les applications.

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

Dans la v0.2.0, les vérifications automatiques ont lieu au démarrage du launcher et périodiquement tant qu’il reste ouvert. Un worker autonome permettra ensuite les sauvegardes planifiées même lorsque l’interface du launcher est fermée.

## Mise à jour TL cible

1. Lire le manifeste stable.
2. Télécharger le paquet dans le dossier de téléchargements configuré.
3. Vérifier la taille attendue.
4. Vérifier le SHA-256.
5. Préparer une requête d’installation pour le worker externe.
6. Fermer TL.
7. Vérifier qu’aucun processus TL n’utilise encore le dossier code.
8. Supprimer entièrement le dossier `Lab` sous la racine Applications configurée.
9. Vérifier que l’ancien code n’existe plus.
10. Recréer `Lab` à partir du nouveau paquet.
11. Vérifier la version et l’intégrité du nouveau runtime.
12. Relancer TL uniquement si l’installation est cohérente.

Le worker est séparé du runtime TL afin de pouvoir remplacer TL alors que TL est arrêté.

## Invariants publics

- Une seule installation canonique de chaque application gérée par le launcher.
- Le site public Windows distribue le Launcher et non TL directement.
- Aucun raccourci public ne doit viser une copie historique de TL.
- Une mise à jour ne fusionne jamais deux runtimes.
- Les anciens fichiers applicatifs doivent être réellement supprimés avant installation du nouveau code.
- Les données utilisateur sont physiquement séparées du code.
- Le Data Vault ne doit jamais être supprimé par une mise à jour d’application.
- Un paquet dont la taille ou le SHA-256 ne correspond pas au manifeste n’est jamais installé.
- Une sauvegarde du Vault ne doit jamais être stockée à l’intérieur du Vault lui-même.
- Un support de sauvegarde amovible peut être absent sans empêcher le launcher ou TL de fonctionner.
- En cas d’échec, le launcher reste fonctionnel et propose réparation/réinstallation.
- Un éventuel paquet précédent peut être conservé comme archive de récupération, mais pas comme deuxième installation lançable.

## État v0.2.0

La v0.2.0 met en place :

- UI WPF du launcher ;
- détection de Taikeron Lab ;
- lecture de la version locale ;
- lecture du manifeste stable public TL ;
- comparaison des versions ;
- téléchargement du paquet ;
- contrôle de taille ;
- contrôle SHA-256 ;
- lancement de TL détecté ;
- configuration persistante des racines Applications / Data Vault / Maps / Téléchargements ;
- destination de sauvegarde configurable ;
- sauvegardes manuelles et automatiques du Data Vault ;
- snapshots versionnés ;
- rétention configurable ;
- manifeste d’intégrité et vérification SHA-256 fichier par fichier ;
- gestion d’un disque de sauvegarde absent ;
- règle de distribution publique Launcher-first.

La suppression/reconstruction atomique du dossier TL sera assurée par le worker externe lors de l’étape suivante. Le déplacement vérifié d’un Data Vault existant et les sauvegardes autonomes lorsque le launcher est fermé sont également des étapes séparées.
