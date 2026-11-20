@echo off
rem Rebuilds DataUsage.exe using the C# compiler built into Windows.
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 ^
  /out:"%~dp0..\DataUsage.exe" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%FW%\System.Threading.Tasks.dll" ^
  /r:"C:\Windows\System32\WinMetadata\Windows.Networking.winmd" ^
  /r:"C:\Windows\System32\WinMetadata\Windows.Foundation.winmd" ^
  /win32icon:"%~dp0app.ico" /resource:"%~dp0app.ico",app.ico ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  "%~dp0DataUsage.cs"
