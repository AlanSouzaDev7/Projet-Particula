using System;
using System.IO;
using System.Text;

namespace Particulas
{
    // Projeta as partículas em células de texto (Braille 2x4 ou pontos), com:
    //  - iluminação por normal (difusa + especular) e neblina de profundidade;
    //  - oclusão por célula (o que está atrás de uma superfície quase não aparece);
    //  - pontilhado por sombreamento (áreas iluminadas com mais pontos, sombras com menos);
    //  - céu de fundo: Via Láctea com nebulosa, galáxia distante e estrelas coloridas;
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
        int[] starColor, nebula;
        byte[] near;

        // Rascunho por partícula (evita projetar duas vezes)
        int[] pCell, pBit;
        float[] pZ, pR, pG, pB;

        byte[] buf = new byte[1 << 16];
        int len;

        // ---- céu de fundo: estrelas num "panorama" (azimute x elevação) ----
        // A Via Láctea é uma faixa senoidal que dá a volta no céu; a câmera vê ~1,6 rad dela.
        // O céu só se move quando a mão gira o objeto (paralaxe) e numa deriva bem lenta:
        // estrelas paradas não precisam ser reenviadas ao terminal a cada quadro.
        const double SkyFov = 1.6;
        const double AndromedaPhi = 2.4, AndromedaE = 0.52;
        readonly int starCount;
        readonly double[] stPhi, stE, stB, stPh;
        readonly int[] stCol;
        readonly bool[] stTwinkle;

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
            const int band = 2600, field = 700, andromeda = 260;
            starCount = band + field + andromeda;
            stPhi = new double[starCount]; stE = new double[starCount]; stB = new double[starCount];
            stPh = new double[starCount]; stCol = new int[starCount]; stTwinkle = new bool[starCount];
            for (int i = 0; i < starCount; i++)
            {
                double phi, e, b;
                if (i < band)
                {
                    phi = rnd.NextDouble() * 2 * Math.PI;
                    e = BandCenter(phi) + rnd.Gauss() * (rnd.NextDouble() < 0.7 ? 0.07 : 0.17);
                    b = 0.3 + 0.7 * Math.Pow(rnd.NextDouble(), 3);        // muitas fracas, poucas fortes
                }
                else if (i < band + field)
                {
                    phi = rnd.NextDouble() * 2 * Math.PI;
                    e = rnd.NextDouble() * 1.9 - 0.95;
                    b = 0.35 + 0.65 * Math.Pow(rnd.NextDouble(), 2);
                }
                else
                {
                    // Galáxia distante (tipo Andrômeda): elipse inclinada com núcleo denso
                    double u = rnd.Gauss() * 0.06, v = rnd.Gauss() * 0.018;
                    phi = AndromedaPhi + u * 0.87 - v * 0.5;
                    e = AndromedaE + u * 0.5 * 0.55 + v * 0.87;
                    b = 0.35 + 0.5 * Math.Exp(-(u * u) / 0.002);
                }
                stPhi[i] = phi; stE[i] = e; stB[i] = b;
                stPh[i] = rnd.NextDouble();
                stTwinkle[i] = rnd.NextDouble() < 0.3;
                double t = rnd.NextDouble();                               // temperatura da estrela
                stCol[i] = t < 0.55 ? 0xC8D7FF : (t < 0.8 ? 0xFFF0D8 : (t < 0.92 ? 0xFFCF9A : 0x9FBFFF));
            }
        }

        static double BandCenter(double phi)
        {
            return 0.32 * Math.Sin(phi + 0.9) + 0.05 * Math.Sin(3 * phi);
        }

        static double WrapPi(double a)
        {
            a %= 2 * Math.PI;
            if (a > Math.PI) a -= 2 * Math.PI;
            if (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        // Brilho da nebulosa (cor de fundo das células) num ponto do céu. Fica larga, suave e
        // escura de propósito: o fundo de cada célula é um retângulo, e variações fortes ou
        // detalhes finos apareceriam como blocos. Quem desenha a Via Láctea são as estrelas.
        static int NebulaColor(double phi, double e)
        {
            double d = (e - BandCenter(phi)) / 0.22;
            double tex = 0.8 + 0.12 * Math.Sin(phi * 3 + Math.Sin(e * 4)) + 0.08 * Math.Sin(phi * 7 - e * 5);
            double band = Math.Exp(-d * d) * tex;
            double core = Math.Exp(-d * d * 2.5) * (0.5 + 0.5 * Math.Sin(phi * 2 + 1));
            double r = 16 * band + 9 * core, g = 10 * band + 6 * core, b = 26 * band + 4 * core;
            if (r + g + b < 9) return -1;
            // Quantizado em degraus de 3: a nebulosa muda devagar e não gera tráfego à toa
            return ((int)r / 3 * 3 << 16) | ((int)g / 3 * 3 << 8) | ((int)b / 3 * 3);
        }

        public void Resize(int w, int h)
        {
            W = Math.Max(1, w); H = Math.Max(1, h);
            int n = W * H;
            Glyph = new char[n]; Fg = new int[n]; Bg = new int[n];
            prevGlyph = new char[n]; prevFg = new int[n]; prevBg = new int[n];
            cnt = new int[n]; bits = new int[n]; star = new byte[n]; starColor = new int[n]; nebula = new int[n]; near = new byte[n];
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

        public void Rasterize(Sim sim, bool braille, int rows, double yaw, double tilt, double zoom, double time, double sky)
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
                if (diff < 0) diff = -diff * 0.3;                           // luz de preenchimento por trás
                double sp = nx1 * Hx + ny2 * Hy + nz2 * Hz;
                sp = sp > 0 ? sp * sp : 0; sp *= sp; sp *= sp; sp *= sp;     // ^16
                // Luz de contorno: superfícies "de lado" para a câmera acendem a silhueta
                double facing = nz2 < 0 ? -nz2 : nz2, rim = 1 - facing;
                rim = rim * rim * rim;
                double depth = (z2 + 0.6) / 1.8;
                double fog = 1 - 0.3 * (depth < 0 ? 0 : (depth > 1 ? 1 : depth));
                double shade = (0.44 + 0.56 * diff + 0.5 * rim) * fog, spec = 190 * sp * fog, glow = 45 * rim * fog;
                pR[i] = (float)(sim.R[i] * shade + spec + glow);
                pG[i] = (float)(sim.G[i] * shade + spec + glow);
                pB[i] = (float)(sim.B[i] * shade + spec + glow);
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

            // "Respiro" em volta da figura: distância (em anéis) de cada célula até a figura.
            // 0 = figura, 1 = colada, 2 = perto, 3 = transição, 4 = longe. O céu se apaga aos
            // poucos ao chegar perto, para a silhueta ficar limpa sem um "corte" no fundo.
            for (int c = 0; c < near.Length; c++) near[c] = 4;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < W; x++)
                {
                    if (cnt[y * W + x] == 0) continue;
                    for (int dy = -3; dy <= 3; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= rows) continue;
                        for (int dx = -7; dx <= 7; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= W) continue;
                            // células têm ~1:2 (largura:altura): 2 colunas valem 1 linha
                            double d = Math.Sqrt(dx * dx * 0.25 + dy * dy);
                            byte lv = d == 0 ? (byte)0 : (d <= 1.2 ? (byte)1 : (d <= 2.3 ? (byte)2 : (d <= 3.5 ? (byte)3 : (byte)4)));
                            int cc = yy * W + xx;
                            if (lv < near[cc]) near[cc] = lv;
                        }
                    }
                }

            // Vãos internos de figuras abertas (entre órbitas, entre braços da galáxia): figura
            // dos dois lados na mesma linha, perto. Ali o céu fica a meia-luz, como "perto".
            const int span = 10;
            for (int y = 0; y < rows; y++)
            {
                int row = y * W, lastLeft = -1000;
                for (int x = 0; x < W; x++)
                {
                    if (cnt[row + x] > 0) { lastLeft = x; continue; }
                    if (x - lastLeft > span || near[row + x] <= 2) continue;
                    for (int d = 1; d <= span && x + d < W; d++)
                        if (cnt[row + x + d] > 0) { near[row + x] = 2; break; }
                }
            }

            // Céu: a câmera aponta para o azimute "sky" e vê SkyFov radianos para os lados
            const double eScale = 0.55;                                    // elevação ±1 cobre a tela
            for (int i = 0; i < starCount; i++)
            {
                double u = WrapPi(stPhi[i] - sky) / SkyFov + 0.5;
                if (u < 0 || u >= 1) continue;
                double v = 0.5 - stE[i] * eScale;
                if (v < 0 || v >= 1) continue;
                int ix = (int)(u * pw), iy = (int)(v * ph);
                int cell = (iy / sy) * W + ix / sx;
                if (near[cell] <= 1) continue;                             // nada de estrela colada na figura
                double b = stB[i];
                if (near[cell] == 2) b *= 0.4; else if (near[cell] == 3) b *= 0.7;
                // Só parte das estrelas cintila, em poucos níveis (nitidez e pouco tráfego)
                if (stTwinkle[i]) b *= 0.8 + 0.2 * Math.Round(1 + Math.Sin(time * (0.7 + stPh[i] * 2) + stPh[i] * 40));
                byte lv = (byte)Math.Min(255, 255 * b);
                if (lv > star[cell]) { star[cell] = lv; starColor[cell] = stCol[i]; }
                if (braille) bits[cell] |= BrailleBit(ix % 2, iy % 4);
            }

            // Nebulosa: cor de fundo de cada célula (inclusive atrás das figuras), lida de um
            // panorama pré-calculado do céu — o quadro só desloca o índice de azimute
            if (skyRows != rows) BuildSkyTable(rows, eScale);
            double cellsToSteps = SkySteps / (2 * Math.PI);
            for (int x = 0; x < W; x++)
            {
                double phi = sky + ((x + 0.5) / W - 0.5) * SkyFov;
                int col = (int)(phi * cellsToSteps) % SkySteps;
                if (col < 0) col += SkySteps;
                int src = col * skyRows;
                for (int y = 0; y < rows; y++)
                {
                    int c = y * W + x, nb = skyTable[src + y], lv = near[c];
                    // Atrás da figura e colado nela: preto; perto: nebulosa a meia força
                    if (nb < 0 || lv <= 1) nb = -1;
                    else if (lv <= 3)
                    {
                        int k = lv == 2 ? 3 : 7;                            // 30% e 70% da força
                        nb = ((nb >> 16 & 255) * k / 10 << 16) | ((nb >> 8 & 255) * k / 10 << 8) | ((nb & 255) * k / 10);
                        if (nb == 0) nb = -1;
                    }
                    nebula[c] = nb;
                }
                for (int y = rows; y < H; y++) nebula[y * W + x] = -1;
            }
        }

        const int SkySteps = 1536;
        int[] skyTable;
        int skyRows = -1;

        void BuildSkyTable(int rows, double eScale)
        {
            skyRows = rows;
            skyTable = new int[SkySteps * rows];
            for (int col = 0; col < SkySteps; col++)
            {
                double phi = col * 2 * Math.PI / SkySteps;
                for (int y = 0; y < rows; y++)
                    skyTable[col * rows + y] = NebulaColor(phi, (0.5 - (y + 0.5) / rows) / eScale);
            }
        }

        static int BrailleBit(int dx, int dy)
        {
            if (dy == 3) return dx == 0 ? 0x40 : 0x80;
            return 1 << (dy + dx * 3);
        }

        // Ordem em que os 8 pontos de uma célula Braille são mantidos ao "ralear" (espalhados
        // como numa matriz de Bayer, para o pontilhado ficar uniforme e não em listras)
        static readonly int[] DitherOrder = { 0x01, 0x20, 0x08, 0x04, 0x02, 0x80, 0x10, 0x40 };

        static int PopCount(int v)
        {
            v -= (v >> 1) & 0x55;
            v = (v & 0x33) + ((v >> 2) & 0x33);
            return (v + (v >> 4)) & 0x0F;
        }

        public void Compose(bool braille, double gain)
        {
            for (int c = 0; c < cnt.Length; c++)
            {
                Bg[c] = nebula[c];
                int n = cnt[c];
                if (n == 0)
                {
                    int s = star[c];
                    if (s > 0)
                    {
                        int sc = starColor[c];
                        double k = s / 255.0;
                        Glyph[c] = braille ? (char)(0x2800 + bits[c]) : (s > 170 ? '•' : '·');
                        Fg[c] = Rgb(((sc >> 16) & 255) * k, ((sc >> 8) & 255) * k, (sc & 255) * k);
                    }
                    else { Glyph[c] = ' '; Fg[c] = 0; }
                    continue;
                }
                double boost = Math.Min(1.3, 0.85 + 0.06 * n) * gain / wsum[c];
                double r = ar[c] * boost, g = ag[c] * boost, b = ab[c] * boost;
                double lum = Math.Min(1.0, (0.3 * r + 0.59 * g + 0.11 * b) / 255.0);
                if (braille)
                {
                    // Pontilhado por sombreamento: quantos pontos ficam acesos segue a luz da célula
                    int bt = bits[c], have = PopCount(bt);
                    int target = Math.Max(2, Math.Min(8, (int)Math.Ceiling(8 * (0.22 + 0.78 * lum))));
                    if (have > target)
                    {
                        int kept = 0, start = ((c % W) * 3 + (c / W) * 5) & 7;
                        for (int j = 0; j < 8 && PopCount(kept) < target; j++)
                        {
                            int bit = DitherOrder[(start + j) & 7];
                            if ((bt & bit) != 0) kept |= bit;
                        }
                        bt = kept;
                    }
                    Glyph[c] = (char)(0x2800 + bt);
                }
                else Glyph[c] = n == 1 || lum < 0.3 ? '·' : (lum < 0.62 ? '•' : '●');
                Fg[c] = Rgb(r, g, b);
            }
        }

        // ---- diagnóstico: o fundo atrapalha a figura? ---------------------------------
        public struct BackgroundStats
        {
            public int ObjectCells, LowContrast, DimCells, HaloStars, InnerStars;
            public double NebulaBehind;
        }

        static double LinearLum(int rgb)
        {
            if (rgb < 0) return 0;
            double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
            r = r <= 0.04045 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.04045 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.04045 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        // Mede, no quadro já composto: contraste dos pontos da figura contra a cor de fundo
        // (WCAG < 3:1 = ruim), estrelas encostadas no contorno, estrelas nos vãos internos da
        // figura e o brilho médio da nebulosa atrás dela.
        public BackgroundStats MeasureBackground(int rows)
        {
            var s = new BackgroundStats();
            double nebSum = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < W; x++)
                {
                    int c = y * W + x;
                    if (cnt[c] > 0)
                    {
                        s.ObjectCells++;
                        double lf = LinearLum(Fg[c]), lb = LinearLum(Bg[c]);
                        // Ponto escuro sobre fundo preto é sombra de propósito; só conta como
                        // interferência quando há cor de fundo (nebulosa) atrás do ponto.
                        if ((lf + 0.05) / (lb + 0.05) < 3.0)
                        {
                            s.DimCells++;
                            if (Bg[c] >= 0) s.LowContrast++;
                        }
                        nebSum += Bg[c] < 0 ? 0 : (((Bg[c] >> 16) & 255) + ((Bg[c] >> 8) & 255) + (Bg[c] & 255)) / 3.0;
                        continue;
                    }
                    if (star[c] == 0 || Glyph[c] == ' ') continue;
                    // Colada = a até ~1 linha da figura (mesma medida do "respiro": 2 colunas = 1 linha)
                    bool touching = false;
                    for (int dy = -1; dy <= 1 && !touching; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (dx * dx * 0.25 + dy * dy > 1.44) continue;
                            if (xx >= 0 && xx < W && yy >= 0 && yy < rows && cnt[yy * W + xx] > 0) { touching = true; break; }
                        }
                    if (touching) { s.HaloStars++; continue; }
                    // Vão interno: há figura à esquerda e à direita na mesma linha, perto
                    bool left = false, right = false;
                    for (int d = 1; d <= 8; d++)
                    {
                        if (x - d >= 0 && cnt[c - d] > 0) left = true;
                        if (x + d < W && cnt[c + d] > 0) right = true;
                    }
                    if (left && right && star[c] > 110) s.InnerStars++;      // só estrelas bem visíveis
                }
            s.NebulaBehind = s.ObjectCells > 0 ? nebSum / s.ObjectCells : 0;
            return s;
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
                    if (!full && g == prevGlyph[c] && SameBg(bg, prevBg[c]) &&
                        (fg < 0 ? prevFg[c] < 0 : prevFg[c] >= 0 && Close(fg, prevFg[c], SkipTolerance))) continue;

                    if (y != lastY || x != lastX + 1)
                    {
                        Csi(); Num(y + 1); Byte((byte)';'); Num(x + 1); Byte((byte)'H');
                    }
                    if (!SameBg(bg, curBg))
                    {
                        if (bg < 0) { Csi(); Byte((byte)'4'); Byte((byte)'9'); Byte((byte)'m'); }
                        else Color(48, bg);
                        curBg = bg;
                    }
                    bg = curBg;
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

        const int SkipTolerance = 28, ReuseTolerance = 20, BgTolerance = 10;

        // Fundos "iguais": ambos sem cor, ou ambos com cor e quase idênticos (nebulosa suave)
        static bool SameBg(int a, int b)
        {
            if (a < 0 || b < 0) return a == b;
            return Close(a, b, BgTolerance);
        }

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

