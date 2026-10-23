@echo off
rem Compila Particulas.exe com o compilador C# do .NET Framework 4 (vem com o Windows).
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Nao encontrei o csc.exe do .NET Framework 4.
  exit /b 1
)
"%CSC%" /nologo /optimize+ /target:exe /platform:x64 /codepage:65001 /out:"%~dp0Particulas.exe" "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Pronto: %~dp0Particulas.exe
