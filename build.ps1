$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /win32icon:"$PSScriptRoot\mote.ico" /out:"$PSScriptRoot\Mote.exe" "$PSScriptRoot\Mote.cs"
