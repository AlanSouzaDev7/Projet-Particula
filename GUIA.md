# Partículas 3D: guia de gestos

Ao abrir `Particulas.exe`, 9.600 pontos flutuam diante da Via Láctea e a webcam liga
sozinha (uns 5 segundos). Há **três jeitos** de formar objetos:

1. **Pose da mão:** faça a pose e **segure ~meio segundo**. A barra do painel "câmera"
   (canto inferior direito) enche e os pontos montam o objeto.
2. **Desenho no ar:** feche a **pinça** (polegar encostado no indicador), desenhe o traço
   no ar e **abra a pinça** para reconhecer. O traço aparece em dourado na tela.
3. **Mouse:** desenhe o mesmo traço com o botão esquerdo.

## As 18 formas

| Forma | Pose da mão | Traço (pinça no ar ou mouse) |
|---|---|---|
| Coração 3D (bate) | 🤟 Eu te amo, ou coração com as duas mãos | coração |
| Planeta com anéis | 👌 OK | círculo |
| Estrela 3D | ☝️ 1 dedo (indicador) | estrela de 5 pontas |
| Galáxia (gira) | 🤙 Hang loose (polegar + mindinho) | espiral |
| DNA | ✌️ V | letra S |
| Cubo | 4 dedos (polegar dobrado) | quadrado |
| Pirâmide | 3 dedos | triângulo |
| Infinito | ☝️☝️ duas mãos, 1 dedo em cada | ∞ (oito deitado) |
| Rosto feliz 3D | 👍 Joinha | letra U |
| Flor | mindinho sozinho | letra W |
| Raio | 🤘 Rock (indicador + mindinho, polegar dobrado) | letra Z |
| **Rosquinha** | ✊✊ dois punhos | círculo duplo (2 voltas) |
| **Átomo** (elétrons orbitando) | 🖖 Spock | 8 em pé |
| **Terra** (com a Lua) | ✌️✌️ duas mãos em V | arco ∩ |
| **Diamante** | L (polegar + indicador) | losango ◇ |
| **Árvore de Natal** | (só por traço) | chapéu ^ |
| **Tornado** (girando) | 👎 Negativo | letra V |
| **Ondas** (animadas) | Pistola (polegar + indicador + médio) | onda ~ |

As formas fechadas (coração, círculos, estrela, quadrado, losango, triângulo, ∞, 8) podem
ser desenhadas começando em qualquer ponto e em qualquer sentido.

**Tempo entre trocas: 6 segundos.** Depois que uma forma aparece, a próxima troca só é
liberada 6 s depois. Se você já estiver segurando a nova pose, ela entra assim que liberar
(o painel mostra `aguarde X s` com uma barra azul). Traços e passadas feitos durante a
espera são ignorados, com um aviso na tela.

## Controles 3D e efeitos

| Gesto | Efeito |
|---|---|
| 🖐 **Mão aberta** | "segura" o objeto: mover a mão **gira em 3D** (para os lados e para cima/baixo) |
| 👋 Mão aberta passando **rápido** para o lado | próxima / anterior |
| 🖐🖐 **Duas mãos abertas** | afaste para **aproximar (zoom)**, junte para afastar |
| ✊ **Punho** (segure) | desmancha o objeto e espalha os pontos |
| ✊ → 🖐 **Fechar e abrir a mão de repente** | **explosão** de partículas |
| 🤏 Pinça parada (toque) | onda de choque no ponto |
| Traço reto → / ← | próxima / anterior |
| Traço reto ↓ / ↑ | espalhar / reunir de novo |
| Mouse: botão direito / roda | onda de choque / zoom |

Teclado: `H` abre o guia na tela (2 páginas) e `Q`/`Esc` sai.

## O que há de 3D

- Iluminação por superfície: cada ponto sabe para onde sua superfície aponta (normal) e
  recebe luz difusa e **brilho especular**. As formas parecem sólidas.
- **Oclusão:** o que está atrás de uma superfície quase não aparece.
- **Pontilhado por sombreamento:** as figuras continuam feitas de pontos. Nas partes
  iluminadas, as células têm mais pontos acesos, e nas sombras têm menos (como uma gravura
  pontilhista). Assim o volume aparece sem virar bloco sólido.
- **Perspectiva e neblina de profundidade:** o que está longe fica menor e mais escuro.
- **Céu de fundo nítido:** Via Láctea atravessando a tela, com nebulosa colorida e uma
  faixa escura de poeira, uma galáxia distante tipo Andrômeda e ~3.500 estrelas com cores
  de temperatura. O céu se move com **paralaxe quando você gira o objeto com a mão**.
- Formas volumétricas e animadas: o coração é uma superfície implícita 3D, os elétrons
  orbitam o núcleo, a Lua orbita a Terra, a galáxia tem rotação diferencial, o tornado gira
  mais rápido embaixo e as ondas se movem.

## Dicas para a câmera reconhecer bem

- Fique a uns **40–80 cm** da câmera, com a **palma virada para ela** e boa luz.
- Segure a pose com calma até a barra encher. O painel mostra o nome da pose lida.
- **Spock:** separe bem o médio do anelar (formando um V entre eles).
- **L / Pistola:** abra bem o polegar para o lado. Com o polegar dobrado, vira 1 dedo / V.
- **Desenho no ar:** faça traços grandes e solte a pinça no final.

## Instalação (uma vez só)

1. Dê dois cliques em **`instalar.bat`**. Ele instala o MediaPipe (detector de mãos do
   Google) e o modelo em `%LOCALAPPDATA%\Particulas`. Precisa de Python 3.
2. Abra **`Particulas.exe`**.

Para recompilar o `.exe`, use `build.bat` (usa o compilador C# que já vem no Windows).

## Diagnóstico

| Comando | O que faz |
|---|---|
| `Particulas.exe --testar-camera` | liga o rastreador e mostra FPS e poses vistas |
| `Particulas.exe --selftest` | testa o reconhecedor de traços (1.680 traços sintéticos) |
| `Particulas.exe --benchmark` | mede ms por quadro e KB enviados ao terminal |
| `Particulas.exe --verificar-render` | confere a saída diferencial com um terminal virtual |
| `Particulas.exe --verificar-fundo` | mede se o céu atrapalha as figuras (contraste, estrelas no contorno e nos vãos) |
| `Particulas.exe --exportar-png arquivo.png` | salva uma imagem com 6 figuras como aparecem no terminal |
| `Particulas.exe --preview` | imprime uma prévia em texto das 18 formas |
| `…\python.exe maos.py --autoteste` | testa o classificador de poses com mãos sintéticas |
| `…\python.exe maos.py --modelo …\hand_landmarker.task --debug` | janela da câmera com a pose escrita sobre a mão |

(`…\python.exe` = `%LOCALAPPDATA%\Particulas\venv\Scripts\python.exe`)

## Estrutura do código

- `src/Program.cs`: entrada e modos de diagnóstico
- `src/App.cs`: laço principal, gestos, câmera 3D e interface
- `src/Shapes.cs`: as 18 formas e suas animações
- `src/Sim.cs`: física das partículas (molas amortecidas)
- `src/Renderer.cs`: projeção, luz, oclusão, estrelas e saída diferencial
- `src/Recognizer.cs`: reconhecedor de traços ($1 + cantos + espiral)
- `src/Hands.cs`: comunicação com o rastreador e filtro One Euro
- `maos.py`: câmera + MediaPipe + classificação das poses
