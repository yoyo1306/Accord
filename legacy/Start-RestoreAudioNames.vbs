' Lance la restauration des noms audio sans fenetre.
Set fso = CreateObject("Scripting.FileSystemObject")
script = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "Restore-AudioNames.ps1")
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & script & """"
CreateObject("WScript.Shell").Run cmd, 0, True
