# AGENTS.md — Launcher

## Langage commun Taikeron — obligatoire

Avant toute modification d'un contrat, frontière, format, adaptateur ou consommateur de données, lire `servicesathe-eng/123/LANGUAGE/README.md`, `registry.v1.json`, `vocabulary.v1.json`, `CHECKS.md` et respecter le verrou `.github/language-binding.json`. Depuis le checkout 123, exécuter `node LANGUAGE/verify.cjs --product Launcher --checkout <racine-du-depot>` et `python LANGUAGE/validate.py`. Toute dérive nécessite une migration explicite et coordonnée ; ne jamais régénérer aveuglément le verrou. Les règles propres au produit restent applicables. Aucun accès réseau au runtime pour consulter 123. TW suspendu (DECISION-007), aucune nouvelle dépendance ni activation météo. Aucun changement de runtime, de poids ou de publication par cette règle.
