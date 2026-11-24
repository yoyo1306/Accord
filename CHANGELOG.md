# Changelog

## 2.0.1 — 2026-11-23

### Correctifs
- **Migration automatique de la tâche planifiée `AccordRestauration`** : l'installateur Inno Setup met désormais automatiquement à jour toute ancienne tâche v1.x pointant vers `Start-RestoreAudioNames.vbs` / `wscript.exe` vers le binaire natif `Accord.Admin.exe --restore-boot --data-dir "%ProgramData%\Accord"` (et la supprime proprement à la désinstallation).
- **Détection des tâches héritées dans l'UI** : `ScheduledTaskManager` n'affiche plus « Restaurer au démarrage » comme activé si la tâche pointe encore vers l'ancien script VBS, et signale dans la barre d'état qu'une migration est nécessaire en mode portable.

## 2.0.0 — 2026-11-21

### Réécriture complète Windows 11 (.NET 10 / WinUI 3)
- **0 prérequis sur PC vierge** : réécriture intégrale en C# / .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`, `self-contained`, `WindowsAppSDKSelfContained=true`), éliminant tous les anciens scripts PowerShell, lanceurs VBS et modules externes (archivés dans `legacy/`).
- **Architecture modulaire à runtime partagé** : séparation `Accord.Core`, `Accord.App` (WinUI 3), `Accord.Watcher` (surveillance arrière-plan) et `Accord.Admin` (élévation ponctuelle registre / `Audiosrv` / tâche planifiée) partageant le même dossier `publish/` sans dupliquer le runtime.
- **Interface Windows 11 (WinUI 3)** : fond **Mica**, barre de titre personnalisée avec zones `Passthrough`, accent système Windows 11, bascule **Clair / Sombre** mémorisée, vue unifiée (bandeau Bascule en direct + onglets Lecture/Enregistrement + inspecteur d'édition), tiroir **Options** coulissant et instance unique avec mise au premier plan.
- **Sécurité & installation `Program Files`** : UI lancée en utilisateur standard, élévation UAC ponctuelle via Named Pipe (`AuthenticatedUserSid` ReadWrite) avec repli fichier, gestion transparente du stockage portable vs `%ProgramData%\Accord\` via `AppPaths`.
- **Profils configurables & forçage rapide** : conservation à 100 % de la détection matérielle Pixel Buds (Bluetooth), Razer BlackShark V3 Pro (dongle HID 2,4 GHz) et Philips Evnia, avec possibilité de forcer un profil en un clic ou de personnaliser les cibles dans Options.
- **Installateur Windows 11 (Inno Setup 6)** : script `installer/Accord.iss` (`lzma2/ultra64`, `WizardStyle=modern dynamic`, français), raccourci directement à la racine du Menu Démarrer sans sous-dossier ni lien « Désinstaller », arrêt automatique des instances en cours, permissions `users-modify` sur `%ProgramData%\Accord`, raccourci Bureau et ajout au `PATH` optionnels.

## 1.2.1 (Archive `legacy/`)
- Interface WinForms `.NET Framework 4` (`Accord.exe`), watcher séparé (`Bascule\Accord.exe`) et scripts PowerShell/VBS (`AutoAudioSwitch.ps1`, `Restore-AudioNames.ps1`).
