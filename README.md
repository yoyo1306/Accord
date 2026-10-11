# AudioRename

Programme Windows pour lister les périphériques de lecture et d'enregistrement, y compris les désactivés et les déconnectés, et pour renommer les deux lignes affichées dans le panneau Son : le nom en gras et la description en gris.

## Utilisation

Lancer `AudioRename.exe`. Windows demande les droits administrateur, parce que ces noms sont stockés dans une clé du registre protégée.

- Deux onglets : **Lecture** et **Enregistrement**.
- Trois cases : **Actifs**, **Désactivés**, **Déconnectés**. Au lancement, seule **Actifs** est cochée.
  - **Désactivés** : les appareils avec la flèche noire, marqués « Désactivé » dans Son.
  - **Déconnectés** : les appareils avec la flèche rouge, marqués « Déconnecté » ou « Non connecté ».
- Sélectionner un appareil, modifier **Nom** et **Description**, puis cliquer **Renommer**. Le service Audio Windows redémarre et le son est coupé une seconde.
- **Actualiser** relit la liste.
- **Backup** enregistre le nom actuel de l'appareil sélectionné dans `AudioRename.backup.txt`, à côté du programme. Le son n'est pas coupé. Chaque appareil a sa propre sauvegarde : un Backup de Philips puis de Razer conserve les deux. Un nouveau Backup du même appareil remplace seulement le sien.
- **Restaurer** remet la sauvegarde de l'appareil sélectionné. Le bouton reste grisé tant qu'aucune sauvegarde n'existe pour lui. Le son est coupé une seconde.

## Compilation

Windows avec le compilateur 64 bits du .NET Framework 4. Le compilateur 32 bits ne voit pas la bonne vue du registre.

```
C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32manifest:app.manifest /out:AudioRename.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll AudioRename.cs
```
