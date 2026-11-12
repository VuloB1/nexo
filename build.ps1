# Compiles src\ into Nexo.exe using the .NET Framework compiler that ships with Windows (no SDK needed).
# Run tools\make-icon.ps1 first if src\nexo.ico is missing. Installer: build-setup.ps1 (needs Nexo.exe).
$fw  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
Get-Process Nexo, StartMenuX -ErrorAction SilentlyContinue | Stop-Process -Force
Push-Location $PSScriptRoot\src
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:..\Nexo.exe /win32icon:nexo.ico /win32manifest:..\app.manifest `
    /resource:Menu.xaml,Menu.xaml `
    /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Data.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    /r:$wpf\UIAutomationClient.dll /r:$wpf\UIAutomationTypes.dll /r:$wpf\WindowsBase.dll /r:$wpf\PresentationCore.dll /r:$wpf\PresentationFramework.dll `
    /r:$fw\System.Runtime.WindowsRuntime.dll `
    Program.cs Widgets.cs
Pop-Location
