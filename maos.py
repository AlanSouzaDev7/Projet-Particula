"""Rastreador de mãos do Partículas 3D.

Lê a webcam, encontra até duas mãos com o MediaPipe Hand Landmarker e classifica a pose
de cada uma. O Particulas.exe inicia este script e lê o stdout, uma linha por quadro:

    F <n> <duo>|<pose> <lado> <confiança> <provável> <qualidade> <cx> <cy> <tam> <pinçaX> <pinçaY>
               <pontaX> <pontaY> <x0> <y0> ... <x20> <y20>|...

Coordenadas de imagem em 0..1, já espelhadas (como um espelho). <duo> é o gesto feito com
as duas mãos juntas, ou "-". <provável> é a pose mais parecida mesmo quando <pose> é
"outra" (indefinida). <qualidade> é ok, longe ou borda. Mensagens de estado saem como
"OK ..." ou "ERR ...". Quando o Particulas.exe fecha, a escrita no stdout falha e este
script termina junto.

Como a pose é decidida:
  * pelos ângulos das juntas em 3D (pontos "do mundo" do MediaPipe, em metros, suavizados),
    o que não depende da distância até a câmera nem de a mão estar inclinada;
  * cada detalhe (dedo esticado, polegar para fora, pinça...) vira uma probabilidade, e cada
    pose uma pontuação; se a melhor não tiver folga sobre a segunda, a pose fica indefinida
    em vez de disparar a forma errada;
  * fusão temporal: as pontuações se acumulam ao longo dos quadros (um quadro ambíguo é`n    resolvido pelos vizinhos), com histerese para trocar de pose e na pinça.

Uso isolado:
  python maos.py --modelo hand_landmarker.task --debug     janela com a câmera e as poses
  python maos.py --modelo hand_landmarker.task --teste 5   roda 5 s e mostra estatísticas
  python maos.py --modelo hand_landmarker.task --coletar   coleta guiada + calibração pessoal
  python maos.py --avaliar coleta.jsonl                    mede o reconhecimento numa coleta
  python maos.py --calibrar coleta.jsonl                   (re)calibra o perfil pessoal
  python maos.py --autoteste                               testa o classificador com mãos sintéticas
  python maos.py --simular                                 sem câmera: mãos sintéticas no protocolo
"""
import argparse
import math
import os
import random
import sys
import threading
import time
from collections import Counter, deque


def out(line):
    try:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
    except (BrokenPipeError, OSError):
        os._exit(0)


# ---- geometria ----------------------------------------------------------------------
WRIST = 0
FINGERS = {
    "indicador": (5, 6, 7, 8),
    "medio": (9, 10, 11, 12),
    "anelar": (13, 14, 15, 16),
    "mindinho": (17, 18, 19, 20),
}


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def norm(v):
    return math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])


def dist(a, b):
    return norm(sub(a, b))


def angle_between(u, v):
    nu, nv = norm(u) or 1e-9, norm(v) or 1e-9
    c = (u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / (nu * nv)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def joint_bend(a, b, c):
    """Quanto a junta b está dobrada (0 = reta) entre os segmentos a-b e b-c, em graus."""
    return angle_between(sub(b, a), sub(c, b))


def finger_bend(w, f):
    """Curvatura do dedo com vetores LONGOS (base→meio e meio→ponta), em graus. Ângulos entre
    segmentos curtos (2–4 cm) explodem com o ruído de profundidade do MediaPipe."""
    mcp, pip, dip, tip = FINGERS[f]
    return (angle_between(sub(w[pip], w[mcp]), sub(w[tip], w[pip]))
            + 0.5 * angle_between(sub(w[mcp], w[WRIST]), sub(w[pip], w[mcp])))



def soft(value, center, width):
    """Transição suave (logística): ~0 bem abaixo de center, 0,5 em center, ~1 bem acima."""
    x = (value - center) / width
    if x < -30:
        return 0.0
    if x > 30:
        return 1.0
    return 1.0 / (1.0 + math.exp(-x))


def unit(v):
    n = norm(v) or 1e-9
    return (v[0] / n, v[1] / n, v[2] / n)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


class HandState:
    """Memória por mão: pontos suavizados, fusão temporal das pontuações e histerese da pinça."""

    def __init__(self):
        self.stable, self.stable_conf, self.alt = None, 0.0, "outra"
        self.acc = {}
        self.pinching = False
        self.missing = 0
        self.smooth = None

    def smooth_world(self, w, alpha=0.55):
        """Média móvel exponencial dos 21 pontos 3D: tira o tremido sem atrasar quase nada."""
        if self.smooth is None:
            self.smooth = list(w)
        else:
            self.smooth = [(s[0] + alpha * (p[0] - s[0]), s[1] + alpha * (p[1] - s[1]), s[2] + alpha * (p[2] - s[2]))
                           for s, p in zip(self.smooth, w)]
        return self.smooth

    def fuse(self, scores, decay=0.7):
        """Fusão temporal: acumula as pontuações de todas as poses com decaimento exponencial
        (~3 quadros de memória) e decide pelo acumulado. Um quadro ambíguo é resolvido pelos
        vizinhos. Para trocar de pose a nova precisa de folga clara; para manter, basta
        continuar razoavelmente apoiada (histerese). scores vazio = mão não confiável."""
        acc = self.acc
        for p in set(acc) | set(scores):
            acc[p] = decay * acc.get(p, 0.0) + (1 - decay) * scores.get(p, 0.0)
        pose, conf, alt = decide(acc, min_score=0.15, min_share=0.45, min_ratio=1.4)
        if pose == "outra" and self.stable not in (None, "outra"):
            total = sum(acc.values()) or 1e-9
            if acc.get(self.stable, 0.0) / total >= 0.3 and alt == self.stable:
                pose, conf = self.stable, conf      # continua na pose atual
        self.stable, self.stable_conf, self.alt = pose, conf, alt
        return pose, conf


# Estado esperado de cada dedo por pose: (polegar, indicador, médio, anelar, mindinho)
# 1 = esticado, 0 = dobrado, None = tanto faz. Detalhes extras (abertura do polegar,
# direção do joinha, pinça, Spock) entram como fatores em pose_scores().
POSE_SPECS = {
    "aberta": (1, 1, 1, 1, 1), "quatro": (0, 1, 1, 1, 1), "spock": (None, 1, 1, 1, 1),
    "punho": (None, 0, 0, 0, 0), "joinha": (1, 0, 0, 0, 0), "negativo": (1, 0, 0, 0, 0),
    "indicador": (None, 1, 0, 0, 0), "L": (1, 1, 0, 0, 0),
    "v": (None, 1, 1, 0, 0), "pistola": (1, 1, 1, 0, 0), "tres": (None, 1, 1, 1, 0),
    "rock": (0, 1, 0, 0, 1), "teamo": (1, 1, 0, 0, 1),
    "hangloose": (1, 0, 0, 0, 1), "mindinho": (0, 0, 0, 0, 1),
    "ok": (None, None, 1, 1, 1), "pinca": (None, None, 0, 0, 0),
}


def raw_measures(w, img):
    """Medidas geométricas da mão (sem decidir nada). Usadas pela classificação e pela
    calibração pessoal, que aprende os limiares a partir delas."""
    palm = dist(w[WRIST], w[9]) or 1e-6
    palm_w = dist(w[5], w[17]) or 1e-6
    m = {}

    # Dedos: três medidas de base longa (robustas ao ruído de profundidade):
    #   alcance     = distância base→ponta / comprimento do dedo (1 = reto)
    #   ponta-palma = distância da ponta ao centro da palma, em palmos
    #   curvatura   = ângulo entre base→meio e meio→ponta
    center = tuple(sum(w[j][k] for j in (0, 5, 9, 13, 17)) / 5 for k in range(3))
    for name in FINGERS:
        mcp, pip, dip, tip = FINGERS[name]
        length = dist(w[mcp], w[pip]) + dist(w[pip], w[dip]) + dist(w[dip], w[tip]) or 1e-6
        m["reach_" + name] = dist(w[mcp], w[tip]) / length
        m["tip_palm_" + name] = dist(w[tip], center) / palm
        m["bend_" + name] = finger_bend(w, name)

    # Polegar: posição lateral (fora da palma, além do indicador) e distância ao centro
    m["thumb_lateral"] = dot(sub(w[4], w[5]), unit(sub(w[5], w[17]))) / palm_w
    m["thumb_out"] = dist(w[4], w[9]) / palm
    m["thumb_spread"] = angle_between(sub(w[4], w[2]), sub(w[8], w[5]))
    m["pinch"] = dist(w[4], w[8]) / palm

    # Spock: abertura médio-anelar no plano da palma (plano por vetores longos; direção de
    # cada dedo pela média de três articulações) e razão entre as distâncias das pontas
    across = sub(w[5], w[17])
    along = sub(tuple(sum(w[j][k] for j in (5, 9, 13, 17)) / 4 for k in range(3)), w[WRIST])
    pn = unit((across[1] * along[2] - across[2] * along[1], across[2] * along[0] - across[0] * along[2],
               across[0] * along[1] - across[1] * along[0]))
    dirs = []
    for name in FINGERS:
        mcp, pip, dip, tip = FINGERS[name]
        mid = tuple((w[pip][k] + w[dip][k] + w[tip][k]) / 3 for k in range(3))
        r = sub(mid, w[mcp])
        k = dot(r, pn)
        dirs.append((r[0] - k * pn[0], r[1] - k * pn[1], r[2] - k * pn[2]))
    m["gap_im"] = angle_between(dirs[0], dirs[1])
    m["gap_mr"] = angle_between(dirs[1], dirs[2])
    m["gap_rp"] = angle_between(dirs[2], dirs[3])
    m["spock_ratio"] = dist(w[12], w[16]) / ((dist(w[8], w[12]) + dist(w[16], w[20])) / 2 or 1e-6)

    # Joinha / negativo: direção do polegar na imagem (0° = para cima) e altura da ponta
    pl = math.hypot(img[0][0] - img[9][0], img[0][1] - img[9][1]) or 1e-6
    dx, dy = img[4][0] - img[2][0], img[4][1] - img[2][1]
    m["thumb_from_up"] = math.degrees(math.atan2(abs(dx), -dy))
    m["thumb_rise"] = (img[5][1] - img[4][1]) / pl
    return m


# Limiares do classificador. Um perfil pessoal (calibração) pode substituir os valores.
DEFAULT_PARAMS = {
    "reach": 0.71, "bend": 100.0,
    "tip_palm_indicador": 0.83, "tip_palm_medio": 0.87, "tip_palm_anelar": 0.80, "tip_palm_mindinho": 0.69,
    "thumb_lateral": 0.25, "thumb_out": 0.83, "thumb_spread": 42.0,
    # Pinça: numa mão real as POLPAS se encostam e as pontas (onde o MediaPipe marca) ficam a
    # ~3–4 cm, ~0,4 palmo; o indicador curvado distingue do punho
    "pinch": 0.45, "pinch_release": 0.62, "pinch_index_bend": 115.0,
    "spock_gap": 14.0, "spock_ratio": 1.35,
    "thumb_up_angle": 48.0,
}
PARAMS = dict(DEFAULT_PARAMS)


def load_profile(path):
    """Carrega um perfil pessoal (JSON) por cima dos limiares padrão. Devolve True se carregou."""
    import json
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return False
    PARAMS.clear()
    PARAMS.update(DEFAULT_PARAMS)
    PARAMS.update({k: float(v) for k, v in data.get("params", {}).items() if k in DEFAULT_PARAMS})
    return True


def hand_features(w, img, state):
    """Probabilidades (0..1) de cada detalhe da mão, a partir dos pontos 3D (metros) e de imagem."""
    m, P = raw_measures(w, img), PARAMS
    f = {}
    # Cada dedo: média das três medidas longas — se uma falhar num quadro, as outras seguram
    f["ext"] = [(soft(m["reach_" + n], P["reach"], 0.03) + soft(m["tip_palm_" + n], P["tip_palm_" + n], 0.05)
                 + soft(-m["bend_" + n], -P["bend"], 8)) / 3 for n in FINGERS]
    # Polegar (a "dobra" do polegar se mostrou pouco confiável e não entra)
    f["thumb"] = 0.5 * soft(m["thumb_lateral"], P["thumb_lateral"], 0.08) + 0.5 * soft(m["thumb_out"], P["thumb_out"], 0.05)
    f["spread"] = soft(m["thumb_spread"], P["thumb_spread"], 6)

    # Pinça (com histerese): pontas do polegar e do indicador juntas, indicador só meio dobrado
    # (no punho as pontas também se encostam, mas o indicador está todo enrolado)
    thr = P["pinch_release"] if state.pinching else P["pinch"]
    f["pinch"] = soft(-m["pinch"], -thr, 0.04) * soft(-m["bend_indicador"], -P["pinch_index_bend"], 8)
    state.pinching = f["pinch"] > 0.5

    by_angle = (soft(m["gap_mr"], P["spock_gap"], 2.5)
                * soft(m["gap_mr"] - 1.8 * max(m["gap_im"], m["gap_rp"]), 0, 3))
    f["spock"] = 0.5 * by_angle + 0.5 * soft(m["spock_ratio"], P["spock_ratio"], 0.1)

    # Tolerante: na prática o joinha sai bem inclinado (até ~45-50°)
    a = P["thumb_up_angle"]
    f["up"] = soft(-m["thumb_from_up"], -a, 8) * soft(m["thumb_rise"], 0.15, 0.08)
    f["down"] = soft(m["thumb_from_up"], 180 - a, 8) * soft(-m["thumb_rise"], 0.15, 0.08)
    return f


def pose_scores(f):
    probs = [f["thumb"]] + f["ext"]
    scores = {}
    for pose, spec in POSE_SPECS.items():
        s = 1.0
        for want, p in zip(spec, probs):
            if want is not None:
                s *= p if want else 1 - p
        if pose in ("ok", "pinca"):
            s *= f["pinch"]
        else:
            s *= 1 - f["pinch"]
        if pose == "spock":
            s *= f["spock"]
        elif pose in ("aberta", "quatro"):
            s *= 1 - f["spock"]
        elif pose == "punho":
            s *= 1 - f["thumb"] * max(f["up"], f["down"])
        elif pose == "joinha":
            s *= f["up"]
        elif pose == "negativo":
            s *= f["down"]
        elif pose in ("indicador", "v"):
            s *= 1 - f["thumb"] * f["spread"]
        elif pose in ("L", "pistola"):
            s *= f["spread"]
        scores[pose] = s
    return scores


def decide(scores, min_score=0.2, min_share=0.5, min_ratio=1.5):
    """Escolhe a pose a partir das pontuações; ambíguo vira 'outra' (melhor não disparar nada
    do que disparar a forma errada). Devolve (pose, confiança, mais provável)."""
    ranked = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
    if not ranked:
        return "outra", 0.0, "outra"
    best, b = ranked[0]
    second = ranked[1][1] if len(ranked) > 1 else 0.0
    total = sum(scores.values()) or 1e-9
    share = b / total
    conf = math.sqrt(b * share)          # forte E sem rival à altura
    if b < min_score or share < min_share or b < min_ratio * second:
        return "outra", conf, best
    return best, conf, best


def classify(w, img, state):
    """w: 21 pontos 3D em metros; img: 21 pontos (x, y) de imagem com escala isotrópica
    (y cresce para baixo). Devolve (pose, confiança 0..1, mais provável, pontuações)."""
    scores = pose_scores(hand_features(w, img, state))
    pose, conf, alt = decide(scores)
    return pose, conf, alt, scores


def hand_quality(hand, size):
    """'ok', 'longe' (mão pequena demais na imagem) ou 'borda' (parte da mão fora da imagem)."""
    if size < 0.05:
        return "longe"
    for p in hand:
        if p.x < 0.01 or p.x > 0.99 or p.y < 0.01 or p.y > 0.99:
            return "borda"
    return "ok"


DUO_FROM_PAIR = {
    ("indicador", "indicador"): "dois-indicadores",
    ("v", "v"): "dois-v",
    ("punho", "punho"): "dois-punhos",
    ("aberta", "aberta"): "duas-abertas",
    ("L", "L"): "dois-L",                       # "moldura": alternativa fácil ao Spock para o Átomo
}


def detect_duo(hands):
    """hands: lista de (pose_estável, img). Gesto de duas mãos ou '-'."""
    if len(hands) < 2:
        return "-"
    (pa, ia), (pb, ib) = hands[0], hands[1]
    pl = (math.hypot(ia[0][0] - ia[9][0], ia[0][1] - ia[9][1])
          + math.hypot(ib[0][0] - ib[9][0], ib[0][1] - ib[9][1])) / 2 or 1e-6
    # Coração com as mãos: pontas dos indicadores juntas em cima, polegares juntos embaixo
    idx_close = math.hypot(ia[8][0] - ib[8][0], ia[8][1] - ib[8][1]) < 0.5 * pl
    thb_close = math.hypot(ia[4][0] - ib[4][0], ia[4][1] - ib[4][1]) < 0.5 * pl
    above = (ia[8][1] + ib[8][1]) / 2 < (ia[4][1] + ib[4][1]) / 2 - 0.4 * pl
    if idx_close and thb_close and above:
        return "coracao"
    return DUO_FROM_PAIR.get((pa, pb), "-")


class Tracker:
    """Transforma as mãos de um quadro (pontos de imagem + pontos 3D) em poses estáveis e
    escreve a linha do protocolo. Usado igualmente pela câmera e pela simulação."""

    def __init__(self):
        self.states = {}
        self.duo_votes = deque(maxlen=5)
        self.duo = "-"

    def process(self, hands, aspect):
        """hands: lista de (lado, pontos de imagem com .x/.y, pontos 3D com .x/.y/.z);
        aspect = altura/largura da imagem. Devolve [(pose, conf, provável, qualidade, pontos)]."""
        chunks, duo_input, seen, result = [], [], set(), []
        for k, (side, hand, world) in enumerate(hands):
            key = side if side not in seen else side + str(k)
            seen.add(key)
            state = self.states.setdefault(key, HandState())
            state.missing = 0

            wpts = state.smooth_world([(p.x, p.y, p.z) for p in world])
            img = [(p.x, p.y * aspect) for p in hand]         # escala isotrópica
            size = math.hypot(hand[0].x - hand[9].x, (hand[0].y - hand[9].y) * aspect)
            quality = hand_quality(hand, size)
            _, _, _, scores = classify(wpts, img, state)
            if quality != "ok":
                scores = {}                                    # mão longe/cortada: não chuta
            pose, conf = state.fuse(scores)
            alt = state.alt
            duo_input.append((pose, img))
            result.append((pose, conf, alt, quality, hand))

            ids = (0, 5, 9, 13, 17)
            cx = sum(hand[j].x for j in ids) / 5
            cy = sum(hand[j].y for j in ids) / 5
            px, py = (hand[4].x + hand[8].x) / 2, (hand[4].y + hand[8].y) / 2
            coords = " ".join("%.3f %.3f" % (p.x, p.y) for p in hand)
            chunks.append("%s %s %.2f %s %s %.3f %.3f %.3f %.3f %.3f %.3f %.3f %s"
                          % (pose, side, conf, alt, quality, cx, cy, size, px, py, hand[8].x, hand[8].y, coords))

        # Esquece mãos que sumiram há alguns quadros
        for key in list(self.states):
            if key not in seen:
                self.states[key].missing += 1
                if self.states[key].missing > 8:
                    del self.states[key]

        self.duo_votes.append(detect_duo(duo_input))
        duo, cnt = Counter(self.duo_votes).most_common(1)[0]
        self.duo = duo if cnt >= 3 else "-"
        out("F %d %s|%s" % (len(chunks), self.duo, "|".join(chunks)))
        return result


class _Pt:
    __slots__ = ("x", "y", "z")

    def __init__(self, x, y, z=0.0):
        self.x, self.y, self.z = x, y, z


def simulate(seconds_per_pose=1.5, fps=30):
    """Sem câmera: percorre todas as poses com mãos sintéticas (ruído em cada quadro),
    passando pelo mesmo caminho do código real. Serve para testar a integração com o app."""
    rng = random.Random(7)
    tracker = Tracker()
    out("OK simulacao")
    for pose in POSES:
        out("S " + pose)                                       # gabarito para quem estiver testando
        for _ in range(int(seconds_per_pose * fps)):
            world, img = synth_hand(pose, rng)
            hand = [_Pt(0.5 + (x - 0.5) * 0.65, 0.55 + (y - 0.5) * 0.65) for x, y in img]   # mão inteira na imagem
            wpts = [_Pt(*p) for p in world]
            tracker.process([("R", hand, wpts)], 1.0)
            time.sleep(1.0 / fps)
    return 0


# ---- coleta guiada, avaliação e calibração pessoal ----------------------------------
# A coleta grava só NÚMEROS (os 21 pontos 3D e de imagem de cada quadro), nunca imagens.
POSE_HELP = {   # textos sem acento: a fonte do OpenCV não desenha acentos
    "aberta": "mao aberta, 5 dedos esticados", "quatro": "4 dedos, polegar dobrado na palma",
    "spock": "Spock: medio e anelar separados", "punho": "punho fechado",
    "joinha": "joinha (polegar para cima)", "negativo": "negativo (polegar para baixo)",
    "indicador": "so o indicador levantado", "L": "L: polegar e indicador abertos",
    "v": "V: indicador e medio", "pistola": "pistola: polegar, indicador e medio",
    "tres": "3 dedos: indicador, medio e anelar", "rock": "rock: indicador e mindinho",
    "teamo": "eu te amo: polegar, indicador e mindinho", "hangloose": "hang loose: polegar e mindinho",
    "mindinho": "so o mindinho levantado", "ok": "OK: polegar e indicador em circulo",
    "pinca": "pinca: polegar e indicador juntos, resto dobrado",
}


PANEL_NAMES = ("POLEGAR", "INDICADOR", "MEDIO", "ANELAR", "MINDINHO")


def draw_finger_panel(cv2, frame, pose, states, w, h, extra_ok=None):
    """Painel com os 5 dedos: o que a pose pede (LEVANTAR / DOBRAR / tanto faz) e, ao vivo,
    se a câmera está vendo cada dedo certo (verde) ou errado (vermelho)."""
    spec = POSE_SPECS[pose]
    top, cell = h - 40 - 62, (w - 20) // 5
    cv2.rectangle(frame, (0, top - 6), (w, h - 40), (25, 25, 35), -1)
    for i, (name, want) in enumerate(zip(PANEL_NAMES, spec)):
        x0 = 10 + i * cell
        if want is None:
            label, color = "tanto faz", (130, 130, 130)
        else:
            label = "LEVANTAR" if want else "DOBRAR"
            st = None if states is None else states[i]
            color = (130, 130, 130) if st is None else ((90, 210, 90) if st else (70, 70, 230))
        cv2.rectangle(frame, (x0, top), (x0 + cell - 8, top + 52), color, 2 if states is None else -1)
        text_color = (255, 255, 255) if states is not None and want is not None else (200, 200, 200)
        cv2.putText(frame, name, (x0 + 6, top + 20), cv2.FONT_HERSHEY_SIMPLEX, 0.45, text_color, 1)
        cv2.putText(frame, label, (x0 + 6, top + 42), cv2.FONT_HERSHEY_SIMPLEX, 0.5, text_color, 2)
    # Dica do detalhe extra, colorida ao vivo: verde = feito, vermelho = ainda não
    hint_color = (120, 230, 255) if extra_ok is None else ((90, 230, 90) if extra_ok else (80, 80, 255))
    if pose in ("ok", "pinca"):
        cv2.putText(frame, "encoste a ponta do polegar na ponta do indicador", (10, top - 14),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, hint_color, 2)
    elif pose == "spock":
        cv2.putText(frame, "V entre medio e anelar: indicador+medio | anelar+mindinho", (10, top - 14),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, hint_color, 2)


def collect(args):
    """Janela com a câmera: pede cada pose (6 s para ler e preparar, 3 s gravando) e salva os pontos."""
    import json
    import cv2
    import mediapipe as mp
    from mediapipe.tasks.python import BaseOptions, vision

    path = args.coletar if args.coletar != "auto" else os.path.join(
        os.path.dirname(os.path.abspath(args.modelo)), time.strftime("coleta-%Y%m%d-%H%M%S.jsonl"))
    cam = Camera(args.camera)
    if not cam.ok:
        print("Não consegui abrir a câmera (feche o Particulas.exe se estiver aberto).")
        return 3
    landmarker = vision.HandLandmarker.create_from_options(vision.HandLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=args.modelo), running_mode=vision.RunningMode.VIDEO,
        num_hands=1, min_hand_detection_confidence=0.6, min_hand_presence_confidence=0.5,
        min_tracking_confidence=0.5))
    title = "Coleta de gestos - Particulas (ESPACO grava ja, R repete a pose, Esc cancela)"
    folder = os.path.dirname(os.path.abspath(path))
    # Só as poses que ainda não têm uma gravação conferida (ou todas, com --tudo)
    previous = collection_files(folder)
    if getattr(args, "tudo", False) or not previous:
        poses = list(POSES)
    else:
        chosen, _, _ = best_frames(previous)
        poses = [p for p in POSES if p not in chosen]
        if not poses:
            print("Todas as poses já têm gravação conferida. Use --tudo para refazer todas.")
            cam.close()
            return calibrate(previous, os.path.join(folder, "perfil.json"))
        print("Poses pendentes (as outras já conferiram numa coleta anterior): %s" % ", ".join(poses))
    t0, seq, last_ts, saved = time.monotonic(), 0, -1, 0
    PREP, REC = 6.0, 3.0      # segundos para ler/preparar e para gravar cada pose
    attempts, notice = Counter(), ""
    with open(path, "w", encoding="utf-8") as fh:
        k = 0
        while k < len(poses):
            # Os quadros da pose ficam em memória e só vão para o arquivo quando ela termina:
            # se a pessoa apertar R, a tentativa errada é descartada
            pose, phase_start, buffer, repeat = poses[k], time.monotonic(), [], False
            attempts[pose] += 1
            live_state, matched, checked, all_ok = HandState(), 0, 0, False
            # A gravação só começa quando a pose está certa (tudo verde por 0,4 s), para não
            # gravar a mão ainda chegando na pose; se não acertar em 6 s, grava assim mesmo
            rec_start, green_since, WAIT_MAX = None, None, 6.0
            while True:
                frame, seq2 = cam.latest(seq)
                if frame is None or seq2 == seq:
                    continue
                seq = seq2
                frame = cv2.flip(frame, 1)
                h, w = frame.shape[:2]
                ts = max(int((time.monotonic() - t0) * 1000), last_ts + 1)
                last_ts = ts
                res = landmarker.detect_for_video(
                    mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)), ts)
                now = time.monotonic()
                elapsed = now - phase_start
                recording = rec_start is not None and now - rec_start < REC
                has_hand = bool(res.hand_landmarks)
                states, extra_ok = None, None
                if has_hand:
                    hand = res.hand_landmarks[0]
                    world = res.hand_world_landmarks[0]
                    for p in hand:
                        cv2.circle(frame, (int(p.x * w), int(p.y * h)), 3, (0, 255, 255), -1)
                    # Conferência ao vivo de cada dedo contra a definição da pose
                    wpts = [(p.x, p.y, p.z) for p in world]
                    feats = hand_features(wpts, [(p.x, p.y * h / w) for p in hand], live_state)
                    probs = [feats["thumb"]] + feats["ext"]
                    states = [None if want is None else ((pr > 0.5) == bool(want))
                              for want, pr in zip(POSE_SPECS[pose], probs)]
                    # Detalhe extra da pose: pinça encostada (OK/pinça) ou V entre médio e anelar (Spock)
                    extra_ok = (feats["pinch"] > 0.5 if pose in ("ok", "pinca")
                                else feats["spock"] > 0.5 if pose == "spock" else None)
                    all_ok = all(s is not False for s in states) and extra_ok is not False
                else:
                    all_ok, extra_ok = False, None
                if elapsed >= PREP and rec_start is None:
                    green_since = (green_since or now) if all_ok else None
                    if (green_since is not None and now - green_since >= 0.4) or elapsed >= PREP + WAIT_MAX:
                        rec_start = now
                        recording = True
                if has_hand:
                    if recording:
                        checked += 1
                        matched += all_ok
                        side = res.handedness[0][0].category_name[:1] if res.handedness and res.handedness[0] else "?"
                        buffer.append(json.dumps({"pose": pose, "side": side, "aspect": h / w,
                                                  "world": [[round(p.x, 5), round(p.y, 5), round(p.z, 5)] for p in world],
                                                  "img": [[round(p.x, 4), round(p.y, 4)] for p in hand]}))
                # Faixa de instruções: número da pose, nome e descrição em letras grandes
                cv2.rectangle(frame, (0, 0), (w, 96), (25, 25, 35), -1)
                cv2.putText(frame, "Pose %d de %d:  %s" % (k + 1, len(poses), pose.upper()), (14, 34),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.95, (255, 255, 255), 2)
                cv2.putText(frame, POSE_HELP[pose], (14, 72), cv2.FONT_HERSHEY_SIMPLEX, 0.85, (120, 230, 255), 2)
                draw_finger_panel(cv2, frame, pose, states, w, h, extra_ok)
                if elapsed < PREP:
                    left = int(math.ceil(PREP - elapsed))
                    cv2.putText(frame, str(left), (w // 2 - 30, h // 2), cv2.FONT_HERSHEY_SIMPLEX, 3.0, (120, 200, 255), 6)
                    if notice:
                        cv2.putText(frame, notice, (14, 130), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (80, 160, 255), 2)
                    if has_hand and all_ok:
                        msg, color = "POSE CERTA! segure assim... (ESPACO grava ja)", (120, 255, 120)
                    else:
                        msg, color = "acerte os dedos em vermelho...", (120, 200, 255)
                elif rec_start is None:
                    wait_left = int(math.ceil(PREP + WAIT_MAX - elapsed))
                    msg, color = "aguardando a pose certa para gravar... (%d s)" % wait_left, (80, 200, 255)
                elif recording:
                    msg, color = "GRAVANDO - segure a pose (%d quadros)%s" % (
                        len(buffer), "" if has_hand else " - mostre a mao!"), (60, 60, 255)
                else:
                    msg, color = "ok!", (120, 255, 120)
                cv2.rectangle(frame, (0, h - 40), (w, h), (25, 25, 35), -1)
                cv2.putText(frame, msg, (14, h - 13), cv2.FONT_HERSHEY_SIMPLEX, 0.65, color, 2)
                cv2.imshow(title, frame)
                key = cv2.waitKey(1) & 0xFF
                if key == 27:
                    cam.close()
                    cv2.destroyAllWindows()
                    print("Coleta cancelada. %d quadros salvos em %s" % (saved, path))
                    return 1
                if key in (ord("r"), ord("R")):
                    repeat = True
                    break
                if key == 32 and elapsed < PREP:              # ESPAÇO: já está pronto, grava assim que estiver certa
                    phase_start = time.monotonic() - PREP
                if rec_start is not None and now - rec_start >= REC + 0.5:
                    break
            notice = ""
            # Gravação em que a pose não conferiu na maior parte do tempo: repete uma vez
            if not repeat and checked and matched < 0.5 * checked and attempts[pose] < 2:
                repeat, notice = True, "Nao conferiu - vamos repetir esta pose"
            if not repeat:
                for line in buffer:
                    fh.write(line + "\n")
                fh.flush()
                saved += len(buffer)
                k += 1
    cam.close()
    cv2.destroyAllWindows()
    print("Coleta concluída: %d quadros em %s" % (saved, path))
    folder = os.path.dirname(os.path.abspath(path))
    return calibrate(collection_files(folder), os.path.join(folder, "perfil.json"))


def load_frames(path):
    import json
    frames = []
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
                d = json.loads(line)
                if d.get("pose") in POSES:
                    frames.append(d)
    return frames


def frame_points(d):
    aspect = d.get("aspect", 0.75)
    world = [tuple(p) for p in d["world"]]
    img = [(p[0], p[1] * aspect) for p in d["img"]]
    return world, img


# Quadros descartados no início e no fim de cada pose gravada (a mão ainda chegando ou já
# saindo da pose): janela FIXA de tempo, cega ao resultado — ~0,8 s e ~0,5 s a 15 quadros/s
TRIM_START, TRIM_END = 12, 8


def trimmed(seq):
    return seq[TRIM_START:len(seq) - TRIM_END] if len(seq) > TRIM_START + TRIM_END + 10 else seq


def replay(frames, trim=True):
    """Reproduz quadros gravados pelo caminho do app (suavização + classificação + fusão),
    pose por pose. Devolve métricas e confusões. trim=True ignora nas métricas as bordas
    de cada pose (a mão ainda chegando/saindo), mas os quadros passam pela fusão."""
    by_pose = {}
    for d in frames:
        by_pose.setdefault(d["pose"], []).append(d)
    raw_ok = stable_ok = counted = total = wrong = 0
    per_pose, confusion = {}, Counter()
    for pose, seq in by_pose.items():
        state, good, n = HandState(), 0, 0
        first, last = (TRIM_START, len(seq) - TRIM_END) if trim and len(seq) > TRIM_START + TRIM_END + 10 else (6, len(seq))
        for k, d in enumerate(seq):
            world, img = frame_points(d)
            world = state.smooth_world(world)
            raw, _, _, scores = classify(world, img, state)
            stable = state.fuse(scores)[0]
            total += 1
            raw_ok += raw == pose
            if first <= k < last:
                counted += 1
                n += 1
                good += stable == pose
                stable_ok += stable == pose
                if stable not in (pose, "outra"):
                    wrong += 1
                if stable != pose:
                    confusion[(pose, stable)] += 1
        per_pose[pose] = 100.0 * good / max(1, n)
    return {"raw": 100.0 * raw_ok / max(1, total), "stable": 100.0 * stable_ok / max(1, counted),
            "wrong": 100.0 * wrong / max(1, counted), "per_pose": per_pose, "confusion": confusion}


def truth_of(pose):
    """Estados verdadeiros de uma pose para a calibração: polegar, dedos, pinça, Spock."""
    fingers_ext, thumb_mode, _ = POSES[pose]
    thumb = None if thumb_mode == "pinch" else (0 if thumb_mode == "fold" else 1)
    fingers = {}
    for f in FINGERS:
        fingers[f] = None if (thumb_mode == "pinch" and f == "indicador") else (1 if LETTER[f] in fingers_ext else 0)
    return thumb, fingers


def fit_threshold(pos, neg, default):
    """Limiar entre duas classes de valores (pos = classe com valores MAIORES). Usa a folga
    entre o 10º percentil de pos e o 90º de neg; se as classes se sobrepõem, o ponto médio
    das medianas. O resultado é puxado 25% para o padrão (menos sensível a uma coleta ruim)."""
    if len(pos) < 30 or len(neg) < 30:
        return default, "poucos dados"
    pos, neg = sorted(pos), sorted(neg)
    q = lambda v, p: v[int(p * (len(v) - 1))]
    lo, hi = q(neg, 0.9), q(pos, 0.1)
    if hi > lo:
        thr, note = (lo + hi) / 2, "ok"
    else:
        thr, note = (q(neg, 0.5) + q(pos, 0.5)) / 2, "classes se sobrepõem"
    return default + 0.75 * (thr - default), note


def learn_params(frames):
    """Aprende os limiares pessoais a partir de quadros rotulados."""
    vals = {}
    add = lambda key, cls, v: vals.setdefault((key, cls), []).append(v)
    for d in frames:
        pose = d["pose"]
        world, img = frame_points(d)
        m = raw_measures(world, img)
        thumb, fingers = truth_of(pose)
        for f, t in fingers.items():
            if t is None:
                continue
            add("reach", t, m["reach_" + f])
            add("bend", 1 - t, m["bend_" + f])            # curvatura maior = dobrado
            add("tip_palm_" + f, t, m["tip_palm_" + f])
        if thumb is not None:
            add("thumb_lateral", thumb, m["thumb_lateral"])
            add("thumb_out", thumb, m["thumb_out"])
        # Pinça = distância MENOR. Do lado "não pinça" só entram poses com o indicador esticado:
        # no punho/mindinho a ponta do polegar também fica perto, mas a curvatura do
        # indicador já barra esses casos
        if pose in ("ok", "pinca"):
            add("pinch", 0, m["pinch"])
        elif fingers.get("indicador") == 1:
            add("pinch", 1, m["pinch"])
        if pose in ("spock", "aberta", "quatro"):
            add("spock_ratio", 1 if pose == "spock" else 0, m["spock_ratio"])
            add("spock_gap", 1 if pose == "spock" else 0, m["gap_mr"])
        if pose in ("L", "pistola"):
            add("thumb_spread", 1, m["thumb_spread"])
        if pose == "joinha":
            add("thumb_tilt", 1, m["thumb_from_up"])
        elif pose == "negativo":
            add("thumb_tilt", 1, 180 - m["thumb_from_up"])
    params, notes = dict(DEFAULT_PARAMS), {}
    for key in ("reach", "bend", "tip_palm_indicador", "tip_palm_medio", "tip_palm_anelar",
                "tip_palm_mindinho", "thumb_lateral", "thumb_out", "pinch", "spock_ratio", "spock_gap"):
        pos, neg = vals.get((key, 1), []), vals.get((key, 0), [])
        params[key], notes[key] = fit_threshold(pos, neg, DEFAULT_PARAMS[key])
    params["pinch_release"] = params["pinch"] * DEFAULT_PARAMS["pinch_release"] / DEFAULT_PARAMS["pinch"]
    # Abertura do polegar no L/pistola: não há uma classe "contrária" natural; o limiar só
    # desce (nunca sobe) o suficiente para aceitar ~90% do L/pistola desta pessoa
    spread = sorted(vals.get(("thumb_spread", 1), []))
    if len(spread) >= 30:
        params["thumb_spread"] = max(20.0, min(DEFAULT_PARAMS["thumb_spread"], spread[len(spread) // 10] - 4))
        notes["thumb_spread"] = "ok"
    # Inclinação do joinha/negativo: só alarga (até 70°) para aceitar ~90% desta pessoa
    tilt = sorted(vals.get(("thumb_tilt", 1), []))
    if len(tilt) >= 30:
        params["thumb_up_angle"] = min(70.0, max(DEFAULT_PARAMS["thumb_up_angle"], tilt[len(tilt) * 9 // 10] + 5))
        notes["thumb_up_angle"] = "ok"
    return params, notes


def report(label, r):
    worst = sorted(r["per_pose"].items(), key=lambda kv: kv[1])[:4]
    print("  %-22s quadro isolado %5.1f%% | estável certo %5.1f%% | disparo errado %4.1f%% | piores: %s"
          % (label, r["raw"], r["stable"], r["wrong"], ", ".join("%s %.0f%%" % kv for kv in worst)))


FINGER_NAMES = ("polegar", "indicador", "médio", "anelar", "mindinho")


def check_pose(pose, feats):
    """Confere se a pose gravada corresponde à definição (com os limiares padrão). Devolve a
    lista de problemas — vazia se confere. Só aponta contradições claras (um dedo no estado
    oposto, polegar e indicador que não se encostaram): diferenças sutis de jeito de mão são
    justamente o que a calibração aprende."""
    med = lambda vals: sorted(vals)[len(vals) // 2]
    spec = POSE_SPECS[pose]
    probs = [med([f["thumb"] for f in feats])] + [med([f["ext"][i] for f in feats]) for i in range(4)]
    problems = []
    for name, want, p in zip(FINGER_NAMES, spec, probs):
        if want is None:
            continue
        limit = 0.7 if name == "polegar" else 0.6          # o polegar é o sinal mais incerto
        if abs(p - want) > limit:
            problems.append("%s deveria estar %s" % (name, "esticado" if want else "dobrado"))
    if pose in ("ok", "pinca") and med([f["pinch"] for f in feats]) < 0.3:
        problems.append("polegar e indicador não se encostaram")
    if pose == "spock" and med([f["spock"] for f in feats]) < 0.2:
        problems.append("médio e anelar não ficaram separados (faça um V entre eles)")
    return problems


def check_poses(frames):
    """{pose: problemas} para cada pose da coleta (com os limiares padrão)."""
    saved = dict(PARAMS)
    PARAMS.clear(); PARAMS.update(DEFAULT_PARAMS)
    by_pose = {}
    for d in frames:
        by_pose.setdefault(d["pose"], []).append(d)
    result = {}
    for pose, seq in by_pose.items():
        feats = [hand_features(*frame_points(d), HandState()) for d in seq]
        result[pose] = check_pose(pose, feats)
    PARAMS.clear(); PARAMS.update(saved)
    return result


def collection_files(folder):
    """Coletas completas da pasta, da mais antiga para a mais recente."""
    names = [n for n in os.listdir(folder) if n.startswith("coleta-") and n.endswith(".jsonl") and "incompleta" not in n]
    return [os.path.join(folder, n) for n in sorted(names)]


def best_frames(paths):
    """Para cada pose, os quadros da coleta MAIS RECENTE em que ela conferiu com a definição.
    Permite refazer só as poses com problema sem repetir tudo."""
    chosen, origin, problems = {}, {}, {}
    for path in paths:
        frames = load_frames(path)
        checks = check_poses(frames)
        for pose, probs in checks.items():
            if probs:
                if pose not in chosen:
                    problems[pose] = probs
                continue
            chosen[pose] = [d for d in frames if d["pose"] == pose]
            origin[pose] = os.path.basename(path)
            problems.pop(pose, None)
    return chosen, origin, problems


def remove_profile(profile_path, why):
    if os.path.exists(profile_path):
        os.remove(profile_path)
        print("Perfil pessoal anterior removido (%s); usando os limiares padrão." % why)


def calibrate(paths, profile_path):
    """Calibra com metade dos quadros de cada pose e valida na outra metade (honesto), depois
    salva o perfil aprendido com todos os quadros. Só entram poses que conferem com a
    definição; as que não conferem são listadas para refazer."""
    import json
    if isinstance(paths, str):
        paths = [paths]
    chosen, origin, problems = best_frames(paths)
    if problems:
        print("Poses que NÃO conferiram (ficaram de fora da calibração — refaça só estas):")
        for pose in POSES:
            if pose in problems:
                print("  %-10s %s" % (pose, "; ".join(problems[pose])))
    ok_poses = [p for p in POSES if p in chosen]
    print("Poses que conferiram: %d de %d" % (len(ok_poses), len(POSES)))
    frames = [d for p in ok_poses for d in trimmed(chosen[p])]       # sem as bordas de cada pose
    if len(ok_poses) < 8 or len(frames) < 200:
        print("Poses certas insuficientes para calibrar com segurança.")
        remove_profile(profile_path, "coleta insuficiente")
        return 1
    train, test = [], []
    for p in ok_poses:
        # Tira as bordas (mão chegando/saindo) e divide o resto: 1ª metade aprende, 2ª valida
        seq = trimmed(chosen[p])
        half = len(seq) // 2
        train += seq[:half]
        test += seq[half:]
    PARAMS.clear(); PARAMS.update(DEFAULT_PARAMS)
    before = replay(test, trim=False)
    learned, notes = learn_params(train)
    PARAMS.clear(); PARAMS.update(learned)
    after = replay(test, trim=False)
    print("Validação nas poses que conferiram (calibra com metade dos quadros, mede na outra metade):")
    report("padrão", before)
    report("com perfil pessoal", after)
    final, notes = learn_params(frames)
    gain = after["stable"] - before["stable"]
    keep = gain >= 0.5 and after["wrong"] <= before["wrong"] + 0.5
    PARAMS.clear(); PARAMS.update(DEFAULT_PARAMS)
    if not keep:
        print("O perfil pessoal não trouxe ganho claro; os limiares padrão já servem para a sua mão.")
        remove_profile(profile_path, "sem ganho")
        return 0
    with open(profile_path, "w", encoding="utf-8") as fh:
        json.dump({"params": final, "notes": notes, "frames": len(frames), "poses": ok_poses,
                   "sources": origin, "created": time.strftime("%Y-%m-%d %H:%M")},
                  fh, ensure_ascii=False, indent=2)
    print("Perfil pessoal salvo em %s (o Particulas.exe passa a usá-lo)." % profile_path)
    overlap = [k for k, v in notes.items() if v != "ok"]
    if overlap:
        print("Atenção, sinais pouco separados na sua coleta: %s" % ", ".join(overlap))
    return 0


def evaluate_file(path, profile_path):
    """Mede o reconhecimento numa coleta (arquivo) ou numa pasta de coletas — nesse caso só
    nas poses que conferiram (a gravação mais recente certa de cada uma)."""
    if os.path.isdir(path):
        chosen, origin, problems = best_frames(collection_files(path))
        frames = [d for p in POSES if p in chosen for d in chosen[p]]
        print("%d poses conferidas (%d quadros) nas coletas de %s%s" % (
            len(chosen), len(frames), path, "; fora: " + ", ".join(problems) if problems else ""))
    else:
        frames = load_frames(path)
        print("%d quadros de %d poses em %s" % (len(frames), len({d['pose'] for d in frames}), path))
    PARAMS.clear(); PARAMS.update(DEFAULT_PARAMS)
    report("padrão", replay(frames))
    if profile_path and load_profile(profile_path):
        r = replay(frames)
        report("com perfil pessoal", r)
        for (want, got), c in r["confusion"].most_common(5):
            print("    %s -> %s: %d quadros" % (want, got, c))
    return 0


# ---- câmera em thread própria: sempre o quadro mais recente (menos latência) -------
class Camera:
    def __init__(self, index):
        import cv2
        self.cap = cv2.VideoCapture(index, cv2.CAP_DSHOW)
        if not self.cap.isOpened():
            self.cap = cv2.VideoCapture(index)
        self.ok = self.cap.isOpened()
        if not self.ok:
            return
        self.cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        self.cap.set(cv2.CAP_PROP_FPS, 30)
        self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        self.cond = threading.Condition()
        self.frame, self.seq, self.alive = None, 0, True
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        while self.alive:
            ok, f = self.cap.read()
            if not ok:
                time.sleep(0.01)
                continue
            with self.cond:
                self.frame, self.seq = f, self.seq + 1
                self.cond.notify()

    def latest(self, last_seq, timeout=1.0):
        with self.cond:
            self.cond.wait_for(lambda: self.seq != last_seq, timeout)
            return self.frame, self.seq

    def close(self):
        self.alive = False
        self.cap.release()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--modelo")
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--debug", action="store_true", help="mostra uma janela com a câmera")
    ap.add_argument("--teste", type=float, default=0, help="roda N segundos e sai (diagnóstico)")
    ap.add_argument("--autoteste", action="store_true", help="testa o classificador com mãos sintéticas")
    ap.add_argument("--simular", action="store_true", help="sem câmera: envia mãos sintéticas em todas as poses")
    ap.add_argument("--coletar", nargs="?", const="auto", help="coleta guiada das poses + calibração pessoal")
    ap.add_argument("--tudo", action="store_true", help="com --coletar: refaz todas as poses (não só as pendentes)")
    ap.add_argument("--calibrar", help="calibra o perfil pessoal a partir de uma coleta (.jsonl) ou de uma pasta de coletas")
    ap.add_argument("--avaliar", help="mede o reconhecimento numa coleta (.jsonl) ou numa pasta de coletas")
    ap.add_argument("--perfil", help="perfil pessoal (padrão: perfil.json ao lado do modelo)")
    args = ap.parse_args()

    if args.autoteste:
        return self_test()
    if args.simular:
        return simulate()
    if args.calibrar:
        target = os.path.abspath(args.calibrar)
        folder = target if os.path.isdir(target) else os.path.dirname(target)
        sources = collection_files(target) if os.path.isdir(target) else [target]
        return calibrate(sources, args.perfil or os.path.join(folder, "perfil.json"))
    if args.avaliar:
        target = os.path.abspath(args.avaliar)
        folder = target if os.path.isdir(target) else os.path.dirname(target)
        return evaluate_file(target, args.perfil or os.path.join(folder, "perfil.json"))
    if not args.modelo:
        ap.error("--modelo é obrigatório")
    if args.coletar:
        return collect(args)
    profile = args.perfil or os.path.join(os.path.dirname(os.path.abspath(args.modelo)), "perfil.json")
    has_profile = os.path.exists(profile) and load_profile(profile)

    try:
        import cv2
        import mediapipe as mp
        from mediapipe.tasks.python import BaseOptions, vision
    except Exception as e:  # noqa: BLE001
        out("ERR bibliotecas ausentes (%s) - rode instalar.bat" % e.__class__.__name__)
        return 2

    if not os.path.exists(args.modelo):
        out("ERR modelo nao encontrado - rode instalar.bat")
        return 2

    cam = Camera(args.camera)
    if not cam.ok:
        out("ERR nao consegui abrir a camera")
        return 3

    options = vision.HandLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=args.modelo),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=2,
        min_hand_detection_confidence=0.6,
        min_hand_presence_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    landmarker = vision.HandLandmarker.create_from_options(options)
    out("OK camera" + (" perfil" if has_profile else ""))

    tracker = Tracker()
    t0 = time.monotonic()
    last_ts, seq, frames, infer_total = -1, 0, 0, 0.0
    poses_seen = Counter()
    while True:
        frame, seq2 = cam.latest(seq)
        if frame is None or seq2 == seq:
            continue
        seq = seq2
        frame = cv2.flip(frame, 1)  # espelho
        h, w = frame.shape[:2]
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        ts = max(int((time.monotonic() - t0) * 1000), last_ts + 1)
        last_ts = ts
        t_inf = time.perf_counter()
        res = landmarker.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), ts)
        infer_total += time.perf_counter() - t_inf
        frames += 1

        hands = []
        for k, hand in enumerate(res.hand_landmarks or []):
            side = "R"
            if res.handedness and k < len(res.handedness) and res.handedness[k]:
                side = res.handedness[k][0].category_name[:1] or "R"
            world = res.hand_world_landmarks[k] if res.hand_world_landmarks else hand
            hands.append((side, hand, world))
        for pose, conf, alt, quality, hand in tracker.process(hands, h / w):
            poses_seen[pose] += 1
            if args.debug:
                cx, cy = hand[9].x, hand[9].y
                for p in hand:
                    cv2.circle(frame, (int(p.x * w), int(p.y * h)), 3, (0, 255, 255), -1)
                cv2.putText(frame, "%s %d%% (%s) %s" % (pose, conf * 100, alt, quality), (int(cx * w) - 60, int(cy * h)),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 0, 255), 2)
        duo = tracker.duo

        if args.debug:
            cv2.putText(frame, "duo: " + duo, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2)
            cv2.imshow("maos.py (Esc para sair)", frame)
            if cv2.waitKey(1) == 27:
                break
        if args.teste and time.monotonic() - t0 > args.teste:
            out("INFO quadros=%d fps=%.1f inferencia=%.1fms poses=%s"
                % (frames, frames / args.teste, 1000 * infer_total / max(1, frames), dict(poses_seen)))
            break

    cam.close()
    return 0


# ---- autoteste: modelo cinemático de mão --------------------------------------------
# Gera os 21 pontos de mãos sintéticas em cada pose (dedos com comprimentos e juntas
# realistas), aplica rotações aleatórias e ruído, e confere se o classificador acerta.
MCP = {"indicador": (0.028, 0.088), "medio": (0.006, 0.092), "anelar": (-0.014, 0.086), "mindinho": (-0.032, 0.076)}
SEGS = {"indicador": (0.040, 0.024, 0.020), "medio": (0.044, 0.028, 0.022),
        "anelar": (0.041, 0.026, 0.021), "mindinho": (0.032, 0.019, 0.018)}
SPLAY = {"indicador": 8, "medio": 0, "anelar": -8, "mindinho": -16}

POSES = {
    # pose: (dedos esticados, polegar, abertura extra dos dedos)
    "aberta": ("imar", "ext", None),
    "quatro": ("imar", "fold", None),
    "spock": ("imar", "ext", {"indicador": 5, "medio": 2, "anelar": -25, "mindinho": -29}),
    "punho": ("", "fold", None),
    "joinha": ("", "up", None),
    "negativo": ("", "down", None),
    "indicador": ("i", "fold", None),
    "L": ("i", "ext", None),
    "v": ("im", "fold", {"indicador": 12, "medio": -6}),
    "pistola": ("im", "ext", None),
    "tres": ("ima", "fold", None),
    "rock": ("ir", "fold", None),
    "teamo": ("ir", "ext", None),
    "hangloose": ("r", "ext", None),
    "mindinho": ("r", "fold", None),
    "ok": ("mar", "pinch", None),
    "pinca": ("", "pinch", None),
}
LETTER = {"indicador": "i", "medio": "m", "anelar": "a", "mindinho": "r"}


def rot(p, ax, ay, az):
    x, y, z = p
    c, s = math.cos(ax), math.sin(ax)
    y, z = y * c - z * s, y * s + z * c
    c, s = math.cos(ay), math.sin(ay)
    x, z = x * c + z * s, -x * s + z * c
    c, s = math.cos(az), math.sin(az)
    x, y = x * c - y * s, x * s + y * c
    return (x, y, z)


Z_MULT = 2.5          # ruído de profundidade relativo (o MediaPipe erra mais no eixo da câmera)


def make_identity(rng):
    """Características fixas de uma pessoa/mão: lado, costas para a câmera, tamanho, proporções."""
    return {"left": rng.random() < 0.5, "back": rng.random() < 0.15, "scale": rng.uniform(0.8, 1.25),
            "segs": {f: rng.uniform(0.9, 1.1) for f in MCP}}


def synth_hand(pose, rng, hard=False, real=False, identity=None, style=None):
    """hard=True: dedos menos definidos (esticados meio relaxados, dobrados menos fechados),
    mais ruído e a mão mais inclinada, como numa webcam real com gente sem prática.
    real=True (além de hard): mão esquerda ou direita, às vezes as costas da mão para a
    câmera, mãos de tamanhos e proporções diferentes e ruído de profundidade maior (o
    MediaPipe erra mais no eixo da câmera).
    style: jeito particular de uma pessoa (dedos que não esticam/fecham totalmente, polegar
    pouco aberto, Spock tímido) — usado para testar a calibração pessoal."""
    fingers_ext, thumb_mode, splay_over = POSES[pose]
    noise_deg, pos_noise, tilt = (14, 0.0035, 1.5) if hard else (8, 0.002, 1.0)
    ident = (identity or make_identity(rng)) if real else None
    pts = [None] * 21
    pts[0] = (0.0, 0.0, 0.0)
    for f, (mx, my) in MCP.items():
        mcp, pip, dip, tip = FINGERS[f]
        extended = LETTER[f] in fingers_ext
        if thumb_mode == "pinch" and f == "indicador":
            flex = (30, 35, 20)
        elif extended:
            flex = (style or {}).get("ext") or ((12, 12, 8) if hard else (5, 5, 3))
        else:
            flex = (style or {}).get("fold") or ((65, 90, 55) if hard else (75, 100, 60))
        flex = [a + rng.uniform(-noise_deg, noise_deg) for a in flex]
        splay = dict(splay_over or SPLAY)
        if pose == "spock" and style and "spock" in style:
            splay["anelar"], splay["mindinho"] = style["spock"]
        sp = math.radians(splay.get(f, SPLAY[f]) + rng.uniform(-3, 3))
        p = (mx, my, 0.0)
        pts[mcp] = p
        phi = 0.0
        seg_scale = ident["segs"][f] if real else 1.0
        for joint, length, bend in zip((pip, dip, tip), [s * seg_scale for s in SEGS[f]], flex):
            phi += math.radians(bend)
            d = (math.sin(sp) * math.cos(phi), math.cos(sp) * math.cos(phi), math.sin(phi))
            p = (p[0] + d[0] * length, p[1] + d[1] * length, p[2] + d[2] * length)
            pts[joint] = p

    cmc = (0.024, 0.022, 0.004)
    pts[1] = cmc
    if thumb_mode in ("ext", "up", "down"):
        a = math.radians(rng.uniform(*((style or {}).get("thumb") or ((52, 85) if hard else (60, 80)))))
        d = (math.sin(a), math.cos(a), 0.08)
        pts[2] = (cmc[0] + d[0] * 0.035, cmc[1] + d[1] * 0.035, cmc[2] + d[2] * 0.035)
        pts[3] = (pts[2][0] + d[0] * 0.030, pts[2][1] + d[1] * 0.030, pts[2][2] + d[2] * 0.030)
        pts[4] = (pts[3][0] + d[0] * 0.026, pts[3][1] + d[1] * 0.026, pts[3][2] + d[2] * 0.026)
    elif thumb_mode == "fold":
        pts[2], pts[3], pts[4] = (0.034, 0.045, 0.012), (0.022, 0.060, 0.030), (0.004, 0.064, 0.040)
    else:  # pinça: ponta do polegar encostada na ponta do indicador
        a = math.radians(70)
        pts[2] = (cmc[0] + math.sin(a) * 0.035, cmc[1] + math.cos(a) * 0.035, cmc[2] + 0.01)
        t = pts[8]
        pts[4] = (t[0] + 0.004, t[1] - 0.004, t[2] + 0.004)
        pts[3] = ((pts[2][0] + pts[4][0]) / 2 + 0.012, (pts[2][1] + pts[4][1]) / 2, (pts[2][2] + pts[4][2]) / 2)

    # Orientação: joinha/negativo giram a mão para o polegar apontar para cima/baixo
    roll = math.radians(rng.uniform(-25, 25) * tilt)
    if thumb_mode == "up":
        roll += math.atan2(pts[4][0] - pts[2][0], pts[4][1] - pts[2][1])
    elif thumb_mode == "down":
        roll += math.atan2(pts[4][0] - pts[2][0], pts[4][1] - pts[2][1]) + math.pi
    pitch, yaw = math.radians(rng.uniform(-25, 25) * tilt), math.radians(rng.uniform(-35, 35) * tilt)
    z_noise = pos_noise
    if real:
        if ident["left"]:                            # mão esquerda = espelho da direita
            pts = [(-p[0], p[1], p[2]) for p in pts]
            roll = -roll
        if ident["back"]:                            # costas da mão para a câmera
            yaw += math.pi
            roll = -roll                             # (o giro espelha x; mantém o polegar no lugar)
        s = ident["scale"]
        pts = [(p[0] * s, p[1] * s, p[2] * s) for p in pts]
        z_noise = pos_noise * Z_MULT
    world = []
    for p in pts:
        q = rot(p, pitch, yaw, roll)
        world.append((q[0] + rng.gauss(0, pos_noise), q[1] + rng.gauss(0, pos_noise), q[2] + rng.gauss(0, z_noise)))
    # Imagem: projeção simples, com y crescendo para baixo
    img = [(0.5 + q[0] * 3, 0.5 - q[1] * 3) for q in world]
    # O MediaPipe dá y "do mundo" crescendo para baixo também
    world = [(q[0], -q[1], q[2]) for q in world]
    return world, img


def evaluate(classify_fn, hard, trials=200, seed=1, verbose=False, real=False):
    """Acerto e 'disparo errado' (pose válida porém trocada) por quadro isolado."""
    rng = random.Random(seed)
    total = ok = wrong = 0
    confusion = Counter()
    for pose in POSES:
        hits = 0
        for _ in range(trials):
            world, img = synth_hand(pose, rng, hard, real)
            got = classify_fn(world, img, HandState())
            got = got[0] if isinstance(got, tuple) else got
            if got == pose:
                hits += 1
            else:
                confusion[(pose, got)] += 1
                if got != "outra":
                    wrong += 1
        total += trials
        ok += hits
        if verbose:
            print("  %-10s %5.1f%%" % (pose, 100.0 * hits / trials))
    return 100.0 * ok / total, 100.0 * wrong / total, confusion


def stability(classify_fn, vote_fn, hard, frames=40, seed=2, real=False, smooth=False):
    """Simula a mão parada numa pose por 'frames' quadros (cada quadro com ruído próprio; no
    modo realista a mesma pessoa/mão do começo ao fim) e mede: % de quadros com a pose
    estável certa e quantas vezes a pose estável trocou. smooth=True reproduz o caminho do
    app: pontos suavizados -> classificação -> fusão temporal."""
    rng = random.Random(seed)
    right = counted = switches = 0
    for pose in POSES:
        state, last = HandState(), None
        ident = make_identity(rng) if real else None
        for k in range(frames):
            world, img = synth_hand(pose, rng, hard, real, ident)
            if smooth:
                world = state.smooth_world(world)
            stable = vote_fn(state, classify_fn(world, img, state))
            if k >= 6:
                counted += 1
                right += stable == pose
                if last is not None and stable != last:
                    switches += 1
            last = stable
    return 100.0 * right / counted, switches


MODES = (
    ("normais", False, False),
    ("difíceis (relaxadas, inclinadas, ruidosas)", True, False),
    ("realistas (+ esquerda/direita, costas, tamanhos, ruído de profundidade)", True, True),
)
# Metas por modo: pose estável certa (%), trocas indevidas, disparo errado por quadro (%)
TARGETS = {False: (99.5, 0, 1.0), True: (99.0, 3, 3.0)}


def self_test(verbose=True):
    passed = True
    for label, hard, real in MODES:
        print("=== mãos %s ===" % label)
        acc, wrong, confusion = evaluate(classify, hard, verbose=verbose and not real, real=real)
        if verbose:
            for (want, got), c in confusion.most_common(6):
                print("    confusão: %s -> %s x%d" % (want, got, c))
        steady, switches = stability(classify, lambda s, r: s.fuse(r[3])[0], hard, real=real, smooth=True)
        want_steady, want_switches, want_wrong = TARGETS[hard]
        ok = steady >= want_steady and switches <= want_switches and wrong <= want_wrong
        passed &= ok
        print("  quadro isolado: acerto %.1f%%, disparo errado %.1f%% (meta <= %.1f%%)" % (acc, wrong, want_wrong))
        print("  como no app (suavização + fusão): pose certa %.1f%% (meta >= %.1f%%), %d trocas indevidas (meta <= %d)  %s"
              % (steady, want_steady, switches, want_switches, "OK" if ok else "ABAIXO DA META"))
    return 0 if passed else 2


if __name__ == "__main__":
    sys.exit(main())
