using System;
using System.Collections.Generic;
using System.Text;

namespace Particulas
{
    static class SelfTest
    {
        // Gera traços "humanos" a partir dos gabaritos (início/sentido aleatórios, proporções
        // distorcidas, tremor e quantização em células do console) e mede a taxa de acerto.
        public static int Run()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Recognizer.RegisterDefaults();
            var rnd = new Random(42);
            int total = 0, ok = 0;
            var perName = new SortedDictionary<string, int[]>();
            var confusions = new SortedDictionary<string, int>();
            const int trials = 60;

            foreach (var kv in Recognizer.Raw)
            {
                GestureTemplate t = kv.Key;
                for (int k = 0; k < trials; k++)
                {
                    List<P> s = Synthesize(kv.Value, t.Closed, rnd);
                    GestureResult r = Recognizer.Recognize(s);
                    bool hit = r.Action == t.Action;
                    total++; if (hit) ok++;
                    int[] c;
                    if (!perName.TryGetValue(t.Name, out c)) { c = new int[2]; perName[t.Name] = c; }
                    c[0]++; if (hit) c[1]++;
                    if (!hit)
                    {
                        string key = t.Name + " -> " + (r.Action == Recognizer.ActNone ? "nenhum/" + r.Name : r.Name);
                        int cnt;
                        confusions.TryGetValue(key, out cnt);
                        confusions[key] = cnt + 1;
                    }
                }
            }
            foreach (var kv in confusions) Console.WriteLine("  confusão: " + kv.Key + " x" + kv.Value);
            foreach (var kv in perName)
                Console.WriteLine(kv.Key.PadRight(14) + (100.0 * kv.Value[1] / kv.Value[0]).ToString("0.0").PadLeft(6) + "%");

            // Linhas
            string[] lineNames = { "→", "←", "↓", "↑" };
            int[] lineAct = { Recognizer.ActNext, Recognizer.ActPrev, Recognizer.ActScatter, Recognizer.ActRegroup };
            double[][] dirs = { new[] { 1.0, 0 }, new[] { -1.0, 0 }, new[] { 0, -1.0 }, new[] { 0, 1.0 } };
            for (int d = 0; d < 4; d++)
            {
                int hits = 0;
                for (int k = 0; k < trials; k++)
                {
                    var s = new List<P>();
                    double len = 10 + rnd.NextDouble() * 30;
                    for (int i = 0; i <= 20; i++)
                    {
                        double f = i / 20.0;
                        double x = 40 + dirs[d][0] * len * f + (rnd.NextDouble() - 0.5) * 1.5;
                        double y = 15 - dirs[d][1] * len * 0.5 * f + (rnd.NextDouble() - 0.5) * 1.0;
                        s.Add(new P(Math.Round(x), -Math.Round(y) * 2.0));
                    }
                    if (Recognizer.Recognize(Dedup(s)).Action == lineAct[d]) hits++;
                }
                total += trials; ok += hits;
                Console.WriteLine(("linha " + lineNames[d]).PadRight(14) + (100.0 * hits / trials).ToString("0.0").PadLeft(6) + "%");
            }

            // Rabiscos aleatórios: quantos são aceitos indevidamente como alguma forma
            int junkAccepted = 0;
            for (int k = 0; k < 400; k++)
            {
                var s = new List<P>();
                double x = 50, y = 20, ang = rnd.NextDouble() * 6.28;
                int n = 15 + rnd.Next(40);
                for (int i = 0; i < n; i++)
                {
                    ang += (rnd.NextDouble() - 0.5) * 2.2;
                    x += Math.Cos(ang) * 1.5; y += Math.Sin(ang) * 0.8;
                    s.Add(new P(Math.Round(x), -Math.Round(y) * 2.0));
                }
                if (Recognizer.Recognize(Dedup(s)).Action >= 0) junkAccepted++;
            }
            Console.WriteLine("rabiscos aceitos como forma: " + (100.0 * junkAccepted / 400).ToString("0.0") + "%");

            double acc = 100.0 * ok / total;
            Console.WriteLine("TOTAL         " + acc.ToString("0.0").PadLeft(6) + "%  (" + ok + "/" + total + ")");
            return acc >= 90 ? 0 : 2;
        }

        static List<P> Synthesize(List<P> raw, bool closed, Random rnd)
        {
            // Densifica o gabarito para poder começar em qualquer ponto
            var dense = new List<P>();
            int segs = closed ? raw.Count : raw.Count - 1;
            for (int i = 0; i < segs; i++)
            {
                P a = raw[i], b = raw[(i + 1) % raw.Count];
                for (int j = 0; j < 12; j++)
                    dense.Add(new P(a.X + (b.X - a.X) * j / 12.0, a.Y + (b.Y - a.Y) * j / 12.0));
            }
            if (!closed) dense.Add(raw[raw.Count - 1]);

            if (closed)
            {
                int start = rnd.Next(dense.Count);
                var rot = new List<P>();
                for (int i = 0; i <= dense.Count; i++) rot.Add(dense[(start + i) % dense.Count]);
                int drop = (int)(rot.Count * rnd.NextDouble() * 0.08);       // fechamento imperfeito
                rot.RemoveRange(rot.Count - drop, drop);
                dense = rot;
            }
            if (rnd.Next(2) == 0) dense.Reverse();

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in dense)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            double span = Math.Max(maxX - minX, maxY - minY);
            double cells = 14 + rnd.NextDouble() * 22;                        // tamanho do desenho
            double ax = 0.75 + rnd.NextDouble() * 0.5, ay = 0.75 + rnd.NextDouble() * 0.5;
            // Posição aleatória na tela: o desenho não cai sempre alinhado às células
            double baseX = 50 + rnd.NextDouble(), baseY = 25 + rnd.NextDouble();
            var outp = new List<P>();
            foreach (var p in dense)
            {
                double x = baseX + (p.X - minX) / span * cells * ax + (rnd.NextDouble() - 0.5) * 0.9;
                double y = baseY - (p.Y - minY) / span * cells * 0.5 * ay + (rnd.NextDouble() - 0.5) * 0.6;
                outp.Add(new P(Math.Round(x), -Math.Round(y) * 2.0));        // célula -> coordenada do app
            }
            return Dedup(outp);
        }

        public static List<P> Dedup(List<P> s)
        {
            var o = new List<P>();
            foreach (var p in s)
                if (o.Count == 0 || o[o.Count - 1].X != p.X || o[o.Count - 1].Y != p.Y) o.Add(p);
            return o;
        }
    }
}

