# Histórico do desenvolvimento

Como o Partículas 3D foi construído, etapa por etapa, com os pedidos que guiaram cada
fase, as decisões técnicas e os números medidos. Cada etapa corresponde a um commit.

## Etapa 1 — Pontos que viram formas, comandados pela mão

**Commit:** `Partículas 3D: formas 3D, gestos da mão e renderização otimizada`

**Pedido:** um programa executável no `cmd` em que vários pontos na tela formam objetos
(coração, planeta e outros), com um guia de quais gestos formam quais objetos. Em seguida:
trocar as teclas por gestos da mão detectados pela câmera do PC; melhorar o reconhecimento
de gestos e o desempenho; aumentar as bibliotecas de gestos e de formas; dar elementos 3D.

**O que foi feito:**

- Programa em **C# compilado com o `csc.exe` que já vem no Windows** (nenhuma instalação
  para compilar), desenhando com **caracteres Braille** (8 pontos por caractere) e cor de
  24 bits no console.
- **Física de molas amortecidas**: cada ponto é puxado para o seu lugar na forma; ao trocar
  de forma, os destinos são embaralhados e os pontos cruzam a tela.
- **Rastreador de mãos em Python** com o MediaPipe, rodando como processo filho e falando
  com o `.exe` pelo stdout, com a câmera numa thread própria (sempre o quadro mais
  recente). `instalar.bat` monta tudo em `%LOCALAPPDATA%\Particulas`.
- Biblioteca de gestos: poses de uma mão (OK, V, L, Spock, rock, hang loose, joinha,
  negativo, pistola...) e de duas mãos (coração com as mãos, dois V, dois punhos...).
- **Reconhecedor de traços** ($1 Unistroke + cantos + espiral) para desenhar no ar com a
  pinça ou com o mouse.
- **18 formas 3D** com normais de superfície, iluminação difusa + especular, oclusão,
  perspectiva e neblina, e animações (coração batendo, elétrons, Lua, tornado, ondas).
- Controles 3D: mão aberta segura e gira o objeto, duas mãos dão zoom, punho espalha,
  fechar e abrir rápido explode.
- **Saída diferencial**: só as células que mudaram são reenviadas ao terminal.

**Problemas resolvidos:** um `rem` com acento no `.bat` quebrava o `cmd` (os `.bat` passaram
a ser só ASCII); o rastreador ficava mudo dentro do `.exe` porque uma thread lendo o stdin
travava os imports (removida: o Python agora termina quando a escrita no stdout falha);
U/∩ se confundiam com V/^ no reconhecedor de traços (resolvido contando cantos).

## Etapa 2 — Via Láctea, 9.600 partículas e qualidade de imagem

**Commit:** `Céu da Via Láctea, 9.600 partículas pontilhadas e trava de 6 s`

**Pedido:** deixar mais nítida a galáxia ao fundo; aumentar para 6 s o tempo entre trocas
de forma; qualidade excelente nas figuras mantendo os traços pontilhados; **o triplo de
partículas**, sem perder o fundamento do projeto. Depois: verificar se o fundo interfere
na imagem principal, visual e esteticamente.

**O que foi feito:**

- **3.200 → 9.600 partículas**, com ruído de movimento menor para contornos mais nítidos.
- **Céu da Via Láctea** pré-calculado num panorama: nebulosa colorida, faixa escura de
  poeira, galáxia tipo Andrômeda e ~3.500 estrelas com cores de temperatura; paralaxe
  somente enquanto a mão gira o objeto.
- **Pontilhado por sombreamento** (quantos dos 8 pontos de cada caractere acendem depende
  da luz) e luz de borda: as figuras ficam com volume e continuam pontilhadas.
- **Trava de troca** de forma, com a pose nova entrando na fila.
- **O fundo não pode competir com a figura:** máscara de proximidade que apaga a nebulosa e
  as estrelas em volta do contorno e escurece os vãos internos das figuras.
- Novos diagnósticos: `--verificar-fundo` (mede contraste, estrelas coladas no contorno e
  nos vãos), `--verificar-render` e `--exportar-png` (imagem para conferir a estética).

**Resultado:** interferência do fundo em **0,00%** das células da figura.

## Etapa 3 — Reconhecimento probabilístico e trava de 4,3 s

**Commit:** `Reconhecimento de gestos probabilístico e trava de 4,3 s`

**Pedido:** diminuir a trava para 4,3 s e melhorar a identificação dos gestos pela câmera.

**O que foi feito:**

- Classificador reescrito: cada detalhe da mão (dedo esticado, polegar para fora, pinça,
  abertura do Spock, direção do joinha) vira uma **probabilidade**, e cada pose uma
  **pontuação**.
- **Recusa de ambiguidade:** sem folga clara sobre a segunda pose, nada é disparado e o
  painel mostra "parece V? firme a pose".
- **Fusão temporal com histerese:** quadros tremidos são resolvidos pelos vizinhos.
- **Qualidade da mão** (longe / cortada na borda) com aviso em vez de palpite.
- **Tempo de confirmação adaptativo** (0,3 s com a pose clara, 0,6 s quando duvidosa).
- `--testar-camera --simular`: teste de integração completo sem câmera.

## Etapa 4 — Robustez a ruído e calibração com a mão real

**Commit:** `Reconhecimento robusto a ruído de profundidade e calibração pessoal`

**Pedido:** continuar testando e aprimorando o reconhecimento da mão na câmera até um nível
de excelência. Esta etapa foi feita **com a mão real do usuário**, em várias rodadas de
coleta, com pedidos de mais tempo para ler e fazer cada pose.

**Descoberta principal:** a profundidade estimada por webcam é muito ruidosa. Ângulos entre
falanges curtas oscilam tanto que, num teste "realista", a pose certa ficava estável só
**58%** do tempo, com **22,6%** de disparos errados por quadro.

**O que foi feito:**

- **Medidas de base longa:** cada dedo passou a ser medido pelo alcance (base→ponta ÷
  comprimento), pela distância ponta→palma e pela curvatura base→meio→ponta; o polegar,
  pela posição lateral em relação ao indicador. Resultado no modo realista: pose certa
  **58% → 100%**, disparo errado por quadro **22,6% → 2,4%**.
- **Gerador de mãos sintéticas realistas:** esquerda/direita, costas da mão, tamanhos,
  ruído de profundidade e "estilos" de mão; `--autoteste` com metas por modo.
- **Coleta guiada (`calibrar.bat`):** 6 s para preparar (espaço adianta), painel ao vivo
  de cada dedo em verde/vermelho, gravação só com a pose certa, repetição automática, `R`
  descarta a tentativa, e nas rodadas seguintes só as poses pendentes. Grava só números.
- **Calibração pessoal:** confere cada pose contra a definição (uma coleta com poses
  trocadas chegou a distorcer o limiar da pinça; daí a conferência), aprende os limiares
  com encolhimento em direção ao padrão, valida em metade separada dos quadros e só salva
  o perfil se houver ganho real.
- **Pinça ajustada a mãos reais:** as pontas marcadas pelo MediaPipe ficam a ~0,4 palmo
  mesmo com as polpas encostadas; o limiar passou de 0,30 para 0,45 palmo.
- **Átomo com duas mãos em L:** o Spock exige separar médio e anelar, o que muita gente
  (incluindo o usuário) não consegue; a "moldura" com as duas mãos virou a alternativa.
- `--avaliar`: mede o reconhecimento numa coleta ou pasta de coletas.

**Resultado com a mão real:** 16 de 17 poses conferidas, **97,2% de acerto na validação e
0% de disparo errado**. Os erros restantes são estados de transição reais da mão (por
exemplo, a pistola começando como L).

## Etapa 5 — Documentação e publicação

Revisão geral do projeto, correção de um caractere perdido num comentário do `maos.py` e
da lista de comandos no cabeçalho do `Program.cs`, e a documentação: `README.md`,
`docs/ARQUITETURA.md`, este histórico e a imagem `docs/formas.png`. Publicado em
<https://github.com/AlanSouzaDev7/Projet-Particula>.

## Etapa 6 — Trocas mais rápidas

**Pedido:** diminuir o tempo para formar a próxima forma para 2,4 segundos. A trava entre
uma troca e outra (`ShapeLockSeconds`) passou de 4,3 s para **2,4 s**.

## Números finais

| Medida | Valor |
|---|---|
| Partículas | 9.600 |
| Formas | 18 |
| Poses de uma mão / gestos de duas mãos | 17 / 6 |
| Renderização | ~1,6 ms por quadro, 40 quadros/s |
| Economia de banda (saída diferencial) | 42% |
| Reconhecedor de traços (sintético) | 98,8% |
| Classificador, mãos sintéticas normal / difícil / realista | 100% / 99,7% / 100% |
| Integração C# + Python simulada | 100% (17 poses) |
| Mão real do usuário, com perfil | 97,2% de acerto, 0% de disparo errado |
| Interferência do fundo na figura | 0,00% |
