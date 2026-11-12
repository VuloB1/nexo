# Builds dist\Nexo-Setup.exe (installer + uninstaller with Nexo.exe embedded). Run build.ps1 first.
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
Push-Location $PSScriptRoot
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /out:dist\Nexo-Setup.exe /win32icon:src\nexo.ico /win32manifest:setup\setup.manifest `
    "/resource:Nexo.exe,Nexo.exe" "/resource:src\nexo.ico,nexo.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll setup\Setup.cs
Pop-Location
