"""Rastreador de mãos do Partículas 3D.

Lê a webcam, encontra até duas mãos com o MediaPipe Hand Landmarker e classifica a pose
de cada uma. O Particulas.exe inicia este script e lê o stdout, uma linha por quadro:

    F <n> <duo>|<pose> <lado> <cx> <cy> <tam> <pinçaX> <pinçaY> <pontaX> <pontaY> <x0> <y0> ... <x20> <y20>|...

Coordenadas de imagem em 0..1, já espelhadas (como um espelho). <duo> é o gesto feito com
as duas mãos juntas, ou "-". Mensagens de estado saem como "OK ..." ou "ERR ...". Quando o
Particulas.exe fecha, a escrita no stdout falha e este script termina junto.

Como a pose é decidida:
  * pelos ângulos das juntas em 3D (pontos "do mundo" do MediaPipe, em metros), o que não
    depende da distância até a câmera nem de a mão estar inclinada;
  * com votação nos últimos quadros (a pose não "pisca") e histerese na pinça.

Uso isolado:
  python maos.py --modelo hand_landmarker.task --debug     janela com a câmera e as poses
  python maos.py --modelo hand_landmarker.task --teste 5   roda 5 s e mostra estatísticas
  python maos.py --autoteste                               testa o classificador com mãos sintéticas
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
    mcp, pip, dip, tip = FINGERS[f]
    return (0.5 * joint_bend(w[WRIST], w[mcp], w[pip])
            + joint_bend(w[mcp], w[pip], w[dip])
            + joint_bend(w[pip], w[dip], w[tip]))


class HandState:
    """Memória por mão: votação das poses recentes e histerese da pinça."""

    def __init__(self):
        self.recent = deque(maxlen=5)
        self.stable = None
        self.pinching = False
        self.missing = 0

    def vote(self, pose):
        self.recent.append(pose)
        best, count = Counter(self.recent).most_common(1)[0]
        if self.stable is None or count >= 3:
            self.stable = best
        return self.stable


def classify(w, img, state):
    """w: 21 pontos 3D em metros; img: 21 pontos (x, y) de imagem com escala isotrópica
    (y cresce para baixo). Devolve o nome da pose (sem votação)."""
    palm = dist(w[WRIST], w[9]) or 1e-6

    ext = {f: finger_bend(w, f) < 80 for f in FINGERS}

    thumb_bend = joint_bend(w[1], w[2], w[3]) + joint_bend(w[2], w[3], w[4])
    thumb = thumb_bend < 60 and dist(w[4], w[9]) / palm > 0.85
    thumb_dir, index_dir = sub(w[4], w[2]), sub(w[8], w[5])
    thumb_spread = angle_between(thumb_dir, index_dir)

    # Pinça com histerese: fecha abaixo de 0,28 palma e só abre acima de 0,45.
    # O indicador precisa estar só meio dobrado: no punho a ponta do polegar também
    # encosta na do indicador, mas o indicador está todo enrolado.
    pinch_d = dist(w[4], w[8]) / palm
    index_bend = finger_bend(w, "indicador")
    state.pinching = index_bend < 140 and pinch_d < (0.45 if state.pinching else 0.28)

    i, m, a, n = ext["indicador"], ext["medio"], ext["anelar"], ext["mindinho"]
    if state.pinching:
        if m and a and n:
            return "ok"
        if sum((m, a, n)) <= 1:
            return "pinca"

    count = sum((i, m, a, n))
    if count == 4:
        # Spock: médio e anelar bem separados, os pares vizinhos juntos. A abertura é medida
        # no plano da palma, para que dedos um pouco dobrados não pareçam afastados.
        u, v = sub(w[5], w[WRIST]), sub(w[17], w[WRIST])
        pn = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        ln = norm(pn) or 1e-9
        pn = (pn[0] / ln, pn[1] / ln, pn[2] / ln)
        d = []
        for f in FINGERS:
            r = sub(w[FINGERS[f][3]], w[FINGERS[f][0]])
            k = r[0] * pn[0] + r[1] * pn[1] + r[2] * pn[2]
            d.append((r[0] - k * pn[0], r[1] - k * pn[1], r[2] - k * pn[2]))
        g_im, g_mr, g_rp = angle_between(d[0], d[1]), angle_between(d[1], d[2]), angle_between(d[2], d[3])
        if g_mr > 15 and g_mr > 2.0 * max(g_im, g_rp):
            return "spock"
        return "aberta" if thumb else "quatro"
    if count == 0:
        if thumb:
            # Joinha / negativo: o polegar aponta para cima / para baixo na imagem
            pl = math.hypot(img[0][0] - img[9][0], img[0][1] - img[9][1]) or 1e-6
            dx, dy = img[4][0] - img[2][0], img[4][1] - img[2][1]
            if abs(dy) > 1.2 * abs(dx):
                if dy < 0 and img[4][1] < img[5][1] - 0.25 * pl:
                    return "joinha"
                if dy > 0 and img[4][1] > img[5][1] + 0.25 * pl:
                    return "negativo"
        return "punho"
    if i and not m and not a and n:
        return "teamo" if thumb else "rock"
    if not i and not m and not a and n:
        return "hangloose" if thumb else "mindinho"
    if i and not m and not a and not n:
        return "L" if thumb and thumb_spread > 45 else "indicador"
    if i and m and not a and not n:
        return "pistola" if thumb and thumb_spread > 45 else "v"
    if i and m and a and not n:
        return "tres"
    return "outra"


DUO_FROM_PAIR = {
    ("indicador", "indicador"): "dois-indicadores",
    ("v", "v"): "dois-v",
    ("punho", "punho"): "dois-punhos",
    ("aberta", "aberta"): "duas-abertas",
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
    args = ap.parse_args()

    if args.autoteste:
        return self_test()
    if not args.modelo:
        ap.error("--modelo é obrigatório")

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
    out("OK camera")

    states = {}
    t0 = time.monotonic()
    last_ts, seq, frames, infer_total = -1, 0, 0, 0.0
    poses_seen = Counter()
    duo_votes = deque(maxlen=5)
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

        chunks, duo_input, seen = [], [], set()
        for k, hand in enumerate(res.hand_landmarks or []):
            side = "R"
            if res.handedness and k < len(res.handedness) and res.handedness[k]:
                side = res.handedness[k][0].category_name[:1] or "R"
            key = side if side not in seen else side + str(k)
            seen.add(key)
            state = states.setdefault(key, HandState())
            state.missing = 0

            world = res.hand_world_landmarks[k] if res.hand_world_landmarks else hand
            wpts = [(p.x, p.y, p.z) for p in world]
            img = [(p.x, p.y * h / w) for p in hand]          # escala isotrópica
            pose = state.vote(classify(wpts, img, state))
            poses_seen[pose] += 1
            duo_input.append((pose, img))

            ids = (0, 5, 9, 13, 17)
            cx = sum(hand[j].x for j in ids) / 5
            cy = sum(hand[j].y for j in ids) / 5
            size = math.hypot(hand[0].x - hand[9].x, (hand[0].y - hand[9].y) * h / w)
            px, py = (hand[4].x + hand[8].x) / 2, (hand[4].y + hand[8].y) / 2
            coords = " ".join("%.3f %.3f" % (p.x, p.y) for p in hand)
            chunks.append("%s %s %.3f %.3f %.3f %.3f %.3f %.3f %.3f %s"
                          % (pose, side, cx, cy, size, px, py, hand[8].x, hand[8].y, coords))

            if args.debug:
                for p in hand:
                    cv2.circle(frame, (int(p.x * w), int(p.y * h)), 3, (0, 255, 255), -1)
                cv2.putText(frame, pose, (int(cx * w), int(cy * h)), cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 0, 255), 2)

        # Esquece mãos que sumiram há alguns quadros
        for key in list(states):
            if key not in seen:
                states[key].missing += 1
                if states[key].missing > 8:
                    del states[key]

        duo_votes.append(detect_duo(duo_input))
        duo, cnt = Counter(duo_votes).most_common(1)[0]
        if cnt < 3:
            duo = "-"
        out("F %d %s|%s" % (len(chunks), duo, "|".join(chunks)))

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


def synth_hand(pose, rng):
    fingers_ext, thumb_mode, splay_over = POSES[pose]
    pts = [None] * 21
    pts[0] = (0.0, 0.0, 0.0)
    for f, (mx, my) in MCP.items():
        mcp, pip, dip, tip = FINGERS[f]
        extended = LETTER[f] in fingers_ext
        if thumb_mode == "pinch" and f == "indicador":
            flex = (30, 35, 20)
        elif extended:
            flex = (5, 5, 3)
        else:
            flex = (75, 100, 60)
        flex = [a + rng.uniform(-8, 8) for a in flex]
        sp = math.radians((splay_over or SPLAY).get(f, SPLAY[f]) + rng.uniform(-3, 3))
        p = (mx, my, 0.0)
        pts[mcp] = p
        phi = 0.0
        for joint, length, bend in zip((pip, dip, tip), SEGS[f], flex):
            phi += math.radians(bend)
            d = (math.sin(sp) * math.cos(phi), math.cos(sp) * math.cos(phi), math.sin(phi))
            p = (p[0] + d[0] * length, p[1] + d[1] * length, p[2] + d[2] * length)
            pts[joint] = p

    cmc = (0.024, 0.022, 0.004)
    pts[1] = cmc
    if thumb_mode in ("ext", "up", "down"):
        a = math.radians(rng.uniform(60, 80))
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
    roll = math.radians(rng.uniform(-25, 25))
    if thumb_mode == "up":
        roll += math.atan2(pts[4][0] - pts[2][0], pts[4][1] - pts[2][1])
    elif thumb_mode == "down":
        roll += math.atan2(pts[4][0] - pts[2][0], pts[4][1] - pts[2][1]) + math.pi
    pitch, yaw = math.radians(rng.uniform(-25, 25)), math.radians(rng.uniform(-35, 35))
    world = []
    for p in pts:
        q = rot(p, pitch, yaw, roll)
        world.append((q[0] + rng.gauss(0, 0.002), q[1] + rng.gauss(0, 0.002), q[2] + rng.gauss(0, 0.002)))
    # Imagem: projeção simples, com y crescendo para baixo
    img = [(0.5 + q[0] * 3, 0.5 - q[1] * 3) for q in world]
    # O MediaPipe dá y "do mundo" crescendo para baixo também
    world = [(q[0], -q[1], q[2]) for q in world]
    return world, img


def self_test():
    rng = random.Random(1)
    trials, total, ok = 200, 0, 0
    confusion = Counter()
    for pose in POSES:
        hits = 0
        for _ in range(trials):
            world, img = synth_hand(pose, rng)
            got = classify(world, img, HandState())
            if got == pose:
                hits += 1
            else:
                confusion[(pose, got)] += 1
        total += trials
        ok += hits
        print("%-10s %5.1f%%" % (pose, 100.0 * hits / trials))
    for (want, got), c in confusion.most_common(12):
        print("  confusão: %s -> %s x%d" % (want, got, c))
    acc = 100.0 * ok / total
    print("TOTAL      %5.1f%%  (%d/%d)" % (acc, ok, total))
    return 0 if acc >= 90 else 2


if __name__ == "__main__":
    sys.exit(main())
