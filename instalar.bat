@echo off
rem Instala o rastreador de maos (Python + MediaPipe) usado pelo Particulas.exe.
rem Tudo fica em %LOCALAPPDATA%\Particulas; rode de novo para reparar a instalacao.
setlocal
set "DEST=%LOCALAPPDATA%\Particulas"
set "VENV=%DEST%\venv"
set "MODEL=%DEST%\hand_landmarker.task"
set "URL=https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task"

where py >nul 2>nul && (set "PY=py -3") || (set "PY=python")
%PY% --version >nul 2>nul
if errorlevel 1 (
  echo Python nao encontrado. Instale em https://www.python.org/downloads/ e rode de novo.
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"
if not exist "%VENV%\Scripts\python.exe" (
  echo [1/3] Criando ambiente Python em %VENV% ...
  %PY% -m venv "%VENV%" || exit /b 1
)

echo [2/3] Instalando MediaPipe (pode levar alguns minutos) ...
"%VENV%\Scripts\python.exe" -m pip install --disable-pip-version-check -q --upgrade pip
"%VENV%\Scripts\python.exe" -m pip install --disable-pip-version-check -q "mediapipe>=0.10.30" || exit /b 1

if not exist "%MODEL%" (
  echo [3/3] Baixando o modelo de deteccao de maos ...
  "%VENV%\Scripts\python.exe" -c "import urllib.request,sys; urllib.request.urlretrieve(sys.argv[1], sys.argv[2])" "%URL%" "%MODEL%" || exit /b 1
) else (
  echo [3/3] Modelo ja baixado.
)

copy /y "%~dp0maos.py" "%DEST%\maos.py" >nul
echo.
echo Pronto! Abra o Particulas.exe e mostre a mao para a camera.
