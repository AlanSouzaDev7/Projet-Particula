using System;

namespace Particulas
{
    // Cada partícula é uma mola amortecida puxada até o seu ponto-alvo.
    // Estrutura de arrays (SoA): mantém o laço quente contíguo na memória.
    sealed class Sim
    {
        public readonly int N;
        public readonly double[] X, Y, Z, VX, VY, VZ, TX, TY, TZ, R, G, B, TR, TG, TB, K, SX, SY;
        public readonly float[] NX, NY, NZ;   // normal da superfície no alvo (iluminação)
        public readonly int[] Tag;
        public double HalfW = 1.8, HalfH = 1.0;   // tamanho da tela em unidades do mundo
        readonly FastRandom rnd = new FastRandom((ulong)Environment.TickCount);
        readonly int[] perm;

        public Sim(int n)
        {
            N = n;
            X = new double[n]; Y = new double[n]; Z = new double[n];
            VX = new double[n]; VY = new double[n]; VZ = new double[n];
            TX = new double[n]; TY = new double[n]; TZ = new double[n];
            R = new double[n]; G = new double[n]; B = new double[n];
            TR = new double[n]; TG = new double[n]; TB = new double[n];
            K = new double[n]; SX = new double[n]; SY = new double[n];
            NX = new float[n]; NY = new float[n]; NZ = new float[n];
            Tag = new int[n];
            perm = new int[n];
            for (int i = 0; i < n; i++) { K[i] = 0.018 + rnd.NextDouble() * 0.035; perm[i] = i; }
            Scatter(true);
        }

        public void Scatter(bool snap)
        {
            for (int i = 0; i < N; i++)
            {
                TX[i] = (rnd.NextDouble() * 2 - 1) * HalfW * 0.97;
                TY[i] = (rnd.NextDouble() * 2 - 1) * HalfH * 0.95;
                TZ[i] = (rnd.NextDouble() * 2 - 1) * 0.8;
                double r, g, b;
                Shapes.Hsv(0.5 + rnd.NextDouble() * 0.3, 0.2 + rnd.NextDouble() * 0.3, 0.6 + rnd.NextDouble() * 0.4,
                           out r, out g, out b);
                TR[i] = r; TG[i] = g; TB[i] = b;
                // Normais aleatórias fazem a poeira cintilar com a luz
                double nx = rnd.Gauss(), ny = rnd.Gauss(), nz = rnd.Gauss(), l = Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-9;
                NX[i] = (float)(nx / l); NY[i] = (float)(ny / l); NZ[i] = (float)(nz / l);
                Tag[i] = 0;
                if (snap)
                {
                    X[i] = TX[i]; Y[i] = TY[i]; Z[i] = TZ[i];
                    R[i] = r; G[i] = g; B[i] = b;
                }
            }
        }

        public void SetShape(Shape s, bool snap)
        {
            // Embaralha quem vai para onde: a transição vira um "enxame" em vez de um deslize
            for (int i = N - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                int t = perm[i]; perm[i] = perm[j]; perm[j] = t;
            }
            for (int i = 0; i < N; i++)
            {
                Target t = s.Gen(i, N, rnd);
                int p = perm[i];
                TX[p] = t.X; TY[p] = t.Y; TZ[p] = t.Z;
                TR[p] = Math.Min(255, t.R); TG[p] = Math.Min(255, t.G); TB[p] = Math.Min(255, t.B);
                NX[p] = (float)t.NX; NY[p] = (float)t.NY; NZ[p] = (float)t.NZ;
                Tag[p] = t.Tag;
                if (snap)
                {
                    X[p] = t.X; Y[p] = t.Y; Z[p] = t.Z;
                    R[p] = TR[p]; G[p] = TG[p]; B[p] = TB[p];
                }
            }
        }

        // Coloca cada partícula exatamente no alvo animado (usado pela prévia e pelo benchmark)
        public void Snap(Shape s, double time)
        {
            for (int i = 0; i < N; i++)
            {
                double tx = TX[i], ty = TY[i], tz = TZ[i];
                if (s != null && s.Anim != null) s.Anim(time, Tag[i], ref tx, ref ty, ref tz);
                X[i] = tx; Y[i] = ty; Z[i] = tz;
                VX[i] = VY[i] = VZ[i] = 0;
            }
        }

        public void Step(double time, Shape shape)
        {
            // Tremor baixo: com muitas partículas, contornos nítidos valem mais que "vida" extra
            const double damping = 0.87, noise = 0.0012, colorRate = 0.06;
            bool scattered = shape == null;
            Animator anim = shape != null ? shape.Anim : null;
            bool electric = shape != null && shape.Electric;

            for (int i = 0; i < N; i++)
            {
                double tx = TX[i], ty = TY[i], tz = TZ[i];
                if (anim != null) anim(time, Tag[i], ref tx, ref ty, ref tz);
                if (scattered)
                {
                    tx += 0.06 * Math.Sin(time * 0.7 + i * 1.7);
                    ty += 0.05 * Math.Cos(time * 0.5 + i * 2.3);
                }
                else if (electric)
                {
                    tx += (rnd.NextDouble() - 0.5) * 0.035;
                    ty += (rnd.NextDouble() - 0.5) * 0.035;
                }
                double k = K[i];
                double vx = (VX[i] + (tx - X[i]) * k) * damping + (rnd.NextDouble() - 0.5) * noise;
                double vy = (VY[i] + (ty - Y[i]) * k) * damping + (rnd.NextDouble() - 0.5) * noise;
                double vz = (VZ[i] + (tz - Z[i]) * k) * damping + (rnd.NextDouble() - 0.5) * noise;
                VX[i] = vx; VY[i] = vy; VZ[i] = vz;
                X[i] += vx; Y[i] += vy; Z[i] += vz;
                R[i] += (TR[i] - R[i]) * colorRate;
                G[i] += (TG[i] - G[i]) * colorRate;
                B[i] += (TB[i] - B[i]) * colorRate;
            }
        }

        // Onda de choque a partir de uma célula da tela
        public void Shock(double cellX, double cellY, double radius, double power)
        {
            double r2 = radius * radius;
            for (int i = 0; i < N; i++)
            {
                double dx = SX[i] - cellX, dy = (SY[i] - cellY) * 2.0;
                double d2 = dx * dx + dy * dy;
                if (d2 >= r2 || d2 < 1e-12) continue;
                double d = Math.Sqrt(d2), f = power * (1 - d / radius);
                VX[i] += dx / d * f * 0.16;
                VY[i] -= dy / d * f * 0.16;
                VZ[i] += (rnd.NextDouble() - 0.5) * f * 0.2;
            }
        }
    }
}
