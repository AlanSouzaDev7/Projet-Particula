using System;
using System.Collections.Generic;

namespace Particulas
{
    // ---------------------------------------------------------------------------------
    //  Reconhecedor de traços: variante do "$1 Unistroke Recognizer" (Wobbrock et al.),
    //  sensível à orientação, com casamento cíclico para formas fechadas
    //  (o usuário pode começar o círculo/coração/estrela em qualquer ponto e sentido).
    //  Usado tanto pelo mouse quanto pelo "desenho no ar" com a pinça da mão.
    // ---------------------------------------------------------------------------------
    struct P
    {
        public double X, Y;
        public P(double x, double y) { X = x; Y = y; }
    }

    sealed class GestureTemplate
    {
        public string Name;
        public int Action;
        public bool Closed;
        public P[] Pts;
        public int Corners;     // cantos agudos (distingue U de V, ∩ de ^)
    }

    sealed class GestureResult
    {
        public int Action, BestAction;
        public double Score;
        public string Name;
    }

    static class Recognizer
    {
        public const int ActNext = -1, ActPrev = -2, ActScatter = -3, ActRegroup = -4, ActClick = -5, ActNone = -9;
        public const double MinScore = 0.72;
        const int SpiralAction = Shapes.Galaxy_;
        const int NumPoints = 64;
        const double Size = 250.0;
        static readonly double HalfDiag = 0.5 * Math.Sqrt(2 * Size * Size);
        static readonly List<GestureTemplate> templates = new List<GestureTemplate>();
        public static readonly List<KeyValuePair<GestureTemplate, List<P>>> Raw =
            new List<KeyValuePair<GestureTemplate, List<P>>>();

        public static void Add(string name, int action, bool closed, List<P> raw)
        {
            var t = new GestureTemplate { Name = name, Action = action, Closed = closed, Pts = Normalize(raw, closed) };
            t.Corners = closed ? 0 : CountCorners(t.Pts);
            templates.Add(t);
            Raw.Add(new KeyValuePair<GestureTemplate, List<P>>(t, raw));
        }

        static void AddTestOnly(string name, int action, bool closed, List<P> raw)
        {
            var t = new GestureTemplate { Name = name, Action = action, Closed = closed };
            Raw.Add(new KeyValuePair<GestureTemplate, List<P>>(t, raw));
        }

        static List<P> Curve(int count, Func<double, P> f)
        {
            var l = new List<P>(count);
            for (int i = 0; i < count; i++) l.Add(f(i / (double)count));
            return l;
        }

        // Os gabaritos abaixo usam coordenadas matemáticas (y para cima)
        public static void RegisterDefaults()
        {
            templates.Clear(); Raw.Clear();

            Add("coração", Shapes.Heart_, true, Curve(64, u =>
            {
                double t = 2 * Math.PI * u, s = Math.Sin(t);
                return new P(16 * s * s * s, 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
            }));

            Add("círculo", Shapes.Planet_, true, Curve(64, u => new P(Math.Cos(2 * Math.PI * u), Math.Sin(2 * Math.PI * u))));
            Add("círculo duplo", Shapes.Torus_, true,
                Curve(128, u => new P(Math.Cos(4 * Math.PI * u), Math.Sin(4 * Math.PI * u))));

            var pentagram = new List<P>();
            for (int k = 0; k < 5; k++)
            {
                double a = Math.PI / 2 + k * 4 * Math.PI / 5;
                pentagram.Add(new P(Math.Cos(a), Math.Sin(a)));
            }
            Add("estrela", Shapes.Star_, true, pentagram);
            var starOutline = new List<P>();
            for (int k = 0; k < 10; k++)
            {
                double a = Math.PI / 2 + k * Math.PI / 5, r = k % 2 == 0 ? 1.0 : 0.42;
                starOutline.Add(new P(Math.Cos(a) * r, Math.Sin(a) * r));
            }
            Add("estrela", Shapes.Star_, true, starOutline);

            foreach (double turns in new[] { 1.75, 2.5, 3.25 })
            {
                var ccw = new List<P>();
                var cw = new List<P>();
                for (int i = 0; i <= 96; i++)
                {
                    double t = i / 96.0, a = t * turns * 2 * Math.PI, r = 0.06 + t;
                    ccw.Add(new P(Math.Cos(a) * r, Math.Sin(a) * r));
                    cw.Add(new P(-Math.Cos(a) * r, Math.Sin(a) * r));
                }
                // A espiral é reconhecida por geometria (SpiralScore); os gabaritos servem só ao autoteste
                AddTestOnly("espiral", SpiralAction, false, ccw);
                AddTestOnly("espiral", SpiralAction, false, cw);
            }

            var sLetter = new List<P>();
            for (int i = 0; i <= 32; i++)
            {
                double a = (30 + 240.0 * i / 32) * Math.PI / 180;            // arco de cima, anti-horário
                sLetter.Add(new P(Math.Cos(a) * 0.5, 0.5 + Math.Sin(a) * 0.5));
            }
            for (int i = 1; i <= 32; i++)
            {
                double a = (90 - 240.0 * i / 32) * Math.PI / 180;            // arco de baixo, horário
                sLetter.Add(new P(Math.Cos(a) * 0.5, -0.5 + Math.Sin(a) * 0.5));
            }
            Add("S", Shapes.Dna_, false, sLetter);

            Add("quadrado", Shapes.Cube_, true, new List<P> { new P(-1, 1), new P(1, 1), new P(1, -1), new P(-1, -1) });
            Add("losango", Shapes.Diamond_, true, new List<P> { new P(0, 1), new P(0.7, 0), new P(0, -1), new P(-0.7, 0) });
            Add("triângulo", Shapes.Pyramid_, true, new List<P> { new P(0, 1), new P(1, -0.8), new P(-1, -0.8) });

            Add("infinito", Shapes.Infinity_, true, Curve(64, u =>
            {
                double t = 2 * Math.PI * u, s = Math.Sin(t), c = Math.Cos(t), d = 1 + s * s;
                return new P(c / d, s * c / d);
            }));
            Add("8 em pé", Shapes.Atom_, true, Curve(64, u =>
            {
                double t = 2 * Math.PI * u, s = Math.Sin(t), c = Math.Cos(t), d = 1 + s * s;
                return new P(s * c / d, c / d);
            }));

            var u1 = new List<P> { new P(-1, 1), new P(-1, 0) };
            for (int i = 1; i < 32; i++)
            {
                double a = Math.PI + Math.PI * i / 32;
                u1.Add(new P(Math.Cos(a), Math.Sin(a)));
            }
            u1.Add(new P(1, 0)); u1.Add(new P(1, 1));
            Add("U", Shapes.Smiley_, false, u1);

            var arch = new List<P> { new P(-1, -1), new P(-1, 0) };
            for (int i = 1; i < 32; i++)
            {
                double a = Math.PI - Math.PI * i / 32;
                arch.Add(new P(Math.Cos(a), Math.Sin(a)));
            }
            arch.Add(new P(1, 0)); arch.Add(new P(1, -1));
            Add("arco ∩", Shapes.Earth_, false, arch);

            Add("W", Shapes.Flower_, false, new List<P> { new P(-1, 1), new P(-0.5, -1), new P(0, 0.6), new P(0.5, -1), new P(1, 1) });
            Add("Z", Shapes.Lightning_, false, new List<P> { new P(-1, 1), new P(1, 1), new P(-1, -1), new P(1, -1) });
            Add("V", Shapes.Tornado_, false, new List<P> { new P(-1, 1), new P(0, -1), new P(1, 1) });
            Add("chapéu ^", Shapes.Tree_, false, new List<P> { new P(-1, -1), new P(0, 1), new P(1, -1) });
            Add("onda ~", Shapes.Waves_, false, Curve(65, u => new P(u * 2 - 1, 0.45 * Math.Sin(u * 4 * Math.PI))));
        }

        public static GestureResult Recognize(List<P> stroke)
        {
            var res = new GestureResult { Action = ActNone, Score = 0, Name = "" };
            if (stroke.Count < 2 || PathLength(stroke) < 3.0) { res.Action = res.BestAction = ActClick; res.Score = 1; return res; }

            double minX, minY, maxX, maxY;
            Bounds(stroke, out minX, out minY, out maxX, out maxY);
            double w = maxX - minX, h = maxY - minY;
            double diag = Math.Sqrt(w * w + h * h);
            P first = stroke[0], last = stroke[stroke.Count - 1];
            double chord = Dist(first, last);

            // Linhas retas são tratadas à parte (o $1 distorce traços unidimensionais).
            // Medimos pela "magreza" do traço, e não pelo comprimento do caminho, para tolerar tremor.
            double major = Math.Max(w, h);
            if (Math.Min(w, h) / major < 0.3 && chord > 0.8 * major)
            {
                double dx = last.X - first.X, dy = last.Y - first.Y;
                res.Score = chord / major;
                if (Math.Abs(dx) >= Math.Abs(dy)) { res.Action = dx > 0 ? ActNext : ActPrev; res.Name = dx > 0 ? "linha →" : "linha ←"; }
                else { res.Action = dy < 0 ? ActScatter : ActRegroup; res.Name = dy < 0 ? "linha ↓" : "linha ↑"; }
                res.BestAction = res.Action;
                return res;
            }

            // Espiral: o $1 lida mal com ela (as voltas internas somem ao quantizar em células),
            // então usamos geometria: muitas voltas em torno do centro + raio crescendo/diminuindo.
            double spiralScore = chord / Math.Max(diag, 1e-6) > 0.2 ? SpiralScore(stroke, minX, minY, maxX, maxY) : 0;
            if (spiralScore > 0)
            {
                res.Action = res.BestAction = SpiralAction; res.Score = spiralScore; res.Name = "espiral";
                return res;
            }

            P[] open = Normalize(stroke, false);
            P[] closed = Normalize(stroke, true);
            double gap = chord / Math.Max(diag, 1e-6);
            int corners = CountCorners(open);

            double best = double.MaxValue;
            GestureTemplate bestT = null;
            foreach (var t in templates)
            {
                double d;
                if (t.Closed)
                {
                    if (gap > 0.4) continue;
                    d = CyclicDistance(closed, t.Pts, best);
                }
                else
                {
                    d = Math.Min(PathDistance(open, t.Pts, false), PathDistance(open, t.Pts, true));
                    // Formas parecidas no contorno (U/V, ∩/^) diferem nos cantos: penaliza a diferença
                    d *= 1 + 0.35 * Math.Abs(corners - t.Corners);
                }
                if (d < best) { best = d; bestT = t; }
            }
            if (bestT == null) return res;
            res.Score = 1 - best / HalfDiag;
            res.Name = bestT.Name;
            res.BestAction = bestT.Action;
            res.Action = res.Score >= MinScore ? bestT.Action : ActNone;
            return res;
        }

        // Conta cantos agudos: pontos onde a direção muda mais de ~95° numa janela curta.
        // O vértice de um V vira ~127° de uma vez; a curva de um U espalha a virada (~75° por janela).
        // O traço é suavizado antes, para que os degraus de 1 célula (tremor/quantização) não contem.
        static int CountCorners(P[] raw)
        {
            const int k = 6, smooth = 2;
            const double threshold = 95 * Math.PI / 180;
            var p = new P[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                double sx = 0, sy = 0;
                int n = 0;
                for (int j = Math.Max(0, i - smooth); j <= Math.Min(raw.Length - 1, i + smooth); j++)
                {
                    sx += raw[j].X; sy += raw[j].Y; n++;
                }
                p[i] = new P(sx / n, sy / n);
            }
            int corners = 0;
            bool inCorner = false;
            for (int i = k; i < p.Length - k; i++)
            {
                double ax = p[i].X - p[i - k].X, ay = p[i].Y - p[i - k].Y;
                double bx = p[i + k].X - p[i].X, by = p[i + k].Y - p[i].Y;
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la < 1e-9 || lb < 1e-9) continue;
                double cos = (ax * bx + ay * by) / (la * lb);
                bool sharp = Math.Acos(Math.Max(-1, Math.Min(1, cos))) > threshold;
                if (sharp && !inCorner) corners++;
                inCorner = sharp;
            }
            return corners;
        }

        static double SpiralScore(IList<P> s, double minX, double minY, double maxX, double maxY)
        {
            // Normaliza para um quadrado (o traço pode estar esticado) e mede em volta do centro
            double w = Math.Max(maxX - minX, 1e-6), h = Math.Max(maxY - minY, 1e-6);
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            double winding = 0, prev = double.NaN, rMax = 0;
            var radii = new double[s.Count];
            for (int i = 0; i < s.Count; i++)
            {
                double x = (s[i].X - cx) / w, y = (s[i].Y - cy) / h;
                radii[i] = Math.Sqrt(x * x + y * y);
                rMax = Math.Max(rMax, radii[i]);
                double a = Math.Atan2(y, x);
                if (!double.IsNaN(prev))
                {
                    double d = a - prev;
                    if (d > Math.PI) d -= 2 * Math.PI;
                    if (d < -Math.PI) d += 2 * Math.PI;
                    winding += d;
                }
                prev = a;
            }
            double turns = Math.Abs(winding) / (2 * Math.PI);
            // Raio médio do primeiro e do último quinto do traço
            int q = Math.Max(1, s.Count / 5);
            double r0 = 0, r1 = 0;
            for (int i = 0; i < q; i++) { r0 += radii[i]; r1 += radii[s.Count - 1 - i]; }
            r0 /= q; r1 /= q;
            double growth = Math.Abs(r1 - r0) / Math.Max(rMax, 1e-6);
            if (turns < 1.4 || growth < 0.4) return 0;
            return Math.Min(1.0, 0.75 + 0.1 * turns + 0.2 * growth);
        }

        static P[] Normalize(IList<P> raw, bool closed)
        {
            P[] pts;
            if (closed)
            {
                var loop = new List<P>(raw);
                loop.Add(raw[0]);
                pts = Resample(loop, NumPoints + 1);
                Array.Resize(ref pts, NumPoints);
            }
            else pts = Resample(raw, NumPoints);

            double minX, minY, maxX, maxY;
            Bounds(pts, out minX, out minY, out maxX, out maxY);
            double w = maxX - minX, h = maxY - minY, m = Math.Max(Math.Max(w, h), 1e-6);
            // Escala por eixo, mas sem esticar demais traços "achatados"
            w = Math.Max(w, 0.3 * m); h = Math.Max(h, 0.3 * m);
            double cx = 0, cy = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new P((pts[i].X - minX) * Size / w, (pts[i].Y - minY) * Size / h);
                cx += pts[i].X; cy += pts[i].Y;
            }
            cx /= pts.Length; cy /= pts.Length;
            for (int i = 0; i < pts.Length; i++) pts[i] = new P(pts[i].X - cx, pts[i].Y - cy);
            return pts;
        }

        // Reamostragem em passos iguais ao longo do caminho, em uma passada e sem inserir na lista
        static P[] Resample(IList<P> src, int n)
        {
            var res = new P[n];
            double interval = PathLength(src) / (n - 1);
            res[0] = src[0];
            int count = 1;
            if (interval <= 0)
            {
                for (; count < n; count++) res[count] = src[0];
                return res;
            }
            double D = 0;
            P prev = src[0];
            for (int i = 1; i < src.Count && count < n; i++)
            {
                P cur = src[i];
                double d = Dist(prev, cur);
                while (d > 0 && D + d >= interval && count < n)
                {
                    double t = (interval - D) / d;
                    var q = new P(prev.X + t * (cur.X - prev.X), prev.Y + t * (cur.Y - prev.Y));
                    res[count++] = q;
                    prev = q;
                    d = Dist(prev, cur);
                    D = 0;
                }
                D += d;
                prev = cur;
            }
            for (; count < n; count++) res[count] = src[src.Count - 1];
            return res;
        }

        static double PathDistance(P[] a, P[] b, bool reverse)
        {
            double d = 0;
            int n = a.Length;
            for (int i = 0; i < n; i++) d += Dist(reverse ? a[n - 1 - i] : a[i], b[i]);
            return d / n;
        }

        static double CyclicDistance(P[] a, P[] b, double cutoff)
        {
            int n = a.Length;
            double best = double.MaxValue, limit = Math.Min(cutoff, double.MaxValue / 2) * n;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                for (int s = 0; s < n; s++)
                {
                    double d = 0;
                    int idx = s;
                    for (int i = 0; i < n && d < limit; i++)
                    {
                        d += Dist(a[idx], b[i]);
                        idx += dir;
                        if (idx == n) idx = 0; else if (idx < 0) idx = n - 1;
                    }
                    if (d < limit) limit = d;
                    if (d / n < best) best = d / n;
                }
            }
            return best;
        }

        public static double PathLength(IList<P> p)
        {
            double d = 0;
            for (int i = 1; i < p.Count; i++) d += Dist(p[i - 1], p[i]);
            return d;
        }

        static void Bounds(IList<P> p, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            foreach (var q in p)
            {
                minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
            }
        }

        static double Dist(P a, P b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
