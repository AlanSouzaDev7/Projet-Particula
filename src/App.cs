using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Particulas
{
    // Laço principal, entrada (câmera, mouse, teclado), câmera 3D e sobreposições na tela
    sealed class App
    {
        const string ESC = "\u001b";
        const double Dt = 1.0 / 40.0;
        public const int ParticleCount = 9600;
        const double ShapeLockSeconds = 4.3;            // tempo mínimo entre uma troca de forma e outra

        readonly Shape[] shapes = Shapes.All();
        readonly Sim sim = new Sim(ParticleCount);
        readonly Renderer rend = new Renderer();
        Stream stdout;
        IntPtr hIn, hOut;
        readonly Native.INPUT_RECORD[] inputBuf = new Native.INPUT_RECORD[128];

        bool running = true, braille, welcome = true;
        int helpPage;                                   // 0 fechado, 1 formas, 2 controles
        int current = -1, lastShape = 0;
        double time, welcomeUntil = 12;
        string toast = ""; int toastColor; double toastUntil;

        // ---- câmera 3D ----
        double yaw, tilt = 0.15, tiltOffset, zoom = 1.0;
        double skyYaw, skyYawRef;                       // azimute do céu de fundo (paralaxe)
        const double SkyParallax = 0.3, SkyDrift = 0.004;

        // ---- desempenho ----
        int fpsFrames; double fpsClock, fps;

        // ---- mouse ----
        readonly List<P> stroke = new List<P>();        // em células da tela
        List<P> fading; double fadeUntil; int fadeColor;
        bool leftDown, rightDown;

        // ---- mãos (câmera) ----
        // Tempo segurando a pose para confirmar: curto quando o rastreador tem certeza,
        // mais longo quando a pose está duvidosa (evita disparos por engano)
        double poseConf;
        double HoldNeeded
        {
            get { return poseConf >= 0.75 ? 0.32 : (poseConf >= 0.5 ? 0.45 : 0.6); }
        }
        readonly HandTracker tracker = new HandTracker();
        HandFrame frame = HandFrame.Empty;
        Hand main;
        double mainX, mainY;                            // centro da palma suavizado (0..1)
        double pinchX, pinchY;                          // ponto da pinça suavizado (0..1)
        readonly OneEuro fX = new OneEuro(1.2, 4.0, 1.0), fY = new OneEuro(1.2, 4.0, 1.0);
        readonly OneEuro fPX = new OneEuro(1.5, 6.0, 1.0), fPY = new OneEuro(1.5, 6.0, 1.0);
        string holdPose = "";
        double holdTime;
        bool holdFired;

        bool grabbing;                                  // mão aberta "segurando" o objeto
        double anchorX, anchorY, anchorYaw, anchorTilt, swipeCooldown;
        readonly Queue<KeyValuePair<double, double>> swipeTrail = new Queue<KeyValuePair<double, double>>();

        bool zooming;                                   // duas mãos abertas
        double zoomDist0, zoom0;

        bool drawing;                                   // desenhando no ar com a pinça
        double lastPinchTime;
        readonly List<P> airStroke = new List<P>();

        double lastFistTime = -10;
        double lastChange = -100;                       // quando a forma mudou pela última vez

        static readonly Dictionary<string, int> PoseToShape = new Dictionary<string, int>
        {
            { "teamo", Shapes.Heart_ }, { "coracao", Shapes.Heart_ }, { "ok", Shapes.Planet_ },
            { "indicador", Shapes.Star_ }, { "hangloose", Shapes.Galaxy_ }, { "v", Shapes.Dna_ },
            { "quatro", Shapes.Cube_ }, { "tres", Shapes.Pyramid_ }, { "dois-indicadores", Shapes.Infinity_ },
            { "joinha", Shapes.Smiley_ }, { "mindinho", Shapes.Flower_ }, { "rock", Shapes.Lightning_ },
            { "dois-punhos", Shapes.Torus_ }, { "spock", Shapes.Atom_ }, { "dois-v", Shapes.Earth_ },
            { "L", Shapes.Diamond_ }, { "negativo", Shapes.Tornado_ }, { "pistola", Shapes.Waves_ },
        };

        static readonly int[][] Bones =
        {
            new[] { 0, 1, 2, 3, 4 }, new[] { 0, 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 },
            new[] { 13, 14, 15, 16 }, new[] { 0, 17, 18, 19, 20 }, new[] { 5, 9, 13, 17 },
        };

        static string PoseLabel(string pose)
        {
            switch (pose)
            {
                case "teamo": return "Eu te amo";
                case "coracao": return "Coração (2 mãos)";
                case "ok": return "OK";
                case "indicador": return "1 dedo";
                case "hangloose": return "Hang loose";
                case "v": return "V";
                case "quatro": return "4 dedos";
                case "tres": return "3 dedos";
                case "dois-indicadores": return "2 indicadores";
                case "joinha": return "Joinha";
                case "negativo": return "Negativo";
                case "mindinho": return "Mindinho";
                case "rock": return "Rock";
                case "spock": return "Spock";
                case "L": return "L";
                case "pistola": return "Pistola";
                case "pinca": return "Pinça (desenhe)";
                case "punho": return "Punho";
                case "dois-punhos": return "2 punhos";
                case "dois-v": return "2 mãos em V";
                case "aberta": return "Mão aberta";
                case "duas-abertas": return "2 mãos abertas";
                case "": return "";
                default: return "?";
            }
        }

        public int Run()
        {
            hIn = Native.GetStdHandle(Native.STD_INPUT_HANDLE);
            hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
            uint inMode0, outMode0;
            if (!Native.GetConsoleMode(hIn, out inMode0) || !Native.GetConsoleMode(hOut, out outMode0))
            {
                Console.Error.WriteLine("Execute este programa diretamente em um terminal (cmd ou Windows Terminal).");
                return 1;
            }
            if (!Native.SetConsoleMode(hOut, outMode0 | Native.ENABLE_VIRTUAL_TERMINAL_PROCESSING))
            {
                Console.Error.WriteLine("Este console não suporta sequências ANSI (requer Windows 10 ou superior).");
                return 1;
            }
            uint inMode = (inMode0 | Native.ENABLE_EXTENDED_FLAGS | Native.ENABLE_MOUSE_INPUT |
                           Native.ENABLE_WINDOW_INPUT | Native.ENABLE_PROCESSED_INPUT)
                          & ~Native.ENABLE_QUICK_EDIT_MODE & ~Native.ENABLE_VIRTUAL_TERMINAL_INPUT;
            Native.SetConsoleMode(hIn, inMode);

            // O Windows Terminal desenha Braille muito bem; no conhost antigo usamos pontos simples
            braille = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

            Encoding oldEnc = Console.OutputEncoding;
            string oldTitle = Console.Title;
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Title = "Partículas 3D — gestos com a mão";
            stdout = Console.OpenStandardOutput();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };
            Recognizer.RegisterDefaults();
            tracker.Start();
            Native.timeBeginPeriod(1);

            Out(ESC + "[?1049h" + ESC + "[?25l" + ESC + "[2J");
            try { Loop(); }
            finally
            {
                tracker.Dispose();
                Native.timeEndPeriod(1);
                Out(ESC + "[0m" + ESC + "[2J" + ESC + "[?25h" + ESC + "[?1049l");
                Native.SetConsoleMode(hIn, inMode0);
                Native.SetConsoleMode(hOut, outMode0);
                Console.OutputEncoding = oldEnc;
                Console.Title = oldTitle;
            }
            return 0;
        }

        void Out(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            stdout.Write(b, 0, b.Length);
            stdout.Flush();
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            double next = 0;
            int frameNo = 0;
            while (running)
            {
                PollInput();
                if (frameNo++ % 10 == 0 || rend.W == 0) CheckResize();
                Update();
                Draw();

                fpsFrames++;
                double now = sw.Elapsed.TotalSeconds;
                if (now - fpsClock >= 1.0) { fps = fpsFrames / (now - fpsClock); fpsFrames = 0; fpsClock = now; }

                next += Dt;
                int ms = (int)((next - now) * 1000);
                if (ms > 0) Thread.Sleep(ms);
                else if (now - next > 0.25) next = now;    // não tenta "recuperar" quadros perdidos
            }
        }

        void CheckResize()
        {
            int w, h;
            try { w = Console.WindowWidth; h = Console.WindowHeight; }
            catch (IOException) { w = 100; h = 30; }
            if (w == rend.W && h == rend.H) return;
            rend.Resize(w, h);
            UpdateExtents();
            Out(ESC + "[2J");
        }

        void UpdateExtents()
        {
            int sx, sy; double a;
            Renderer.Metrics(braille, out sx, out sy, out a);
            int rows = Math.Max(1, rend.H - 1);
            double size = rend.PixelsPerUnit(braille, rows, 1.0);
            sim.HalfW = rend.W * sx * 0.5 / size;
            sim.HalfH = rows * sy * 0.5 * a / size;
        }

        // ---- entrada: teclado e mouse ------------------------------------------------
        void PollInput()
        {
            uint count;
            if (!Native.GetNumberOfConsoleInputEvents(hIn, out count) || count == 0) return;
            uint read;
            if (!Native.ReadConsoleInput(hIn, inputBuf, (uint)Math.Min(count, inputBuf.Length), out read)) return;
            for (int i = 0; i < read; i++)
            {
                if (inputBuf[i].EventType == Native.KEY_EVENT && inputBuf[i].KeyEvent.bKeyDown != 0)
                    OnKey(inputBuf[i].KeyEvent.wVirtualKeyCode);
                else if (inputBuf[i].EventType == Native.MOUSE_EVENT)
                    OnMouse(inputBuf[i].MouseEvent);
            }
        }

        // As formas vêm dos gestos; o teclado só abre o guia e sai
        void OnKey(int vk)
        {
            welcome = false;
            switch (vk)
            {
                case 0x1B: case 0x51: running = false; break;          // Esc / Q
                case 0x48: case 0x70: helpPage = (helpPage + 1) % 3; break;   // H / F1: página 1, 2, fecha
            }
        }

        void OnMouse(Native.MOUSE_EVENT_RECORD m)
        {
            int top = 0, left = 0;
            try { top = Console.WindowTop; left = Console.WindowLeft; } catch (IOException) { }
            double mx = m.X - left, my = m.Y - top;

            if ((m.dwEventFlags & Native.MOUSE_WHEELED) != 0)
            {
                short delta = (short)(m.dwButtonState >> 16);
                zoom = delta > 0 ? Math.Min(2.8, zoom * 1.1) : Math.Max(0.4, zoom / 1.1);
                return;
            }

            bool l = (m.dwButtonState & 0x1) != 0, r = (m.dwButtonState & 0x2) != 0;
            if (l && !leftDown)
            {
                leftDown = true; welcome = false;
                stroke.Clear(); stroke.Add(new P(mx, my));
            }
            else if (l)
            {
                P lastP = stroke[stroke.Count - 1];
                if (lastP.X != mx || lastP.Y != my) stroke.Add(new P(mx, my));
            }
            else if (leftDown)
            {
                leftDown = false;
                ApplyStroke(stroke, "mouse", 0xFFFFFF);
            }

            if (r && !rightDown) { welcome = false; sim.Shock(mx, my, 14, 1.0); }
            rightDown = r;
        }

        // Traço (do mouse ou desenhado no ar) -> reconhecedor -> ação
        void ApplyStroke(List<P> cells, string source, int color)
        {
            if (cells.Count == 0) return;
            // Converte células -> coordenadas "quadradas" com y para cima (célula ≈ 2x mais alta)
            var pts = new List<P>(cells.Count);
            foreach (var p in cells) pts.Add(new P(p.X, -p.Y * 2.0));
            GestureResult res = Recognizer.Recognize(pts);

            fading = new List<P>(cells); fadeUntil = time + 0.7; fadeColor = color;
            P end = cells[cells.Count - 1];

            bool changesShape = res.Action != Recognizer.ActClick && res.Action != Recognizer.ActNone &&
                                !(res.Action >= 0 && res.Action == current);
            if (changesShape && LockRemaining > 0)
            {
                Toast("Aguarde " + LockRemaining.ToString("0.0") + " s para trocar de forma", 0xFFB070);
                return;
            }

            switch (res.Action)
            {
                case Recognizer.ActClick: sim.Shock(end.X, end.Y, 14, 1.0); fading = null; break;
                case Recognizer.ActNext: Select(Neighbor(+1), source + ": " + res.Name); break;
                case Recognizer.ActPrev: Select(Neighbor(-1), source + ": " + res.Name); break;
                case Recognizer.ActScatter: DoScatter(); break;
                case Recognizer.ActRegroup: Select(lastShape, source + ": " + res.Name); break;
                case Recognizer.ActNone:
                    Toast("Desenho não reconhecido" + (res.Name.Length > 0 ? " (parecia " + res.Name + ", " +
                          (int)(res.Score * 100) + "%)" : "") + " — H mostra o guia", 0xFF7A7A);
                    break;
                default:
                    Select(res.Action, source + ": " + res.Name + " " + (int)(res.Score * 100) + "%");
                    break;
            }
        }

        // Forma vizinha da atual (ou da última, se os pontos estiverem soltos)
        int Neighbor(int dir)
        {
            int baseIdx = current >= 0 ? current : lastShape - dir;
            return ((baseIdx + dir) % shapes.Length + shapes.Length) % shapes.Length;
        }

        // Segundos que faltam para poder trocar de forma de novo (0 = liberado)
        double LockRemaining
        {
            get { return Math.Max(0, ShapeLockSeconds - (time - lastChange)); }
        }

        void Select(int idx, string how)
        {
            welcome = false;
            current = idx; lastShape = idx;
            lastChange = time;
            sim.SetShape(shapes[idx], false);
            Toast(shapes[idx].Name + "   ‹" + how + "›", 0xFFFFFF);
        }

        void DoScatter()
        {
            welcome = false;
            current = -1;
            lastChange = time;
            sim.Scatter(false);
            Toast("Pontos espalhados", 0xB0C4FF);
        }

        void Toast(string text, int color)
        {
            toast = text; toastColor = color; toastUntil = time + 2.5;
        }

        // ---- atualização -------------------------------------------------------------
        void Update()
        {
            time += Dt;
            if (time > welcomeUntil) welcome = false;
            UpdateHands();

            Shape s = current >= 0 ? shapes[current] : null;
            if (!grabbing) tiltOffset *= 0.985;                     // volta devagar ao ângulo da forma
            double targetTilt = (s != null ? s.Tilt : 0.15) + tiltOffset;
            tilt += (targetTilt - tilt) * (grabbing ? 0.35 : 0.04);

            // Céu de fundo: acompanha (com paralaxe) só o giro feito com a mão; fora isso, deriva lenta
            if (grabbing) skyYaw += WrapAngle(yaw - skyYawRef) * SkyParallax;
            skyYaw += SkyDrift * Dt;

            if (!grabbing)
            {
                if (s == null) yaw += WrapAngle(0.25 * Math.Sin(time * 0.2) - yaw) * 0.03;
                else if (s.Spin > 0) yaw += s.Spin * Dt;
                else yaw += WrapAngle(0.55 * Math.Sin(time * 0.8) - yaw) * 0.05;
            }
            skyYawRef = yaw;

            sim.Step(time, s);
        }

        void UpdateHands()
        {
            double age;
            frame = tracker.Snapshot(out age);
            if (age > 0.5) frame = HandFrame.Empty;                 // dados velhos: as mãos saíram de cena

            main = null;                                             // a mão mais próxima da câmera
            foreach (var h in frame.Hands) if (main == null || h.Size > main.Size) main = h;
            if (main != null)
            {
                welcome = false;
                mainX = fX.Filter(main.Cx, Dt); mainY = fY.Filter(main.Cy, Dt);
                pinchX = fPX.Filter(main.PinchX, Dt); pinchY = fPY.Filter(main.PinchY, Dt);
            }
            else { fX.Reset(); fY.Reset(); fPX.Reset(); fPY.Reset(); }

            string pose = frame.Duo != "-" ? frame.Duo : (main != null ? main.Pose : "");
            double conf = main != null ? main.Conf : 0;
            if (frame.Duo != "-") foreach (var h in frame.Hands) conf = Math.Min(conf, h.Conf);
            poseConf += (conf - poseConf) * 0.3;

            // Segurar a pose por um instante confirma o gesto (evita disparos no meio de transições)
            if (pose == holdPose) holdTime += Dt;
            else { holdPose = pose; holdTime = 0; holdFired = false; }
            // Se a forma mudou há menos de 4,3 s, a pose fica "na fila": dispara quando a trava
            // acabar, desde que continue sendo feita (o painel mostra a contagem regressiva)
            if (!holdFired && holdTime >= HoldNeeded && !(WouldChange(pose) && LockRemaining > 0))
            {
                int idx;
                if (PoseToShape.TryGetValue(pose, out idx))
                {
                    holdFired = true;
                    if (idx != current) Select(idx, "mão: " + PoseLabel(pose));
                }
                else if (pose == "punho")
                {
                    holdFired = true;
                    if (current >= 0) DoScatter();
                }
            }

            // Punho -> mão aberta de repente = explosão
            if (main != null && main.Pose == "punho") lastFistTime = time;
            if (main != null && main.Pose == "aberta" && time - lastFistTime < 0.4)
            {
                double cx, cy;
                ToCell(mainX, mainY, out cx, out cy);
                sim.Shock(cx, cy, 40, 2.2);
                Toast("Explosão!", 0xFFB070);
                lastFistTime = -10;
            }

            UpdateAirDrawing();
            UpdateGrab();
            UpdateTwoHandZoom();
        }

        // A pose levaria a uma forma diferente da atual (ou espalharia os pontos)?
        bool WouldChange(string pose)
        {
            int idx;
            if (PoseToShape.TryGetValue(pose, out idx)) return idx != current;
            return pose == "punho" && current >= 0;
        }

        // Pinça fechada = caneta encostada: o traço no ar vai para o mesmo reconhecedor do mouse
        void UpdateAirDrawing()
        {
            if (main != null && main.Pose == "pinca")
            {
                if (!drawing) { drawing = true; airStroke.Clear(); }
                lastPinchTime = time;
                double cx, cy;
                ToCell(pinchX, pinchY, out cx, out cy);
                if (airStroke.Count == 0) airStroke.Add(new P(cx, cy));
                else
                {
                    P l = airStroke[airStroke.Count - 1];
                    double dx = cx - l.X, dy = (cy - l.Y) * 2;
                    if (dx * dx + dy * dy >= 0.5) airStroke.Add(new P(cx, cy));
                }
            }
            else if (drawing && time - lastPinchTime > 0.18)       // tolera piscadas curtas da pinça
            {
                drawing = false;
                if (airStroke.Count > 0) ApplyStroke(new List<P>(airStroke), "no ar", 0xFFD27A);
                airStroke.Clear();
            }
        }

        // Mão aberta: "segura" o objeto e gira em 3D; passada rápida para o lado troca de forma
        void UpdateGrab()
        {
            bool open = main != null && main.Pose == "aberta" && frame.Duo != "duas-abertas";
            if (!open) { grabbing = false; swipeTrail.Clear(); return; }
            if (!grabbing)
            {
                grabbing = true;
                anchorX = mainX; anchorY = mainY; anchorYaw = yaw; anchorTilt = tiltOffset;
                swipeTrail.Clear();
            }
            yaw = anchorYaw + (mainX - anchorX) * 5.5;
            tiltOffset = Math.Max(-1.2, Math.Min(1.2, anchorTilt + (mainY - anchorY) * 3.0));

            swipeTrail.Enqueue(new KeyValuePair<double, double>(time, main.Cx));
            while (swipeTrail.Count > 0 && time - swipeTrail.Peek().Key > 0.25) swipeTrail.Dequeue();
            if (time > swipeCooldown && swipeTrail.Count > 2)
            {
                double dx = main.Cx - swipeTrail.Peek().Value;
                if (Math.Abs(dx) > 0.3)
                {
                    yaw = anchorYaw;                                 // desfaz o giro causado pela passada
                    if (LockRemaining > 0)
                        Toast("Aguarde " + LockRemaining.ToString("0.0") + " s para trocar de forma", 0xFFB070);
                    else
                        Select(Neighbor(dx > 0 ? +1 : -1), dx > 0 ? "mão →" : "mão ←");
                    swipeCooldown = time + 1.0;
                    swipeTrail.Clear();
                    anchorX = mainX; anchorY = mainY; anchorYaw = yaw; anchorTilt = tiltOffset;
                }
            }
        }

        // Duas mãos abertas: afastar = aproximar a câmera (zoom), juntar = afastar
        void UpdateTwoHandZoom()
        {
            if (frame.Duo != "duas-abertas" || frame.Hands.Count < 2) { zooming = false; return; }
            Hand a = frame.Hands[0], b = frame.Hands[1];
            double dx = (a.Cx - b.Cx) * 4.0 / 3.0, dy = a.Cy - b.Cy, d = Math.Sqrt(dx * dx + dy * dy);
            if (!zooming) { zooming = true; zoomDist0 = Math.Max(0.05, d); zoom0 = zoom; }
            double target = Math.Max(0.4, Math.Min(2.8, zoom0 * d / zoomDist0));
            zoom += (target - zoom) * 0.3;
        }

        void ToCell(double nx, double ny, out double cx, out double cy)
        {
            cx = nx * rend.W;
            cy = ny * Math.Max(1, rend.H - 1);
        }

        static double WrapAngle(double a)
        {
            a %= 2 * Math.PI;
            if (a > Math.PI) a -= 2 * Math.PI;
            if (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        // ---- desenho -----------------------------------------------------------------
        void Draw()
        {
            int rows = Math.Max(1, rend.H - 1);
            rend.Rasterize(sim, braille, rows, yaw, tilt, zoom, time, skyYaw);
            double gain = 1.0;
            if (current >= 0 && shapes[current].Electric)
                gain = 0.8 + 0.25 * Math.Abs(Math.Sin(time * 23) * Math.Sin(time * 7.3));
            rend.Compose(braille, gain);

            if (leftDown && stroke.Count > 0) DrawStroke(stroke, 0xFFFFFF);
            if (drawing && airStroke.Count > 0) DrawStroke(airStroke, 0xFFD27A);
            if (fading != null && time < fadeUntil)
            {
                double f = (fadeUntil - time) / 0.7;
                DrawStroke(fading, Renderer.Rgb(((fadeColor >> 16) & 255) * f, ((fadeColor >> 8) & 255) * f, (fadeColor & 255) * f));
            }

            DrawHandCursors();
            if (welcome) DrawWelcome();
            DrawCameraPanel(rows);
            if (helpPage > 0) DrawHelp();
            if (time < toastUntil && helpPage == 0) DrawToast(rows);
            DrawStatus();
            rend.Present(stdout);
        }

        void DrawStroke(List<P> pts, int color)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                P a = pts[i], b = i + 1 < pts.Count ? pts[i + 1] : pts[i];
                DrawLine((int)a.X, (int)a.Y, (int)b.X, (int)b.Y, '•', color, 0, 0, rend.W - 1, rend.H - 2);
            }
        }

        void DrawLine(int x0, int y0, int x1, int y1, char ch, int color, int minX, int minY, int maxX, int maxY)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= minX && x0 <= maxX && y0 >= minY && y0 <= maxY) rend.Dot(x0, y0, ch, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        bool Known(string pose)
        {
            return PoseToShape.ContainsKey(pose) || pose == "punho" || pose == "aberta" || pose == "pinca";
        }

        // Marca onde está cada mão sobre as partículas
        void DrawHandCursors()
        {
            foreach (var h in frame.Hands)
            {
                double cx, cy;
                if (h == main) ToCell(drawing ? pinchX : mainX, drawing ? pinchY : mainY, out cx, out cy);
                else ToCell(h.Cx, h.Cy, out cx, out cy);
                int x = (int)cx, y = (int)cy;
                int color = drawing && h == main ? 0xFFD27A : (grabbing && h == main ? 0x7CC8FF : (Known(h.Pose) ? 0x7CFFB2 : 0x9AA0C8));
                rend.Dot(x, y, '●', color);
                rend.Dot(x - 2, y, grabbing && h == main ? '[' : '(', color);
                rend.Dot(x + 2, y, grabbing && h == main ? ']' : ')', color);
            }
        }

        // Painel no canto: a mão vista pela câmera, a pose reconhecida e o progresso da confirmação
        void DrawCameraPanel(int rows)
        {
            const int pw = 32, ph = 13;
            if (rend.W < pw + 4 || rows < ph + 2) return;
            int x0 = rend.W - pw - 1, y0 = rows - ph;
            int bg = 0x10101A, border = 0x4A4A78;

            rend.Put(x0, y0, "┌" + " câmera ".PadRight(pw - 2, '─') + "┐", border, bg);
            for (int i = 1; i < ph - 1; i++)
                rend.Put(x0, y0 + i, "│" + new string(' ', pw - 2) + "│", border, bg);
            rend.Put(x0, y0 + ph - 1, "└" + new string('─', pw - 2) + "┘", border, bg);

            int ix = x0 + 1, iy = y0 + 1, iw = pw - 2, ih = ph - 4;   // área do esqueleto
            if (tracker.Failed || !tracker.Ready)
            {
                WrapText(ix + 1, iy + 2, iw - 2, tracker.Status, tracker.Failed ? 0xFF8A8A : 0xB0B4D8, bg);
                return;
            }
            if (frame.Hands.Count == 0)
            {
                rend.Put(ix + 2, iy + 3, "mostre a mão para", 0xB0B4D8, bg);
                rend.Put(ix + 2, iy + 4, "a câmera", 0xB0B4D8, bg);
            }

            foreach (var h in frame.Hands)
            {
                for (int b = 0; b < Bones.Length; b++)
                    for (int k = 0; k + 1 < Bones[b].Length; k++)
                    {
                        int p = Bones[b][k], q = Bones[b][k + 1];
                        DrawLine(ix + (int)(h.Pts[2 * p] * iw), iy + (int)(h.Pts[2 * p + 1] * ih),
                                 ix + (int)(h.Pts[2 * q] * iw), iy + (int)(h.Pts[2 * q + 1] * ih),
                                 '·', 0x6F86C8, ix, iy, ix + iw - 1, iy + ih - 1);
                    }
                for (int j = 0; j < 21; j++)
                {
                    int px = ix + (int)(h.Pts[2 * j] * iw), py = iy + (int)(h.Pts[2 * j + 1] * ih);
                    if (px >= ix && px < ix + iw && py >= iy && py < iy + ih)
                        rend.Dot(px, py, '•', j % 4 == 0 && j > 0 ? 0xFFD27A : 0xC8D4FF);
                }
            }

            // Pose atual + barra de confirmação
            // Dicas quando a câmera não consegue decidir: mão longe, cortada ou pose ambígua
            string hint = null;
            if (main != null && main.Quality == "longe") hint = "aproxime a mão da câmera";
            else if (main != null && main.Quality == "borda") hint = "mão saindo da imagem";
            else if (main != null && main.Pose == "outra" && frame.Duo == "-" && main.Alt != "outra" && main.Alt != null)
                hint = "parece " + PoseLabel(main.Alt) + "? firme a pose";
            if (hint != null)
            {
                rend.Put(ix + 1, y0 + ph - 2, (hint.Length > iw - 2 ? hint.Substring(0, iw - 2) : hint).PadRight(iw - 2), 0xFFB070, bg);
            }

            string label = PoseLabel(holdPose);
            if (main != null && label.Length > 0 && label != "?")
                label += " " + (int)Math.Round(poseConf * 100) + "%";
            int target;
            if (PoseToShape.TryGetValue(holdPose, out target)) label += " → " + shapes[target].Name;
            else if (holdPose == "punho") label += " → espalhar";
            else if (holdPose == "aberta") label += " → girar 3D";
            else if (holdPose == "duas-abertas") label += " → zoom";
            if (label.Length > iw - 2) label = label.Substring(0, iw - 2);
            rend.Put(ix + 1, y0 + ph - 3, label, 0xFFFFFF, bg);
            bool actionable = PoseToShape.ContainsKey(holdPose) || holdPose == "punho";
            int barW = iw - 2;
            if (actionable && !holdFired && WouldChange(holdPose) && LockRemaining > 0)
            {
                // Trava de troca: barra azul enchendo até liberar
                string wait = "aguarde " + LockRemaining.ToString("0.0") + " s ";
                int filled = (int)(barW * (1 - LockRemaining / ShapeLockSeconds));
                rend.Put(ix + 1, y0 + ph - 2, new string('█', filled) + new string('░', barW - filled), 0x6FA8FF, bg);
                rend.Put(ix + 1 + Math.Max(0, barW - wait.Length), y0 + ph - 3, wait, 0x9CC4FF, bg);
            }
            else if (actionable)
            {
                int filled = holdFired ? barW : (int)(barW * Math.Min(1, holdTime / HoldNeeded));
                rend.Put(ix + 1, y0 + ph - 2, new string('█', filled) + new string('░', barW - filled),
                         holdFired ? 0x7CFFB2 : 0xFFD27A, bg);
            }
            else if (LockRemaining > 0)
            {
                string wait = "próxima troca em " + Math.Ceiling(LockRemaining) + " s";
                rend.Put(ix + 1, y0 + ph - 2, wait.PadRight(barW), 0x7F8FB8, bg);
            }
        }

        void WrapText(int x, int y, int width, string text, int fg, int bg)
        {
            string line = "";
            foreach (string word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    rend.Put(x, y++, line, fg, bg);
                    line = "";
                }
                line = line.Length == 0 ? word : line + " " + word;
            }
            if (line.Length > 0) rend.Put(x, y, line, fg, bg);
        }

        void Center(int y, string s, int fg, int bg)
        {
            rend.Put((rend.W - s.Length) / 2, y, s, fg, bg);
        }

        void DrawWelcome()
        {
            int y = rend.H / 2 - 4;
            string[] lines =
            {
                "",
                "   P A R T Í C U L A S   3 D   ",
                "",
                "   Mostre a MÃO para a câmera e segure uma pose        ",
                "   Mão aberta gira o objeto · pinça desenha no ar      ",
                "   (o mouse também funciona: desenhe com o botão esq.) ",
                "",
                "   H = guia de gestos      Q = sair   ",
                "",
            };
            int width = 0;
            foreach (var l in lines) width = Math.Max(width, l.Length);
            for (int i = 0; i < lines.Length; i++)
                Center(y + i, lines[i].PadRight(width), i == 1 ? 0xFFD27A : 0xD0D4F0, 0x10101C);
        }

        void DrawHelp()
        {
            var lines = new List<string>();
            if (helpPage == 1)
            {
                lines.Add("GUIA (1/2) — FORMAS: segure a pose ~meio segundo, ou desenhe o traço");
                lines.Add("");
                lines.Add("  " + "FORMA".PadRight(14) + "POSE DA MÃO".PadRight(22) + "TRAÇO (pinça no ar ou mouse)");
                foreach (var s in shapes)
                    lines.Add(("  " + s.Name + " ").PadRight(15, '.') + " " + s.Hand.PadRight(21) + s.Gesture);
                lines.Add("");
                lines.Add("  H = próxima página");
            }
            else
            {
                lines.Add("GUIA (2/2) — CONTROLES");
                lines.Add("");
                lines.Add("  Mão aberta ............ segura e GIRA o objeto em 3D");
                lines.Add("  Passada rápida → / ← .. próxima / anterior");
                lines.Add("  Duas mãos abertas ..... afaste/junte para dar ZOOM");
                lines.Add("  Pinça (polegar+indic.)  DESENHA no ar; solte para reconhecer");
                lines.Add("  Pinça parada .......... toque: onda de choque");
                lines.Add("  Punho fechado ......... espalha os pontos");
                lines.Add("  Punho → abrir rápido .. EXPLOSÃO");
                lines.Add("");
                lines.Add("  Depois de uma troca, a próxima só é liberada após 4,3 s");
                lines.Add("  (segure a nova pose: ela entra assim que liberar)");
                lines.Add("");
                lines.Add("  Traços: linha → ← troca, ↓ espalha, ↑ reúne");
                lines.Add("  Mouse: botão esquerdo desenha, direito = onda, roda = zoom");
                lines.Add("");
                lines.Add("  H fechar     Q ou Esc sair");
            }

            int width = 0;
            foreach (var l in lines) width = Math.Max(width, l.Length);
            width += 4;
            int x0 = Math.Max(0, (rend.W - width) / 2);
            int y0 = Math.Max(0, (rend.H - 1 - (lines.Count + 2)) / 2);
            int bg = 0x12121E, border = 0x6A6AA0;

            rend.Put(x0, y0, "┌" + new string('─', width - 2) + "┐", border, bg);
            for (int i = 0; i < lines.Count; i++)
            {
                rend.Put(x0, y0 + 1 + i, "│", border, bg);
                rend.Put(x0 + 1, y0 + 1 + i, (" " + lines[i]).PadRight(width - 2), i == 0 ? 0xFFD27A : 0xD8DCF5, bg);
                rend.Put(x0 + width - 1, y0 + 1 + i, "│", border, bg);
            }
            rend.Put(x0, y0 + lines.Count + 1, "└" + new string('─', width - 2) + "┘", border, bg);
        }

        void DrawToast(int rows)
        {
            Center(rows - 2, "  " + toast + "  ", toastColor, 0x1A1A2A);
        }

        void DrawStatus()
        {
            int y = rend.H - 1, bg = 0x191928;
            string shapeName = current >= 0 ? shapes[current].Name : "pontos soltos";
            string left = " PARTÍCULAS 3D │ " + shapeName + " │ " + tracker.Status + " │ " + (int)Math.Round(fps) + " fps";
            string right = "H guia · Q sair ";
            string line = left;
            if (left.Length + right.Length + 2 <= rend.W) line = left + new string(' ', rend.W - left.Length - right.Length) + right;
            rend.Put(0, y, line.PadRight(rend.W), 0xA8ACD0, bg);
            rend.Put(1, y, "PARTÍCULAS 3D", 0xFFD27A, bg);
        }

        // ---- modos de diagnóstico ----------------------------------------------------
        public static void Preview()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var shapes = Shapes.All();
            var sim = new Sim(ParticleCount);
            var r = new Renderer();
            r.Resize(70, 26);
            foreach (var s in shapes)
            {
                sim.SetShape(s, true);
                sim.Snap(s, 1.0);
                r.Rasterize(sim, true, 26, s.Spin > 0 ? 0.5 : 0.25, s.Tilt, 1.0, 1.0, 0.3);
                r.Compose(true, 1.0);
                Console.WriteLine("=== " + s.Name + " (mão: " + s.Hand + " | traço: " + s.Gesture + ") ===");
                Console.Write(r.ToPlain(26));
            }
        }

        // Confere a saída diferencial: um "terminal virtual" interpreta os bytes enviados e,
        // a cada quadro, a tela resultante tem de ser igual ao que o renderizador pretendia
        // (mesmos caracteres; cores dentro da tolerância de reaproveitamento).
        public static int VerifyRender()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var shapes = Shapes.All();
            var sim = new Sim(ParticleCount);
            var r = new Renderer();
            const int W = 120, H = 36;
            r.Resize(W, H);
            var screen = new char[W * H];
            var color = new int[W * H];
            for (int i = 0; i < screen.Length; i++) screen[i] = ' ';
            int frames = 0, glyphErrors = 0, colorErrors = 0, worst = 0;
            double t = 0, yaw = 0;
            foreach (var s in shapes)
            {
                sim.SetShape(s, false);
                for (int f = 0; f < 60; f++, frames++)
                {
                    t += Dt; yaw += 0.55 * Dt;
                    sim.Step(t, s);
                    r.Rasterize(sim, f % 2 == 0, H - 1, yaw, s.Tilt, 1.0, t, t * 0.3);   // alterna Braille/pontos; céu andando
                    r.Compose(f % 2 == 0, 1.0);
                    if (f % 7 == 0) r.Put(10, 5, "texto sobreposto " + f, 0xFFFFFF, 0x202030);
                    var ms = new MemoryStream();
                    r.Present(ms);
                    Emulate(ms.ToArray(), screen, color, W);
                    for (int c = 0; c < W * H - 1; c++)
                    {
                        char want = r.Glyph[c] == '⠀' ? ' ' : r.Glyph[c];
                        char got = screen[c] == '⠀' ? ' ' : screen[c];
                        if (want != got) { glyphErrors++; continue; }
                        if (want == ' ') continue;
                        int a = r.Fg[c], b = color[c];
                        int d = Math.Max(Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)),
                                Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                        worst = Math.Max(worst, d);
                        if (d > 48) colorErrors++;
                    }
                }
            }
            Console.WriteLine("quadros verificados: " + frames + " (" + W + "x" + H + ")");
            Console.WriteLine("caracteres errados: " + glyphErrors + "   cores fora da tolerância: " + colorErrors +
                              "   maior desvio de cor: " + worst);
            return glyphErrors == 0 && colorErrors == 0 ? 0 : 1;
        }

        // Interpretador mínimo de VT: posição do cursor (CSI r;c H), cor de frente (CSI 38;2;r;g;b m) e texto UTF-8
        static void Emulate(byte[] data, char[] screen, int[] color, int w)
        {
            int x = 0, y = 0, fg = 0, i = 0;
            string text = Encoding.UTF8.GetString(data);
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    int j = i + 2;
                    while (j < text.Length && (char.IsDigit(text[j]) || text[j] == ';')) j++;
                    string[] args = text.Substring(i + 2, j - i - 2).Split(';');
                    char cmd = text[j];
                    if (cmd == 'H') { y = int.Parse(args[0]) - 1; x = int.Parse(args[1]) - 1; }
                    else if (cmd == 'm' && args.Length == 5 && args[0] == "38")
                        fg = (int.Parse(args[2]) << 16) | (int.Parse(args[3]) << 8) | int.Parse(args[4]);
                    i = j + 1;
                    continue;
                }
                int c = y * w + x;
                if (c >= 0 && c < screen.Length) { screen[c] = ch; color[c] = fg; }
                x++;
                i++;
            }
        }

        // Exporta quadros reais para um PNG, desenhando cada célula como o terminal mostra
        // (pontos Braille na cor de frente sobre a cor de fundo), para avaliar a aparência final.
        public static void ExportPng(string path)
        {
            var shapes = Shapes.All();
            var sim = new Sim(ParticleCount);
            var r = new Renderer();
            const int W = 120, rows = 36, cw = 8, ch = 16, cols = 2, gap = 10, caption = 18;
            r.Resize(W, rows + 1);
            int[] pick = { Shapes.Heart_, Shapes.Planet_, Shapes.Atom_, Shapes.Earth_, Shapes.Cube_, Shapes.Galaxy_ };
            int panelW = W * cw, panelH = rows * ch + caption;
            int lines = (pick.Length + cols - 1) / cols;
            using (var bmp = new System.Drawing.Bitmap(cols * panelW + (cols + 1) * gap, lines * panelH + (lines + 1) * gap))
            using (var gr = System.Drawing.Graphics.FromImage(bmp))
            using (var font = new System.Drawing.Font("Segoe UI", 10f))
            {
                gr.Clear(System.Drawing.Color.FromArgb(24, 24, 28));
                gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                for (int k = 0; k < pick.Length; k++)
                {
                    Shape s = shapes[pick[k]];
                    sim.SetShape(s, false);
                    double t = 0;
                    for (int f = 0; f < 120; f++) { t += Dt; sim.Step(t, s); }
                    r.Rasterize(sim, true, rows, s.Spin > 0 ? 0.6 : 0.25, s.Tilt, 1.0, t, 1.1 + pick[k] * 0.35);
                    r.Compose(true, 1.0);
                    int ox = gap + (k % cols) * (panelW + gap), oy = gap + (k / cols) * (panelH + gap);
                    gr.DrawString(s.Name, font, System.Drawing.Brushes.Gainsboro, ox, oy);
                    oy += caption;
                    gr.FillRectangle(System.Drawing.Brushes.Black, ox, oy, panelW, rows * ch);
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < W; x++)
                        {
                            int c = y * W + x, bg = r.Bg[c];
                            if (bg >= 0)
                                using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(bg >> 16 & 255, bg >> 8 & 255, bg & 255)))
                                    gr.FillRectangle(b, ox + x * cw, oy + y * ch, cw, ch);
                            char g = r.Glyph[c];
                            if (g < '⠀' || g > '⣿') continue;
                            int bits = g - 0x2800, fg = r.Fg[c];
                            if (bits == 0) continue;
                            using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(fg >> 16 & 255, fg >> 8 & 255, fg & 255)))
                                for (int dy = 0; dy < 4; dy++)
                                    for (int dx = 0; dx < 2; dx++)
                                    {
                                        int bit = dy == 3 ? (dx == 0 ? 0x40 : 0x80) : 1 << (dy + dx * 3);
                                        if ((bits & bit) == 0) continue;
                                        gr.FillEllipse(b, ox + x * cw + 1.2f + dx * 3.6f, oy + y * ch + 1.6f + dy * 3.6f, 2.6f, 2.6f);
                                    }
                        }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("prévia salva em " + path);
        }

        // Mede se o céu de fundo atrapalha a figura: contraste, estrelas no contorno e nos vãos.
        // Cada forma é vista com o céu em várias posições (a Via Láctea e a Andrômeda passam atrás dela).
        public static int VerifyBackground()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var shapes = Shapes.All();
            var sim = new Sim(ParticleCount);
            var r = new Renderer();
            const int W = 160, H = 45, rows = H - 1, views = 48;
            r.Resize(W, H);
            long obj = 0, low = 0, dim = 0, halo = 0, inner = 0, frames = 0;
            double worstLow = 0, nebMax = 0;
            string worstShape = "";
            Console.WriteLine("forma          contraste ruim pelo fundo  estrelas coladas/quadro  estrelas nos vãos/quadro  nebulosa atrás  (sombras)");
            foreach (var s in shapes)
            {
                sim.SetShape(s, false);
                double t = 0;
                for (int k = 0; k < 90; k++) { t += Dt; sim.Step(t, s); }   // deixa a forma se montar
                long o = 0, l = 0, dm = 0, h = 0, n = 0;
                double neb = 0;
                for (int v = 0; v < views; v++)
                {
                    t += Dt; sim.Step(t, s);
                    double yaw = v * 0.37, sky = v * 2 * Math.PI / views;
                    r.Rasterize(sim, true, rows, yaw, s.Tilt, 1.0, t, sky);
                    r.Compose(true, 1.0);
                    Renderer.BackgroundStats st = r.MeasureBackground(rows);
                    o += st.ObjectCells; l += st.LowContrast; dm += st.DimCells; h += st.HaloStars; n += st.InnerStars;
                    neb += st.NebulaBehind;
                    nebMax = Math.Max(nebMax, st.NebulaBehind);
                }
                double pct = 100.0 * l / Math.Max(1, o);
                if (pct > worstLow) { worstLow = pct; worstShape = s.Name; }
                Console.WriteLine(s.Name.PadRight(15) + (pct.ToString("0.0") + "%").PadLeft(25) +
                                  ((double)h / views).ToString("0.0").PadLeft(25) + ((double)n / views).ToString("0.0").PadLeft(26) +
                                  (neb / views).ToString("0.0").PadLeft(16) + ((100.0 * dm / Math.Max(1, o)).ToString("0") + "%").PadLeft(11));
                obj += o; low += l; dim += dm; halo += h; inner += n; frames += views;
            }
            double lowPct = 100.0 * low / Math.Max(1, obj);
            Console.WriteLine();
            Console.WriteLine("TOTAL: contraste ruim causado pelo fundo em " + lowPct.ToString("0.00") + "% das células da figura (pior: " +
                              worstShape + " " + worstLow.ToString("0.0") + "%)");
            Console.WriteLine("       estrelas coladas no contorno: " + ((double)halo / frames).ToString("0.0") +
                              "/quadro   estrelas nos vãos: " + ((double)inner / frames).ToString("0.0") + "/quadro");
            Console.WriteLine("       (sombras intencionais, pontos escuros sobre preto: " + (100.0 * dim / Math.Max(1, obj)).ToString("0") + "% — não é interferência)");
            bool ok = lowPct < 1.0 && halo == 0 && (double)inner / frames < 2.0;
            Console.WriteLine(ok ? "RESULTADO: o fundo não interfere na figura" : "RESULTADO: o fundo interfere na figura");
            return ok ? 0 : 1;
        }

        // Mede o custo por quadro (simulação + renderização) e o volume enviado ao terminal
        public static void Benchmark()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var shapes = Shapes.All();
            var sim = new Sim(ParticleCount);
            var r = new Renderer();
            const int W = 160, H = 45, frames = 120;
            r.Resize(W, H);
            var sw = new Stopwatch();
            double simMs = 0, renMs = 0;
            long diffBytes = 0, fullBytes = 0;
            int total = 0;
            double t = 0, yaw = 0;
            foreach (var s in shapes)
            {
                sim.SetShape(s, false);
                for (int f = 0; f < frames; f++)
                {
                    t += Dt; yaw += 0.55 * Dt;
                    sw.Restart(); sim.Step(t, s); sw.Stop(); simMs += sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    r.Rasterize(sim, true, H - 1, yaw, s.Tilt, 1.0, t, t * SkyDrift);
                    r.Compose(true, 1.0);
                    r.Present(Stream.Null);
                    sw.Stop(); renMs += sw.Elapsed.TotalMilliseconds;
                    diffBytes += r.LastBytes;
                    if (f % 10 == 0)
                    {
                        r.ForceFullRedraw();
                        r.Present(Stream.Null);
                        fullBytes += r.LastBytes * 10L;
                    }
                    total++;
                }
            }
            Console.WriteLine("partículas: " + ParticleCount + "   tela: " + W + "x" + H + " (Braille)   quadros: " + total);
            Console.WriteLine("simulação:   " + (simMs / total).ToString("0.000") + " ms/quadro");
            Console.WriteLine("renderização:" + (renMs / total).ToString("0.000").PadLeft(6) + " ms/quadro (projeção, luz, oclusão, saída)");
            Console.WriteLine("saída diferencial: " + (diffBytes / total / 1024.0).ToString("0.0") + " KB/quadro");
            Console.WriteLine("saída completa:    " + (fullBytes / total / 1024.0).ToString("0.0") + " KB/quadro");
            Console.WriteLine("economia de banda para o terminal: " + (100.0 * (1 - (double)diffBytes / fullBytes)).ToString("0") + "%");
        }
    }
}
