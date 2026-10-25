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
| **Átomo** (elétrons orbitando) | 🖖 Spock, ou **duas mãos em L** (moldura) | 8 em pé |
| **Terra** (com a Lua) | ✌️✌️ duas mãos em V | arco ∩ |
| **Diamante** | L (polegar + indicador) | losango ◇ |
| **Árvore de Natal** | (só por traço) | chapéu ^ |
| **Tornado** (girando) | 👎 Negativo | letra V |
| **Ondas** (animadas) | Pistola (polegar + indicador + médio) | onda ~ |

As formas fechadas (coração, círculos, estrela, quadrado, losango, triângulo, ∞, 8) podem
ser desenhadas começando em qualquer ponto e em qualquer sentido.

**Tempo entre trocas: 4,3 segundos.** Depois que uma forma aparece, a próxima troca só é
liberada 4,3 s depois. Se você já estiver segurando a nova pose, ela entra assim que liberar
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

## Como a câmera reconhece as poses

- Cada detalhe da mão (cada dedo esticado ou dobrado, polegar para fora, pinça, abertura
  do Spock, direção do joinha) vira uma **probabilidade**, calculada pelos ângulos das juntas
  em 3D. Cada pose recebe uma pontuação.
- As pontuações são **acumuladas ao longo dos quadros**: um quadro tremido ou ambíguo é
  resolvido pelos vizinhos, e a pose só troca quando a nova tem folga clara.
- Se a decisão ficar **ambígua**, o programa não chuta: o painel mostra "parece V? firme a
  pose". Mão **longe** ou **cortada na borda** também gera aviso em vez de palpite.
- O tempo para confirmar se ajusta à certeza: ~0,3 s quando a pose está clara e ~0,6 s
  quando está duvidosa. O painel mostra a confiança em %.

Os dedos são medidos por **distâncias e ângulos de base longa** (alcance do dedo, ponta até
a palma), que resistem ao erro de profundidade da câmera. Em testes com mãos sintéticas
"realistas" (esquerda/direita, costas da mão, tamanhos diferentes, ruído de profundidade),
a pose correta fica estável em 100% do tempo, com zero trocas indevidas.

## Calibração pessoal (recomendado, ~2 minutos)

Cada mão é diferente: tem gente que não estica totalmente os dedos, que fecha o punho
frouxo ou que abre pouco o polegar. Dê dois cliques em **`calibrar.bat`** (com o
Particulas.exe fechado). Uma janela com a câmera pede as 17 poses, uma de cada vez: 6 s
para ler e se preparar (a barra de espaço começa antes) e 3 s gravando.

- Embaixo da imagem, um painel mostra **cada dedo ao vivo**: LEVANTAR/DOBRAR, em **verde** quando está
  certo e **vermelho** quando precisa ajustar. Ao ficar tudo verde aparece "POSE CERTA!".
- Pose que não conferir durante a gravação é repetida automaticamente uma vez.
- Nas próximas vezes, o calibrar.bat pede **só as poses que ainda não conferiram**
  (para refazer todas: `calibrar.bat --tudo`). Poses feitas de outro jeito (um dedo no
  estado oposto) ficam de fora da calibração e são listadas para refazer.
- Grava **só os números** dos 21 pontos da mão, **nunca imagens**.
- Ao final, o programa aprende os seus limiares e **valida de forma honesta**: calibra
  com metade dos quadros e mede na outra metade. Mostra o antes e o depois, e só salva o
  perfil se ele melhorar o reconhecimento.
- O perfil fica em `%LOCALAPPDATA%\Particulas\perfil.json`. O app passa a usá-lo e mostra
  "câmera ativa (perfil pessoal)". Para voltar ao padrão, apague esse arquivo.

Nos testes com mãos simuladas fora do padrão, a calibração levou o "punho frouxo" de 74%
para 100%, o "polegar pouco aberto" de 87% para 100% e um caso extremo de 80% para 97%.

## Dicas para a câmera reconhecer bem

- Fique a uns **40–80 cm** da câmera, com a **palma virada para ela** e boa luz.
- Segure a pose com calma até a barra encher. O painel mostra o nome da pose lida.
- **Spock:** separe bem o médio do anelar (formando um V entre eles). Se não conseguir
  (muita gente não consegue), use **duas mãos em L** para o Átomo.
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
| `Particulas.exe --testar-camera --simular` | sem câmera: o rastreador envia mãos sintéticas das 17 poses e o app confere cada uma |
| `Particulas.exe --selftest` | testa o reconhecedor de traços (1.680 traços sintéticos) |
| `Particulas.exe --benchmark` | mede ms por quadro e KB enviados ao terminal |
| `Particulas.exe --verificar-render` | confere a saída diferencial com um terminal virtual |
| `Particulas.exe --verificar-fundo` | mede se o céu atrapalha as figuras (contraste, estrelas no contorno e nos vãos) |
| `Particulas.exe --exportar-png arquivo.png` | salva uma imagem com 6 figuras como aparecem no terminal |
| `Particulas.exe --preview` | imprime uma prévia em texto das 18 formas |
| `…\python.exe maos.py --autoteste` | testa o classificador com mãos sintéticas normais, difíceis e realistas (esquerda/direita, costas, tamanhos, ruído de profundidade), com metas |
| `…\python.exe maos.py --avaliar coleta.jsonl` | mede o reconhecimento numa coleta gravada (padrão × perfil pessoal) |
| `…\python.exe maos.py --calibrar coleta.jsonl` | refaz o perfil pessoal a partir de uma coleta |
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

Detalhes técnicos em [docs/ARQUITETURA.md](docs/ARQUITETURA.md) e a evolução do projeto em
[docs/HISTORICO.md](docs/HISTORICO.md).
