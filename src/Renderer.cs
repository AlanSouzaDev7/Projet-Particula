using System;
using System.IO;
using System.Text;

namespace Particulas
{
    // Projeta as partículas em células de texto (Braille 2x4 ou pontos), com:
    //  - iluminação por normal (difusa + especular) e neblina de profundidade;
    //  - oclusão por célula (o que está atrás de uma superfície quase não aparece);
    //  - campo de estrelas ao fundo com paralaxe;
    //  - saída diferencial: só as células que mudaram desde o último quadro vão para o terminal.
    sealed class Renderer
    {
        public int W, H;
        public char[] Glyph;
        public int[] Fg, Bg;
        public int LastBytes;

        char[] prevGlyph;
        int[] prevFg, prevBg;
        bool full = true;

        int[] cnt, bits;
        float[] ar, ag, ab, wsum, zmin;
        byte[] star;

        // Rascunho por partícula (evita projetar duas vezes)
        int[] pCell, pBit;
        float[] pZ, pR, pG, pB;

        byte[] buf = new byte[1 << 16];
        int len;

        const int StarCount = 260;
        readonly double[] stX = new double[StarCount], stY = new double[StarCount],
                          stZ = new double[StarCount], stPh = new double[StarCount];

        // Luz vinda de cima/esquerda/frente, em espaço de câmera (z negativo = em direção ao observador)
        static readonly double Lx, Ly, Lz, Hx, Hy, Hz;

        static Renderer()
        {
            double lx = -0.45, ly = 0.65, lz = -0.62, l = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            Lx = lx / l; Ly = ly / l; Lz = lz / l;
            double hx = Lx, hy = Ly, hz = Lz - 1, h = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            Hx = hx / h; Hy = hy / h; Hz = hz / h;
        }

        public Renderer()
        {
            var rnd = new FastRandom(7);
            for (int i = 0; i < StarCount; i++)
            {
                stX[i] = (rnd.NextDouble() * 2 - 1) * 9;
                stY[i] = (rnd.NextDouble() * 2 - 1) * 5;
                stZ[i] = (rnd.NextDouble() * 2 - 1) * 9;
                stPh[i] = rnd.NextDouble();
            }
        }

        public void Resize(int w, int h)
        {
            W = Math.Max(1, w); H = Math.Max(1, h);
            int n = W * H;
            Glyph = new char[n]; Fg = new int[n]; Bg = new int[n];
            prevGlyph = new char[n]; prevFg = new int[n]; prevBg = new int[n];
            cnt = new int[n]; bits = new int[n]; star = new byte[n];
            ar = new float[n]; ag = new float[n]; ab = new float[n]; wsum = new float[n]; zmin = new float[n];
            full = true;
        }

        public void ForceFullRedraw() { full = true; }

        public static void Metrics(bool braille, out int sx, out int sy, out double aspect)
        {
            sx = braille ? 2 : 1; sy = braille ? 4 : 1;
            aspect = braille ? 1.0 : 2.0;     // altura/largura de um "pixel" (célula ≈ 1:2)
        }

        // Quantos "pixels" valem uma unidade do mundo
        public double PixelsPerUnit(bool braille, int rows, double zoom)
        {
            int sx, sy; double a;
            Metrics(braille, out sx, out sy, out a);
            return Math.Min(W * sx, rows * sy * a) * 0.5 * 0.82 * zoom;
        }

        void EnsureParticleBuffers(int n)
        {
            if (pCell != null && pCell.Length >= n) return;
            pCell = new int[n]; pBit = new int[n];
            pZ = new float[n]; pR = new float[n]; pG = new float[n]; pB = new float[n];
        }

        public void Rasterize(Sim sim, bool braille, int rows, double yaw, double tilt, double zoom, double time)
        {
            Array.Clear(cnt, 0, cnt.Length); Array.Clear(bits, 0, bits.Length); Array.Clear(star, 0, star.Length);
            Array.Clear(ar, 0, ar.Length); Array.Clear(ag, 0, ag.Length); Array.Clear(ab, 0, ab.Length);
            Array.Clear(wsum, 0, wsum.Length);
            for (int c = 0; c < zmin.Length; c++) zmin[c] = float.MaxValue;
            EnsureParticleBuffers(sim.N);

            int sx, sy; double a;
            Metrics(braille, out sx, out sy, out a);
            double pw = W * sx, ph = rows * sy;
            double size = PixelsPerUnit(braille, rows, zoom);
            double cyw = Math.Cos(yaw), syw = Math.Sin(yaw), ct = Math.Cos(tilt), st = Math.Sin(tilt);
            const double cam = 3.2;

            // Passo 1: projeção, iluminação e menor profundidade de cada célula
            for (int i = 0; i < sim.N; i++)
            {
                double x = sim.X[i], y = sim.Y[i], z = sim.Z[i];
                double x1 = x * cyw + z * syw, z1 = -x * syw + z * cyw;   // giro em Y
                double y2 = y * ct + z1 * st, z2 = -y * st + z1 * ct;     // inclinação em X
                double p = cam / Math.Max(0.4, cam + z2);                  // perspectiva
                double px = pw * 0.5 + x1 * size * p, py = ph * 0.5 - y2 * size * p / a;
                sim.SX[i] = px / sx; sim.SY[i] = py / sy;
                if (px < 0 || py < 0 || px >= pw || py >= ph) { pCell[i] = -1; continue; }
                int ix = (int)px, iy = (int)py;
                int cell = (iy / sy) * W + ix / sx;
                pCell[i] = cell;
                pBit[i] = braille ? BrailleBit(ix % 2, iy % 4) : 0;
                pZ[i] = (float)z2;
                if (z2 < zmin[cell]) zmin[cell] = (float)z2;

                double nx = sim.NX[i], ny = sim.NY[i], nz = sim.NZ[i];
                double nx1 = nx * cyw + nz * syw, nz1 = -nx * syw + nz * cyw;
                double ny2 = ny * ct + nz1 * st, nz2 = -ny * st + nz1 * ct;
                double diff = nx1 * Lx + ny2 * Ly + nz2 * Lz;
                if (diff < 0) diff = -diff * 0.25;                          // luz de preenchimento por trás
                double sp = nx1 * Hx + ny2 * Hy + nz2 * Hz;
                sp = sp > 0 ? sp * sp : 0; sp *= sp; sp *= sp; sp *= sp;     // ^16
                double depth = (z2 + 0.6) / 1.8;
                double fog = 1 - 0.4 * (depth < 0 ? 0 : (depth > 1 ? 1 : depth));
                double shade = (0.32 + 0.68 * diff) * fog, spec = 190 * sp * fog;
                pR[i] = (float)(sim.R[i] * shade + spec);
                pG[i] = (float)(sim.G[i] * shade + spec);
                pB[i] = (float)(sim.B[i] * shade + spec);
            }

            // Passo 2: acumula cor ponderada pela proximidade; o que está bem atrás não acende pontos
            for (int i = 0; i < sim.N; i++)
            {
                int cell = pCell[i];
                if (cell < 0) continue;
                float dz = pZ[i] - zmin[cell];
                float w = 1f / (1f + dz * 6f);
                w *= w;
                ar[cell] += pR[i] * w; ag[cell] += pG[i] * w; ab[cell] += pB[i] * w;
                wsum[cell] += w;
                if (dz < 0.35f) { cnt[cell]++; bits[cell] |= pBit[i]; }
            }

            // Estrelas ao fundo: giram devagar com a câmera (paralaxe) e cintilam
            double cs = Math.Cos(yaw * 0.25), ss = Math.Sin(yaw * 0.25), starSize = PixelsPerUnit(braille, rows, 1.0);
            for (int i = 0; i < StarCount; i++)
            {
                double x1 = stX[i] * cs + stZ[i] * ss, z1 = -stX[i] * ss + stZ[i] * cs + 12;
                double p = cam / z1 * 3.5;
                double px = pw * 0.5 + x1 * starSize * p, py = ph * 0.5 - stY[i] * starSize * p / a;
                if (px < 0 || py < 0 || px >= pw || py >= ph) continue;
                int ix = (int)px, iy = (int)py, cell = (iy / sy) * W + ix / sx;
                if (cnt[cell] > 0) continue;
                // Cintilar em 3 níveis: muda poucas vezes por segundo (menos células a reenviar)
                double tw = 0.5 + 0.5 * Math.Sin(time * (0.8 + stPh[i] * 2.5) + stPh[i] * 40);
                byte b = (byte)(45 + 40 * Math.Round(tw * 2));
                if (b > star[cell]) star[cell] = b;
                if (braille) bits[cell] |= BrailleBit(ix % 2, iy % 4);
            }
        }

        static int BrailleBit(int dx, int dy)
        {
            if (dy == 3) return dx == 0 ? 0x40 : 0x80;
            return 1 << (dy + dx * 3);
        }

        public void Compose(bool braille, double gain)
        {
            for (int c = 0; c < cnt.Length; c++)
            {
                Bg[c] = -1;
                int n = cnt[c];
                if (n == 0)
                {
                    int s = star[c];
                    if (s > 0)
                    {
                        Glyph[c] = braille ? (char)(0x2800 + bits[c]) : '·';
                        Fg[c] = Rgb(s * 0.8, s * 0.85, s);
                    }
                    else { Glyph[c] = ' '; Fg[c] = 0; }
                    continue;
                }
                double boost = Math.Min(1.3, 0.85 + 0.12 * n) * gain / wsum[c];
                Fg[c] = Rgb(ar[c] * boost, ag[c] * boost, ab[c] * boost);
                Glyph[c] = braille ? (char)(0x2800 + bits[c]) : (n == 1 ? '·' : (n <= 3 ? '•' : '●'));
            }
        }

        public static int Rgb(double r, double g, double b)
        {
            int ri = r < 0 ? 0 : (r > 255 ? 255 : (int)r);
            int gi = g < 0 ? 0 : (g > 255 ? 255 : (int)g);
            int bi = b < 0 ? 0 : (b > 255 ? 255 : (int)b);
            return (ri << 16) | (gi << 8) | bi;
        }

        public void Put(int x, int y, string s, int fg, int bg)
        {
            if (y < 0 || y >= H) return;
            for (int k = 0; k < s.Length; k++)
            {
                int cx = x + k;
                if (cx < 0 || cx >= W) continue;
                int c = y * W + cx;
                Glyph[c] = s[k]; Fg[c] = fg; Bg[c] = bg;
            }
        }

        public void Dot(int x, int y, char ch, int fg)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            int c = y * W + x;
            Glyph[c] = ch; Fg[c] = fg;
        }

        // ---- saída -------------------------------------------------------------------
        public void Present(Stream stream)
        {
            int need = W * H * 48 + 64;
            if (buf.Length < need) buf = new byte[need];
            len = 0;
            int curFg = -2, curBg = -2, lastX = -9, lastY = -9;
            for (int y = 0; y < H; y++)
            {
                int last = (y == H - 1) ? W - 1 : W;   // evita rolar a tela na última célula
                for (int x = 0; x < last; x++)
                {
                    int c = y * W + x;
                    char g = Glyph[c];
                    bool visible = g != ' ' && g != '⠀';
                    int fg = visible ? Fg[c] : -1;
                    int bg = Bg[c];
                    // Célula igual à que já está na tela (com cor imperceptivelmente diferente): pula
                    if (!full && g == prevGlyph[c] && bg == prevBg[c] &&
                        (fg < 0 ? prevFg[c] < 0 : prevFg[c] >= 0 && Close(fg, prevFg[c], SkipTolerance))) continue;

                    if (y != lastY || x != lastX + 1)
                    {
                        Csi(); Num(y + 1); Byte((byte)';'); Num(x + 1); Byte((byte)'H');
                    }
                    if (bg != curBg)
                    {
                        if (bg < 0) { Csi(); Byte((byte)'4'); Byte((byte)'9'); Byte((byte)'m'); }
                        else Color(48, bg);
                        curBg = bg;
                    }
                    // Vizinhas com cor quase igual reaproveitam a cor atual (menos sequências de cor)
                    if (visible && (curFg < 0 || !Close(fg, curFg, ReuseTolerance))) { Color(38, fg); curFg = fg; }
                    Char(g);
                    prevGlyph[c] = g; prevBg[c] = bg; prevFg[c] = visible ? curFg : -1;
                    lastX = x; lastY = y;
                }
            }
            if (len > 0)
            {
                Csi(); Byte((byte)'0'); Byte((byte)'m');
                stream.Write(buf, 0, len);
                stream.Flush();
            }
            LastBytes = len;
            full = false;
        }

        const int SkipTolerance = 28, ReuseTolerance = 20;

        static bool Close(int a, int b, int tol)
        {
            int dr = ((a >> 16) & 255) - ((b >> 16) & 255), dg = ((a >> 8) & 255) - ((b >> 8) & 255), db = (a & 255) - (b & 255);
            return dr <= tol && dr >= -tol && dg <= tol && dg >= -tol && db <= tol && db >= -tol;
        }


        void Byte(byte b) { buf[len++] = b; }

        void Csi() { buf[len++] = 0x1B; buf[len++] = (byte)'['; }

        void Num(int v)
        {
            if (v >= 100) { buf[len++] = (byte)('0' + v / 100); v %= 100; buf[len++] = (byte)('0' + v / 10); }
            else if (v >= 10) buf[len++] = (byte)('0' + v / 10);
            buf[len++] = (byte)('0' + v % 10);
        }

        void Color(int kind, int rgb)
        {
            Csi(); Num(kind); Byte((byte)';'); Byte((byte)'2'); Byte((byte)';');
            Num((rgb >> 16) & 255); Byte((byte)';'); Num((rgb >> 8) & 255); Byte((byte)';'); Num(rgb & 255);
            Byte((byte)'m');
        }

        void Char(char c)
        {
            if (c < 0x80) buf[len++] = (byte)c;
            else if (c < 0x800)
            {
                buf[len++] = (byte)(0xC0 | (c >> 6));
                buf[len++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                buf[len++] = (byte)(0xE0 | (c >> 12));
                buf[len++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                buf[len++] = (byte)(0x80 | (c & 0x3F));
            }
        }

        public string ToPlain(int rows)
        {
            var o = new StringBuilder();
            for (int y = 0; y < rows; y++)
            {
                o.Append(Glyph, y * W, W);
                o.Append('\n');
            }
            return o.ToString();
        }
    }
}

