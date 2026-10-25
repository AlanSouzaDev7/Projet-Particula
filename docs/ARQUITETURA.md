# Arquitetura do Partículas 3D

Este documento explica como o programa funciona por dentro: os dois processos, o
protocolo entre eles, cada módulo em C#, o rastreador de mãos em Python, a calibração
pessoal e os testes. Para usar o programa, veja o [README](../README.md) e o
[GUIA](../GUIA.md).

## Sumário

1. [Visão geral](#1-visão-geral)
2. [Compilação](#2-compilação)
3. [Laço principal (App.cs)](#3-laço-principal-appcs)
4. [Formas (Shapes.cs)](#4-formas-shapescs)
5. [Física das partículas (Sim.cs)](#5-física-das-partículas-simcs)
6. [Renderização no terminal (Renderer.cs)](#6-renderização-no-terminal-renderercs)
7. [Reconhecedor de traços (Recognizer.cs)](#7-reconhecedor-de-traços-recognizercs)
8. [Ponte com o rastreador (Hands.cs)](#8-ponte-com-o-rastreador-handscs)
9. [Rastreador de mãos (maos.py)](#9-rastreador-de-mãos-maospy)
10. [Calibração pessoal](#10-calibração-pessoal)
11. [Mãos sintéticas e testes](#11-mãos-sintéticas-e-testes)
12. [Onde ficam os arquivos](#12-onde-ficam-os-arquivos)
13. [Como estender](#13-como-estender)

---

## 1. Visão geral

São **dois processos**:

| Processo | Linguagem | Papel |
|---|---|---|
| `Particulas.exe` | C# 5 (.NET Framework 4), x64 | simulação, renderização no console, gestos de alto nível, interface |
| `maos.py` | Python 3 + MediaPipe | câmera, detecção dos 21 pontos da mão, classificação da pose |

O `.exe` inicia o Python como processo filho (`venv\Scripts\python.exe -u maos.py ...`) e lê
o **stdout** dele, uma linha por quadro da câmera. Não há socket nem arquivo temporário. Quando
o `.exe` fecha, a próxima escrita do Python no stdout falha e ele termina sozinho (não há
thread lendo stdin, o que antes causava um travamento durante os imports).

Essa divisão permite usar o MediaPipe (que só existe para Python/C++/JS) sem amarrar o
programa principal a ele: sem câmera ou sem Python, o `.exe` funciona com o mouse.

```
┌─────────────── Particulas.exe ────────────────┐        ┌──────── maos.py ────────┐
│ Program ─► App.Run()                           │        │ thread da câmera        │
│   laço de 40 Hz:                               │ stdout │  (sempre o quadro mais  │
│     HandTracker.Snapshot() ◄───────────────────┼────────┤   recente)              │
│     gestos → Sim.SetShape / giro / zoom        │        │ HandLandmarker (VIDEO)  │
│     Sim.Step()                                 │        │ medidas → probabilidades│
│     Renderer.Rasterize() → Compose() → Present │        │ fusão temporal, duo     │
└────────────────────────────────────────────────┘        └─────────────────────────┘
```

## 2. Compilação

`build.bat` usa o `csc.exe` do .NET Framework 4, que vem no Windows:

```
csc /optimize+ /target:exe /platform:x64 /codepage:65001 /r:System.Drawing.dll
    /out:Particulas.exe src\*.cs
```

- `/codepage:65001` lê os fontes como UTF-8 (há acentos e símbolos nas strings).
- `System.Drawing` só é usado por `--exportar-png`.
- O código é C# 5 (sem `$"..."`, sem `?.`), a versão que esse compilador aceita.
- O `build.bat` é só ASCII: acentos num `.bat` quebram o `cmd` dependendo da página de código.

## 3. Laço principal (App.cs)

`App.Run()` prepara o console (modo VT para cores de 24 bits, entrada de mouse,
`timeBeginPeriod(1)` para temporização precisa) e roda a **40 quadros por segundo**
(`Dt = 1/40`). A cada quadro:

1. **Entrada:** lê teclado e mouse com `ReadConsoleInput` (sem bloquear) e o último quadro
   do rastreador com `HandTracker.Snapshot()`.
2. **Gestos da mão:**
   - **Pose segurada:** a pose precisa ficar estável por `HoldNeeded`, que depende da
     confiança (≥ 0,75 → 0,32 s; ≥ 0,5 → 0,45 s; abaixo → 0,6 s). Aí `PoseToShape` dá a forma.
   - **Trava de troca:** `ShapeLockSeconds = 2.4`. Durante a trava a pose nova fica na fila e
     entra quando liberar; traços e passadas feitos nesse tempo são ignorados, com aviso.
   - **Mão aberta = segurar:** guarda a posição da mão e o ângulo do objeto ao começar; mover
     a mão gira o objeto (yaw e tilt). Só nesse momento o céu ganha paralaxe.
   - **Passada rápida:** com a mão aberta, um deslocamento horizontal grande em 0,25 s
     troca para a próxima/anterior forma (com 1 s de espera entre passadas).
   - **Duas mãos abertas:** a distância entre elas controla o zoom.
   - **Punho segurado:** espalha os pontos; **punho → mão aberta** rápido: explosão.
   - **Pinça:** enquanto fechada, a ponta do indicador desenha um traço dourado; ao abrir,
     o traço vai para o `Recognizer`. Pinça parada é um toque: onda de choque.
3. **Simulação:** `Sim.Step(time, shape)`.
4. **Renderização:** `Renderer.Rasterize` → `Compose` → `Present`.
5. **Interface:** painel "câmera" no canto (miniatura da mão com os ossos, pose lida,
   confiança, barra de segurar ou barra azul da trava, avisos como "mão longe" ou
   "parece V? firme a pose"), notificações (`Toast`) e o guia de 2 páginas (`H`).

Gestos de duas mãos (`Duo`) vêm prontos do Python: `dois-indicadores` (Infinito),
`dois-v` (Terra), `dois-punhos` (Rosquinha), `duas-abertas` (zoom), `dois-L` (Átomo) e
`coracao` (Coração, as duas mãos formando um coração).

Os modos de diagnóstico (`Preview`, `Benchmark`, `VerifyRender`, `VerifyBackground`,
`ExportPng`) ficam no próprio `App.cs`, porque usam o mesmo pipeline sem abrir a janela
interativa.

## 4. Formas (Shapes.cs)

Cada forma é um `Shape` com:

| Campo | Significado |
|---|---|
| `Name`, `Gesture`, `Hand` | nome e textos de ajuda (traço e pose) |
| `Gen` | gera os `N` alvos: posição 3D, cor, **normal** da superfície e uma `tag` |
| `Anim` | (opcional) move o alvo com o tempo: batimento, órbita, rotação, ondas |
| `Tilt`, `Spin` | inclinação inicial e giro automático |
| `Electric` | tremor elétrico (usado no Raio) |

A **normal** de cada ponto é o que permite iluminar a forma como uma superfície sólida.
Exemplos de geração:

- **Coração:** superfície implícita 3D `(x² + 9/4·d² + v² − 1)³ − x²·v³ − 9/80·d²·v³ = 0`
  (`v` vertical, `d` profundidade); a normal é o gradiente. A animação faz o batimento.
- **Planeta:** esfera com faixas de cor + anéis com espessura; os anéis giram.
- **Galáxia:** braços espirais com rotação diferencial (o centro gira mais rápido).
- **Átomo / Terra:** núcleo e elétrons em órbitas inclinadas / globo e Lua orbitando.
- **Tornado:** funil que gira mais rápido embaixo; **Ondas:** superfície senoidal animada.

## 5. Física das partículas (Sim.cs)

São 9.600 partículas guardadas em **vetores separados** (`X`, `Y`, `Z`, `VX`, ... — formato
"structure of arrays", amigável ao cache). Cada uma é presa ao seu alvo por uma **mola
amortecida**:

```
v = (v + (alvo − posição) · k) · 0,87 + ruído(0,0012)
posição += v
cor += (cor_alvo − cor) · 0,06
```

- `k` varia por partícula, então os pontos chegam em tempos diferentes (efeito orgânico).
- Ao trocar de forma, os alvos são **embaralhados** entre as partículas para que elas
  cruzem a tela em vez de deslizar todas juntas.
- O ruído é baixo de propósito: com muitas partículas, contorno nítido vale mais que tremor.
- `Shock` aplica um impulso radial (onda de choque, explosão); `Snap` põe tudo no alvo
  instantaneamente (usado nos testes e na exportação de imagem).
- O gerador aleatório é um xorshift próprio (`FastRandom`, em `Native.cs`), bem mais rápido
  que `System.Random` no laço interno.

## 6. Renderização no terminal (Renderer.cs)

### Resolução: Braille 2×4

Cada caractere do console é um padrão Braille (U+2800–U+28FF) com **8 pontos** (2 colunas ×
4 linhas). Num terminal de 160×45 células isso dá 320×180 "subpixels" — o suficiente para
formas reconhecíveis — e mantém o visual pontilhado, que é a identidade do projeto.

### Etapas de `Rasterize`

1. **Projeção:** rotação (yaw/tilt) e perspectiva com a câmera a 3,2 unidades; o zoom
   escala a projeção.
2. **Oclusão por célula:** cada célula guarda a menor profundidade vista; pontos bem atrás
   dela quase não aparecem. Isso faz as formas parecerem sólidas.
3. **Iluminação:** difusa + **especular** (Blinn, vetor meio-caminho `H`) + **luz de borda**
   (rim light, que destaca o contorno contra o fundo escuro).
4. **Neblina de profundidade:** o que está longe fica mais escuro.
5. **Pontilhado por sombreamento:** a luminância decide quantos dos 8 pontos da célula
   acendem, numa ordem fixa tipo Bayer (`DitherOrder`). Partes iluminadas ficam densas e
   sombras ficam ralas, como uma gravura pontilhista.

### Céu

- Um **panorama pré-calculado** (`SkySteps = 1536` colunas) guarda a cor da nebulosa da Via
  Láctea, com faixa de poeira escura. Girar o objeto só desloca a leitura na tabela.
- Estrelas: 2.600 na faixa da galáxia, 700 espalhadas e 260 numa galáxia tipo Andrômeda,
  com cores de temperatura (azuladas a alaranjadas).
- **O fundo nunca compete com a figura.** Uma máscara de proximidade classifica cada
  célula (0 figura, 1 colada, 2 perto, 3 transição, 4 longe); a nebulosa e as estrelas são
  apagadas perto do contorno, e os vãos internos da figura (entre os anéis e o planeta, por
  exemplo) também ficam escuros. `--verificar-fundo` mede isso automaticamente.

### Saída diferencial

`Present` compara o quadro novo com o anterior e envia ao terminal só as células que
mudaram, com tolerâncias de cor (`SkipTolerance = 28`, `ReuseTolerance = 20`,
`BgTolerance = 10`) para não reenviar mudanças imperceptíveis e reaproveitar a cor
corrente do terminal. Resultado: ~19 KB por quadro em vez de ~33 KB (−42%), o que evita o
"rasgo" de tela. `--verificar-render` aplica a saída num terminal virtual e confere que
o resultado é igual ao quadro desejado (0 caracteres errados em 1.080 quadros).

Custo medido (`--benchmark`): **~1,6 ms por quadro** para projeção, luz, oclusão e saída.

## 7. Reconhecedor de traços (Recognizer.cs)

Baseado no algoritmo **$1 Unistroke Recognizer** (Wobbrock et al.), com extensões:

1. **Reamostragem** do traço em 64 pontos, escala para uma caixa e translação ao centro.
   **Sem normalizar a rotação** (um V e um ^ precisam ser diferentes).
2. **Formas fechadas** (coração, círculo, estrela, quadrado, losango, triângulo, ∞, 8) são
   comparadas **ciclicamente e nos dois sentidos**: dá para começar em qualquer ponto.
3. **Contagem de cantos:** o traço é suavizado e conta-se as viradas maiores que 95°;
   formas com número de cantos incompatível são penalizadas (resolve U × V e ∩ × ^).
4. **Espiral:** heurística própria (ângulo acumulado e raio crescente) → Galáxia.
5. **Linhas retas:** detectadas antes ($1 não lida bem com elas) → próxima/anterior,
   espalhar/reunir.
6. Pontuação mínima `MinScore = 0.72`: abaixo disso, o traço é recusado em vez de virar
   uma forma errada.

Modelos: coração, círculo, círculo duplo, estrela (pentagrama e contorno), S, quadrado,
losango, triângulo, infinito, 8 em pé, U, arco ∩, W, Z, V, chapéu ^ e onda ~ (a espiral
é reconhecida só pela geometria; os gabaritos dela servem apenas ao autoteste).

`--selftest` (`SelfTest.cs`) gera 1.680 traços sintéticos com ruído, escala, inclinação e
ponto de início aleatórios, mais rabiscos que devem ser recusados: **98,8% de acerto**.

## 8. Ponte com o rastreador (Hands.cs)

`HandTracker`:

- Procura o Python em `%LOCALAPPDATA%\Particulas\venv` e o `maos.py` **ao lado do `.exe`**
  (ou, se não houver, na pasta de dados). Assim, editar o `maos.py` do projeto já vale.
- Lê o stdout numa thread em segundo plano e guarda só o último `HandFrame`
  (`Snapshot(out age)` devolve também a idade do quadro).
- Linhas `OK ...` / `ERR ...` viram o `Status` (por exemplo, "câmera ativa (perfil pessoal)").
- Com `--simular`, as linhas `S <pose>` informam qual pose sintética está sendo enviada,
  para o teste de integração comparar.

Cada `Hand` traz pose, lado, confiança, pose provável, qualidade, centro, tamanho, posição
da pinça e da ponta do indicador e os 21 pontos 2D. As posições usadas para girar e
desenhar passam por um **filtro One Euro** (suaviza parado, responde rápido em movimento).

### Protocolo (uma linha por quadro)

```
F <n> <duo>|<pose> <lado> <conf> <provável> <qualidade> <cx> <cy> <tam> <pinçaX> <pinçaY> <pontaX> <pontaY> <x0> <y0> ... <x20> <y20>|...
```

Coordenadas de 0 a 1, espelhadas (como um espelho). `<duo>` é o gesto de duas mãos ou `-`.
`<provável>` é a pose mais parecida mesmo quando `<pose>` é `outra` (usado na dica "parece
V?"). `<qualidade>` é `ok`, `longe` ou `borda`.

## 9. Rastreador de mãos (maos.py)

### Captura e detecção

- Uma **thread da câmera** lê continuamente e guarda só o quadro mais recente (o
  processamento nunca trabalha sobre quadros velhos).
- **MediaPipe Tasks `HandLandmarker`** em modo VIDEO, até 2 mãos. Ele devolve 21 pontos
  de imagem e 21 pontos "do mundo" (em metros, centrados na mão).
- Os pontos do mundo são suavizados por mão (`HandState`, média exponencial α = 0,55).

### Medidas de base longa

A profundidade estimada por uma webcam comum é ruidosa. Ângulos de juntas curtas (entre
falanges vizinhas) variam muito de um quadro para outro, então **cada dedo é medido por
três grandezas longas** (`raw_measures`):

| Medida | Definição | Dedo esticado |
|---|---|---|
| alcance | distância base→ponta ÷ comprimento do dedo | perto de 1 |
| ponta-palma | distância da ponta ao centro da palma, em "palmos" | grande |
| curvatura | ângulo entre base→meio e meio→ponta | pequeno |

O polegar usa a **posição lateral** (quanto a ponta passa do indicador, ao longo da
largura da palma) e a distância ao centro da palma. Há ainda a abertura polegar-indicador
(para L e pistola), a distância da pinça, a separação médio-anelar no plano da palma e a
razão entre as pontas (Spock), e a direção do polegar na imagem (joinha/negativo).

### Probabilidades e pontuação

Cada medida passa por uma **logística suave** em torno de um limiar (`DEFAULT_PARAMS`, que
o perfil pessoal pode substituir), virando uma probabilidade de 0 a 1. Cada dedo é a média
das suas três medidas (se uma falha num quadro, as outras seguram).

Cada pose é definida em `POSE_SPECS` pelo estado de polegar, indicador, médio, anelar e
mindinho (1 esticado, 0 dobrado, `None` tanto faz):

```python
"aberta": (1, 1, 1, 1, 1)   "punho": (None, 0, 0, 0, 0)   "L": (1, 1, 0, 0, 0)
"v": (None, 1, 1, 0, 0)     "rock": (0, 1, 0, 0, 1)       "teamo": (1, 1, 0, 0, 1)  ...
```

A pontuação é o produto das probabilidades, multiplicado por condições extras (pinça para
OK, abertura para Spock, direção do polegar para joinha/negativo, abertura do polegar para
L/pistola).

### Decisão com recusa

`decide()` só aceita a melhor pose se ela tiver pontuação mínima (0,2), pelo menos 50% do
total e **1,5× a segunda colocada**. Caso contrário a pose é `outra` e o app mostra a dica
"parece X? firme a pose". A confiança é `√(pontuação × participação)`: alta só quando a
pose é forte **e** sem rival.

### Fusão temporal

As pontuações se acumulam entre quadros (decaimento 0,7), com **histerese**: para trocar
de pose, a nova precisa de folga clara sobre a atual. A pinça também tem histerese (fecha
a 0,45 palmo, só abre a 0,62), para o traço no ar não se partir.

### Qualidade

Mão pequena demais na imagem gera `longe`; mão cortada na borda gera `borda`. Nesses casos
o app avisa em vez de dar palpite.

## 10. Calibração pessoal

Fluxo do `calibrar.bat` (`maos.py --coletar`):

1. **Coleta guiada (`collect`):** para cada pose, 6 s de preparação (espaço adianta), espera
   até a pose ficar verde (até 6 s) e 3 s de gravação. Um painel mostra cada dedo ao vivo
   (LEVANTAR/DOBRAR em verde ou vermelho). `R` descarta e repete; a pose que conferir em
   menos de 50% dos quadros é repetida automaticamente uma vez. Grava em
   `%LOCALAPPDATA%\Particulas\coleta-AAAAMMDD-HHMMSS.jsonl`, **só números**.
2. **Conferência (`check_pose`):** cada quadro é checado contra a definição da pose (um dedo
   no estado oposto, pinça aberta, Spock sem separação). Poses feitas de outro jeito ficam
   de fora e são listadas para refazer. Rodadas seguintes pedem só as pendentes.
3. **Melhores quadros (`best_frames`):** junta todas as coletas e usa, para cada pose, a
   coleta em que ela conferiu melhor.
4. **Aprendizado (`learn_params`):** para cada limiar, procura o ponto que melhor separa os
   quadros positivos dos negativos (`fit_threshold`) e **puxa 25% de volta ao padrão**, para
   não se ajustar demais a uma sessão. Regras especiais para pinça, abertura do polegar e
   ângulo do joinha.
5. **Validação honesta:** descarta o começo e o fim de cada gravação (transições), calibra
   com **metade** dos quadros e mede na **outra metade**. Mostra antes × depois.
6. **Só salva se ajudar:** o `perfil.json` é gravado apenas se o ganho for de pelo menos
   0,5 ponto percentual; senão, o perfil antigo é removido e o padrão continua valendo.

Com a mão real do autor: 16 de 17 poses conferidas (o Spock não é anatomicamente possível
para ele, daí a alternativa "duas mãos em L"), **97,2% de acerto na validação e 0% de
disparo errado**.

## 11. Mãos sintéticas e testes

`synth_hand()` gera mãos a partir de um **modelo cinemático** (comprimentos de falanges,
ângulos de flexão e abertura por dedo), com variações controladas:

| Modo | O que varia |
|---|---|
| normal | pequenas variações de pose e de orientação |
| difícil | poses no limite (dedos meio dobrados, polegar pouco aberto) |
| realista | mão esquerda/direita, costas da mão, tamanhos, **ruído de profundidade** (×2,5) e "estilos" de mão |

`--autoteste` mede o classificador quadro a quadro e "como no app" (com suavização e
fusão), com metas: disparo errado por quadro ≤ 3%, pose certa estável ≥ 99% e no máximo 3
trocas indevidas. Resultados atuais: **100% / 99,7% / 100%** de pose certa nos três modos.

`--simular` envia essas mãos pelo protocolo real, e `Particulas.exe --testar-camera
--simular` confere do lado C#: **17 poses, 100%**.

| Teste | Onde | Mede |
|---|---|---|
| `--selftest` | C# | reconhecedor de traços |
| `--verificar-render` | C# | saída diferencial × terminal virtual |
| `--verificar-fundo` | C# | interferência do céu na figura |
| `--benchmark` | C# | ms por quadro e KB por quadro |
| `--testar-camera [--simular]` | C# + Python | integração ponta a ponta |
| `maos.py --autoteste` | Python | classificador com mãos sintéticas |
| `maos.py --avaliar` | Python | classificador com coletas reais |

## 12. Onde ficam os arquivos

| Caminho | Conteúdo | Vai para o Git? |
|---|---|---|
| pasta do projeto | código, `.exe`, scripts, documentação | sim |
| `%LOCALAPPDATA%\Particulas\venv` | Python + MediaPipe | não |
| `%LOCALAPPDATA%\Particulas\hand_landmarker.task` | modelo do MediaPipe | não |
| `%LOCALAPPDATA%\Particulas\coleta-*.jsonl` | coletas de calibração (só números) | não |
| `%LOCALAPPDATA%\Particulas\perfil.json` | limiares pessoais | não |

## 13. Como estender

**Nova forma:**
1. Em `Shapes.cs`, crie a função que gera os alvos (posição, cor, normal) e, se quiser,
   um `Animator`; registre no array de formas com um índice `Xxx_`.
2. Em `Recognizer.cs`, adicione um modelo de traço com `Add("nome", Shapes.Xxx_, fechado, pontos)`.
3. Em `App.cs`, ligue uma pose em `PoseToShape` e atualize a ajuda e o `GUIA.md`.
4. Rode `--preview`, `--selftest` e `--verificar-fundo`.

**Nova pose da mão:**
1. Em `maos.py`, adicione a linha em `POSE_SPECS` (e condições extras em `pose_scores`, se
   precisar).
2. Ensine a pose ao gerador sintético (`synth_hand`) e confira com `--autoteste`.
3. Inclua a pose na coleta (`collect`) e em `check_pose`, e ligue-a a uma forma em `App.cs`.
