# Launcher 0.4.18

- Auto-update : progression, octets, débit moyen, numéro de tentative et annulation.
- Délais : 45 s sans réponse/lecture/écriture, 15 min maximum par tentative, 3 tentatives au plus pour les erreurs transitoires. Téléchargement repris de zéro à chaque tentative, fichier partiel supprimé au mieux. Les erreurs HTTP permanentes échouent immédiatement.
- L'ancienne version reste utilisable en cas d'échec/annulation ; bouton de nouvelle tentative conservé. Vérifications taille, SHA-256, marqueur et version conservées. Aucun changement des canaux TL ou des données persistantes.
