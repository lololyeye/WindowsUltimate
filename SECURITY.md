# Security policy

Windows Ultimate changes Windows settings and its installer runs with administrator rights, so security problems matter. Thank you for reporting them responsibly.

*Version française ci-dessous.*

## Supported versions

Only the **latest release** published on [GitHub Releases](https://github.com/lololyeye/WindowsUltimate/releases/latest) receives security fixes. If you use an older version, update first (the app offers updates at startup, or use *Settings → Check now*).

## How to report a vulnerability

Please **do not open a public issue** for a security problem. Instead:

- write to **contact@aoze.fr** with "Security" in the subject.

Include, if you can:

- the version of Windows Ultimate (*Settings*) and of Windows;
- what the problem is and what an attacker could do with it;
- the steps to reproduce it, or a proof of concept;
- whether you have shared it with anyone else.

Don't send personal files, passwords or your full History export: they are not needed, and an export can contain file paths that include your Windows user name.

## What happens next

Windows Ultimate is maintained by one person, in their spare time, so no response time is guaranteed. In practice:

1. You get a reply confirming the report has been read.
2. The problem is checked and, if confirmed, fixed in a new release as soon as possible. The release notes mention the fix.
3. You are credited in the release notes if you wish.

Please give a reasonable time for a fix before making the problem public. If a vulnerability is being actively exploited, say so in the subject line so it is handled first.

## Scope

In scope: the Windows Ultimate application, its installer, the update mechanism and the release workflow in this repository, and the website.
Out of scope: problems in Windows itself, in GitHub, or in third-party components (report those to their maintainers; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)), and reports that require an attacker who already has administrator rights on the PC.

## How releases are protected today

- Releases are built by GitHub Actions from this repository and published on GitHub Releases, with a `.sha256` file for the installer.
- The in-app updater only downloads from `github.com` over HTTPS, and refuses an installer whose SHA-256 doesn't match the published file.
- The installer is **not code-signed yet**. The checksum confirms the file wasn't corrupted or swapped after publication, but both the installer and its checksum come from the same GitHub release.

---

# Politique de sécurité

Windows Ultimate modifie des réglages de Windows et son installateur s'exécute avec les droits administrateur : les problèmes de sécurité comptent. Merci de les signaler de façon responsable.

## Versions prises en charge

Seule la **dernière version** publiée sur [GitHub Releases](https://github.com/lololyeye/WindowsUltimate/releases/latest) reçoit des correctifs de sécurité. Si vous utilisez une version plus ancienne, mettez-la d'abord à jour.

## Signaler une vulnérabilité

**N'ouvrez pas d'issue publique** pour un problème de sécurité. Écrivez plutôt à **contact@aoze.fr** avec « Sécurité » dans l'objet, en indiquant si possible : la version de Windows Ultimate et de Windows, le problème et ce qu'un attaquant pourrait en faire, les étapes pour le reproduire, et si vous l'avez signalé à quelqu'un d'autre. N'envoyez ni fichiers personnels, ni mots de passe, ni export complet de l'historique : un export peut contenir des chemins de fichiers avec votre nom d'utilisateur Windows.

## La suite

Windows Ultimate est maintenu par une seule personne, sur son temps libre : aucun délai de réponse n'est garanti. Vous recevez une réponse confirmant la lecture du signalement ; le problème est vérifié puis, s'il est confirmé, corrigé dans une nouvelle version dès que possible, avec une mention dans les notes de version (et votre nom si vous le souhaitez). Merci de laisser un délai raisonnable avant de rendre le problème public. Si une faille est activement exploitée, indiquez-le dans l'objet pour qu'elle soit traitée en priorité.

## Protection actuelle des versions

Les versions sont compilées par GitHub Actions et publiées sur GitHub Releases avec un fichier `.sha256`. La mise à jour intégrée ne télécharge que depuis `github.com` en HTTPS et refuse un installateur dont l'empreinte ne correspond pas. L'installateur **n'est pas encore signé numériquement** : l'empreinte prouve que le fichier n'a pas été altéré après publication, mais l'installateur et son empreinte proviennent de la même version GitHub.
