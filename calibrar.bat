@echo off
rem Coleta guiada das poses + calibracao pessoal do reconhecimento de maos.
rem Grava so os numeros dos pontos da mao (nada de imagem) e cria perfil.json.
setlocal
set "DEST=%LOCALAPPDATA%\Particulas"
if not exist "%DEST%\venv\Scripts\python.exe" (
  echo Rode instalar.bat primeiro.
  exit /b 1
)
echo.
echo  CALIBRACAO PESSOAL DOS GESTOS
echo  - Feche o Particulas.exe (a camera precisa estar livre).
echo  - Uma janela com a camera pede as poses, uma de cada vez. Na primeira vez sao
echo    as 17; depois, so as que ainda nao conferiram (use --tudo para refazer todas).
echo  - Embaixo, cada dedo aparece em VERDE (certo) ou VERMELHO (ajuste) ao vivo.
echo    6 s para ler e se preparar (ESPACO comeca antes) e 3 s gravando.
echo    Mantenha a mao inteira na imagem.
echo  - R repete a pose atual (a tentativa errada e descartada), Esc cancela.
echo  - Pose que nao conferir e repetida automaticamente uma vez.
echo.
pause
"%DEST%\venv\Scripts\python.exe" "%~dp0maos.py" --modelo "%DEST%\hand_landmarker.task" --coletar %*
echo.
echo Para voltar aos limiares padrao, apague %DEST%\perfil.json
pause
