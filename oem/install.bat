@echo off
rem Lo ejecuta dockurr/windows-arm al final de la instalación de Windows (copiado en C:\OEM).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0configurar-smb.ps1"
