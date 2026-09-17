# Architecture du Taikeron Launcher

## Principe fondamental

Le launcher est le point d’entrée permanent de l’écosystème Taikeron sur Windows. Les applications gérées par le launcher sont considérées comme du code remplaçable.

L’objectif est d’éviter qu’une mise à jour ajoute du nouveau code au-dessus d’un ancien runtime. Pour TL, une mise à jour publique doit supprimer réellement l’ancien code applicatif avant de remettre un paquet neuf et vérifié.

## Arborescence cible

```text
%LOCALAPPDATA%\Taikeron\
├─ Launcher\
│  ├─ TaikeronLauncher.exe
│  ├─ Worker\
│  └─ Downloads\
├─ Apps\
│  ├─ Lab\
│  └─ MapBuilder\
└─ Data\
   ├─ Profiles\
   ├─ Activities\
   ├─ Maps\
   └─ Settings\
```

`Apps` contient uniquement du code applicatif remplaçable. `Data` contient les données utilisateur persistantes et ne doit jamais être supprimé lors d’une mise à jour d’application.

## Mise à jour TL cible

1. Lire le manifeste stable.
2. Télécharger le paquet dans `Launcher\Downloads`.
3. Vérifier la taille attendue.
4. Vérifier le SHA-256.
5. Préparer une requête d’installation pour le worker externe.
6. Fermer TL.
7. Vérifier qu’aucun processus TL n’utilise encore le dossier code.
8. Supprimer entièrement `Apps\Lab`.
9. Vérifier que l’ancien code n’existe plus.
10. Recréer `Apps\Lab` à partir du nouveau paquet.
11. Vérifier la version et l’intégrité du nouveau runtime.
12. Relancer TL uniquement si l’installation est cohérente.

Le worker est séparé du runtime TL afin de pouvoir remplacer TL alors que TL est arrêté.

## Invariants publics

- Une seule installation canonique de chaque application gérée par le launcher.
- Aucun raccourci public ne doit viser une copie historique de TL.
- Une mise à jour ne fusionne jamais deux runtimes.
- Les anciens fichiers applicatifs doivent être réellement supprimés avant installation du nouveau code.
- Les données utilisateur sont physiquement séparées du code.
- Un paquet dont la taille ou le SHA-256 ne correspond pas au manifeste n’est jamais installé.
- En cas d’échec, le launcher reste fonctionnel et propose réparation/réinstallation.
- Un éventuel paquet précédent peut être conservé comme archive de récupération, mais pas comme deuxième installation lançable.

## État v0.1.0

La v0.1.0 met en place :

- UI WPF du launcher ;
- détection de Taikeron Lab ;
- lecture de la version locale ;
- lecture du manifeste stable public TL ;
- comparaison des versions ;
- téléchargement du paquet ;
- contrôle de taille ;
- contrôle SHA-256 ;
- lancement de TL détecté.

La suppression/reconstruction atomique du dossier TL sera assurée par le worker externe lors de l’étape suivante.
