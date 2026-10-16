# Intégration TMC et distribution WMS directe

TMC est un troisième produit du Launcher (TL et TMB inchangés). Son logo est embarqué en ressource WPF. Le runtime Portable est installé sous `AppsRoot/MapChooser/Taikeron Map Chooser.exe` et lancé avec `TAIKERON_MAPS_ROOT` et `TAIKERON_MAPS_MANAGED_BY_LAUNCHER`. Le Launcher contrôle taille et SHA-256, suit les versions, installe, met à jour, répare et désinstalle le runtime géré uniquement. Il préserve la bibliothèque Maps et les données utilisateur.

La source TMC `servicesathe-eng/Taikeron-TMC` fournit `.github/workflows/wms.yml` : publication manuelle WMS directe vers le dépôt public distinct `servicesathe-eng/Taikeron-TMC-Releases`, sans passage par DEV. Ce dépôt public et son secret de publication doivent être créés/configurés séparément avant toute distribution. Manifeste stable attendu : `https://raw.githubusercontent.com/servicesathe-eng/Taikeron-TMC-Releases/main/releases/tmc/stable.json`.

Au démarrage de cette intégration, il n'existe pas encore de release WMS publique. L'absence du manifeste empêche de proposer une installation officielle ; elle n'autorise pas l'usage d'une URL inventée. Les identifiants TGG restent expérimentaux et l'intégration n'active pas de téléchargement de TaikerMap TGG1D. Le build Windows réel du Launcher et l'essai du runtime Electron sont encore nécessaires.
