# Accord

Renomme les périphériques audio de Windows, et garde ces noms. Dès que Windows 11 remet un nom par défaut, le logiciel remet le nom enregistré. Le nom court apparaît dans la barre des tâches. La description est celle du panneau Son.

![Accord](screenshot.png)

## Utilisation

Lancer `Accord.exe`. Windows demande les droits administrateur : les noms sont dans une clé du registre protégée, et le service Audio est redémarré une seconde le temps d'écrire le nom du panneau Son.

- **Lecture** et **Enregistrement**.
- **Actifs**, **Désactivés**, **Déconnectés**. Au lancement, seule **Actifs** est cochée.
- Le profil **Hands-Free** n'apparaît pas dans la liste.
- Sélectionner un appareil, modifier **Nom** et **Description**, puis **Renommer**.
  - La barre des tâches affiche `Nom (Description)`.
  - Le panneau Son affiche la description.
- **Actualiser** relit la liste.
- **Sauvegarder** enregistre l'appareil sélectionné dans `Accord.backup.txt`, à côté du programme. Le son n'est pas coupé. Chaque appareil a sa sauvegarde.
- **Restaurer** remet la sauvegarde de l'appareil sélectionné. Le bouton reste grisé tant qu'il n'y en a pas.
- Le numéro de version s'affiche en bas à droite.

**Restaurer la dernière sauvegarde au démarrage** crée la tâche `AccordRestauration`. Elle s'exécute 15 secondes après l'ouverture de session, avec les droits administrateur, et réécrit les noms sauvegardés, sans fenêtre. Une entrée Hands-Free déjà présente dans la sauvegarde est ignorée.

**Bascule automatique du son** surveille les casques et change le périphérique par défaut, sans couper le son :

| Branché | Lecture | Micro |
| --- | --- | --- |
| Pixel Buds | stéréo | Pixel Buds |
| Razer BlackShark allumé | Game | Chat |
| aucun des deux | Philips Evnia | inchangé |

Si les deux casques sont là, le dernier connecté gagne. Au démarrage, l'ordre est Pixel, puis Razer, puis l'écran. Les appareils renommés sont reconnus par la sauvegarde, pas par leur ancien nom Windows. Le Razer n'est pris que s'il est vraiment allumé, pas seulement si le dongle est branché. Le profil Hands-Free n'est pas choisi.

Dès que Windows 11 remet le nom par défaut d'un appareil sauvegardé, la bascule le remet tout de suite, puis autant de fois que nécessaire, sans couper le son. Fermer Accord ne l'arrête pas. Elle se relance à l'ouverture de session, sans fenêtre, via `Bascule\Accord.exe`. Ce processus suffit : il ne lance ni PowerShell ni le compilateur C#.

## Installation

Télécharger `Accord-1.3.0.zip` depuis les [releases](https://github.com/yoyo1306/Accord/releases), le décompresser, puis lancer `Accord.exe`. Garder les fichiers ensemble.

## Compilation

Windows, compilateur 64 bits du .NET Framework 4. Le compilateur 32 bits ne voit pas la bonne vue du registre.

```
C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32manifest:app.manifest /win32icon:AudioRename.ico /out:Accord.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll AudioRename.cs
```

La bascule, sans droits administrateur :

```
C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32icon:AudioRename.ico /out:Bascule\Accord.exe Bascule\Accord.cs RazerDongle.cs
```

## Crédits

Le changement de périphérique par défaut utilise [AudioDeviceCmdlets](https://github.com/frgnca/AudioDeviceCmdlets) 3.1.0.2, © 2016–2022 François Gendron, licence MIT.
