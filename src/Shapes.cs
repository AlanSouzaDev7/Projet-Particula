using System;

namespace Particulas
{
    // Ponto-alvo de uma partícula: posição 3D, cor, normal da superfície (para a iluminação)
    // e uma etiqueta usada pelas animações (ex.: qual elétron, qual lua).
    struct Target
    {
        public double X, Y, Z, R, G, B, NX, NY, NZ;
        public int Tag;

        public Target(double x, double y, double z, double r, double g, double b)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b; Tag = 0;
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l < 1e-6) { NX = 0; NY = 0; NZ = -1; }
            else { NX = x / l; NY = y / l; NZ = z / l; }
        }

        public Target N(double nx, double ny, double nz)
        {
            Target t = this;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l > 1e-9) { t.NX = nx / l; t.NY = ny / l; t.NZ = nz / l; }
            return t;
        }

        public Target T(int tag)
        {
            Target t = this;
            t.Tag = tag;
            return t;
        }
    }

    delegate Target ShapeGen(int i, int n, FastRandom rnd);

    // Move o alvo ao longo do tempo (elétrons orbitando, ondas, tornado girando...)
    delegate void Animator(double t, int tag, ref double x, ref double y, ref double z);

    sealed class Shape
    {
        public readonly string Name, Gesture, Hand;   // Gesture = traço (mouse/ar), Hand = pose da mão
        public readonly double Tilt;                  // inclinação da câmera (rad); positivo = olhando de cima
        public readonly double Spin;                  // rad/s de giro contínuo; 0 = balança de leve
        public readonly bool Electric;
        public readonly Animator Anim;
        public readonly ShapeGen Gen;

        public Shape(string name, string gesture, string hand, double tilt, double spin, bool electric,
                     Animator anim, ShapeGen gen)
        {
            Name = name; Gesture = gesture; Hand = hand; Tilt = tilt; Spin = spin;
            Electric = electric; Anim = anim; Gen = gen;
        }
    }

    static class Shapes
    {
        // Os índices aqui são usados pelo reconhecedor de traços e pelo mapa de poses da mão
        public const int Heart_ = 0, Planet_ = 1, Star_ = 2, Galaxy_ = 3, Dna_ = 4, Cube_ = 5, Pyramid_ = 6,
                         Infinity_ = 7, Smiley_ = 8, Flower_ = 9, Lightning_ = 10, Torus_ = 11, Atom_ = 12,
                         Earth_ = 13, Diamond_ = 14, Tree_ = 15, Tornado_ = 16, Waves_ = 17;

        public static Shape[] All()
        {
            return new[]
            {
                new Shape("Coração",     "coração",            "eu te amo / 2 mãos", 0.10, 0.6,  false, Beat,       Heart),
                new Shape("Planeta",     "círculo",            "OK",                 0.38, 0.55, false, null,       Planet),
                new Shape("Estrela",     "estrela 5 pontas",   "1 dedo",             0.10, 0.7,  false, null,       Star),
                new Shape("Galáxia",     "espiral",            "hang loose",         0.95, 0.15, false, SpinGalaxy, Galaxy),
                new Shape("DNA",         "letra S",            "V",                  0.22, 0.55, false, null,       Dna),
                new Shape("Cubo",        "quadrado",           "4 dedos",            0.50, 0.55, false, null,       Cube),
                new Shape("Pirâmide",    "triângulo",          "3 dedos",            0.35, 0.55, false, null,       Pyramid),
                new Shape("Infinito",    "∞ deitado",          "2 mãos, 1 dedo",     0.15, 0,    false, null,       Infinity),
                new Shape("Rosto feliz", "letra U",            "joinha",             0.05, 0,    false, null,       Smiley),
                new Shape("Flor",        "letra W",            "mindinho",           0.10, 0,    false, null,       Flower),
                new Shape("Raio",        "letra Z",            "rock",               0.05, 0,    true,  null,       Lightning),
                new Shape("Rosquinha",   "círculo duplo",      "2 punhos",           0.70, 0.5,  false, null,       Torus),
                new Shape("Átomo",       "8 em pé",            "Spock / 2 mãos em L", 0.25, 0.3,  false, OrbitAtom,  Atom),
                new Shape("Terra",       "arco ∩",             "2 mãos em V",        0.35, 0.35, false, OrbitMoon,  Earth),
                new Shape("Diamante",    "losango",            "L",                  0.35, 0.6,  false, null,       Diamond),
                new Shape("Árvore",      "chapéu ^",           "—",                  0.20, 0.45, false, null,       Tree),
                new Shape("Tornado",     "letra V",            "negativo",           0.15, 0,    false, Twister,    Tornado),
                new Shape("Ondas",       "onda ~",             "pistola",            0.60, 0.25, false, WaveSurface, Waves),
            };
        }

        // ---- utilitários -------------------------------------------------------------
        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        static Target Mix(double x, double y, double z,
                          double r1, double g1, double b1, double r2, double g2, double b2, double t)
        {
            t = Clamp01(t);
            return new Target(x, y, z, r1 + (r2 - r1) * t, g1 + (g2 - g1) * t, b1 + (b2 - b1) * t);
        }

        public static void Hsv(double h, double s, double v, out double r, out double g, out double b)
        {
            h = ((h % 1.0) + 1.0) % 1.0 * 6.0;
            int i = (int)h;
            double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            r *= 255; g *= 255; b *= 255;
        }

        static void RandomDir(FastRandom rnd, out double x, out double y, out double z)
        {
            z = rnd.NextDouble() * 2 - 1;
            double a = rnd.NextDouble() * 2 * Math.PI, r = Math.Sqrt(1 - z * z);
            x = Math.Cos(a) * r; y = Math.Sin(a) * r;
        }

        static void Fibonacci(int k, int m, out double x, out double y, out double z)
        {
            y = 1 - 2 * (k + 0.5) / m;
            double rad = Math.Sqrt(Math.Max(0, 1 - y * y)), phi = k * 2.399963229728653;
            x = Math.Cos(phi) * rad; z = Math.Sin(phi) * rad;
        }

        static double[] V(double x, double y, double z) { return new[] { x, y, z }; }

        static double[] Lerp(double[] a, double[] b, double f)
        {
            return V(a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f);
        }

        static double[] InTriangle(FastRandom rnd, double[] a, double[] b, double[] c)
        {
            double u = rnd.NextDouble(), v = rnd.NextDouble();
            if (u + v > 1) { u = 1 - u; v = 1 - v; }
            return V(a[0] + u * (b[0] - a[0]) + v * (c[0] - a[0]),
                     a[1] + u * (b[1] - a[1]) + v * (c[1] - a[1]),
                     a[2] + u * (b[2] - a[2]) + v * (c[2] - a[2]));
        }

        // Normal de um triângulo, virada para fora (para longe de "center")
        static double[] FaceNormal(double[] a, double[] b, double[] c, double[] center)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double mx = (a[0] + b[0] + c[0]) / 3 - center[0], my = (a[1] + b[1] + c[1]) / 3 - center[1],
                   mz = (a[2] + b[2] + c[2]) / 3 - center[2];
            if (nx * mx + ny * my + nz * mz < 0) { nx = -nx; ny = -ny; nz = -nz; }
            return V(nx, ny, nz);
        }

        static void Rotate(double[] axis, double ang, ref double x, ref double y, ref double z)
        {
            // Rodrigues: v cosθ + (k×v) sinθ + k (k·v)(1−cosθ)
            double c = Math.Cos(ang), s = Math.Sin(ang);
            double kx = axis[0], ky = axis[1], kz = axis[2];
            double dot = kx * x + ky * y + kz * z;
            double cx = ky * z - kz * y, cy = kz * x - kx * z, cz = kx * y - ky * x;
            double nx = x * c + cx * s + kx * dot * (1 - c);
            double ny = y * c + cy * s + ky * dot * (1 - c);
            double nz = z * c + cz * s + kz * dot * (1 - c);
            x = nx; y = ny; z = nz;
        }

        // ---- animações ---------------------------------------------------------------
        // "Tum-tum": dois picos rápidos a cada ~1,1 s
        static void Beat(double t, int tag, ref double x, ref double y, ref double z)
        {
            double ph = t % 1.1, a = (ph - 0.10) / 0.06, b = (ph - 0.32) / 0.07;
            double s = 1.0 + 0.08 * (Math.Exp(-a * a) + 0.6 * Math.Exp(-b * b));
            x *= s; y *= s; z *= s;
        }

        // Rotação diferencial: o centro da galáxia gira mais rápido que as bordas
        static void SpinGalaxy(double t, int tag, ref double x, ref double y, ref double z)
        {
            double r = Math.Sqrt(x * x + z * z), a = t * 0.35 / (0.3 + r);
            double c = Math.Cos(a), s = Math.Sin(a), nx = x * c - z * s;
            z = x * s + z * c; x = nx;
        }

        static void OrbitAtom(double t, int tag, ref double x, ref double y, ref double z)
        {
            if (tag > 0) Rotate(OrbitAxis[tag - 1], t * 2.4 + tag * 2.1, ref x, ref y, ref z);
        }

        static readonly double[] Up = { 0, 1, 0 };

        static void OrbitMoon(double t, int tag, ref double x, ref double y, ref double z)
        {
            if (tag == 1) Rotate(Up, t * 0.6, ref x, ref y, ref z);
        }

        static void Twister(double t, int tag, ref double x, ref double y, ref double z)
        {
            double ang = t * (4.0 - 1.5 * (y + 1));
            double c = Math.Cos(ang), s = Math.Sin(ang), nx = x * c - z * s;
            z = x * s + z * c;
            x = nx + 0.1 * Math.Sin(t * 1.1 + y * 1.7) * (1 - y) / 2;
        }

        static void WaveSurface(double t, int tag, ref double x, ref double y, ref double z)
        {
            y = 0.16 * Math.Sin(2.4 * x + 2.2 * t) + 0.12 * Math.Cos(2.9 * z + 1.6 * t)
              + 0.06 * Math.Sin(5 * (x + z) + 3 * t);
        }

        // ---- formas ------------------------------------------------------------------

        // Coração 3D: superfície implícita (x² + 9/4·d² + v² − 1)³ − x²·v³ − 9/80·d²·v³ = 0
        // (v = vertical, d = profundidade). Achamos a superfície por bisseção ao longo de raios.
        static double HeartF(double x, double y, double z)
        {
            double a = x * x + 2.25 * z * z + y * y - 1;
            return a * a * a - x * x * y * y * y - 0.1125 * z * z * y * y * y;
        }

        static Target Heart(int i, int n, FastRandom rnd)
        {
            double dx, dy, dz;
            RandomDir(rnd, out dx, out dy, out dz);
            double lo = 0, hi = 1.7;
            for (int k = 0; k < 24; k++)
            {
                double mid = (lo + hi) * 0.5;
                if (HeartF(dx * mid, dy * mid, dz * mid) < 0) lo = mid; else hi = mid;
            }
            double sx = dx * lo, sy = dy * lo, sz = dz * lo;
            const double e = 1e-3;
            double gx = HeartF(sx + e, sy, sz) - HeartF(sx - e, sy, sz);
            double gy = HeartF(sx, sy + e, sz) - HeartF(sx, sy - e, sz);
            double gz = HeartF(sx, sy, sz + e) - HeartF(sx, sy, sz - e);

            bool surface = rnd.NextDouble() < 0.8;
            double r = surface ? 1.0 : Math.Pow(rnd.NextDouble(), 1.0 / 3);
            const double scale = 0.78;
            double x = sx * r * scale, y = (sy * r + 0.1) * scale, z = sz * r * scale;
            double k2 = (y + 0.8) / 1.6;
            Target t = surface ? Mix(x, y, z, 220, 25, 70, 255, 120, 175, k2)
                               : Mix(x, y, z, 150, 10, 45, 220, 60, 110, k2);
            return t.N(gx, gy, gz);
        }

        static Target Planet(int i, int n, FastRandom rnd)
        {
            int sphereCount = (int)(n * 0.62);
            if (i < sphereCount)
            {
                double x, y, z;
                Fibonacci(i, sphereCount, out x, out y, out z);
                const double R = 0.56;
                double phi = i * 2.399963229728653, lon = phi % (2 * Math.PI);
                if (Math.Abs(y + 0.35) < 0.12 && Math.Abs(lon - 1.0) < 0.35)
                    return new Target(x * R, y * R, z * R, 235, 95, 60);              // a "grande mancha"
                double band = 0.5 + 0.5 * Math.Sin(y * 13 + Math.Sin(phi * 3) * 0.35);
                return Mix(x * R, y * R, z * R, 240, 195, 125, 185, 115, 65, band);
            }
            double a = rnd.NextDouble() * 2 * Math.PI;
            double rr = 0.76 + 0.36 * rnd.NextDouble();
            if (rr > 0.93 && rr < 0.97) rr += 0.05;                                  // divisão de Cassini
            double shade = 0.85 + 0.15 * Math.Sin(rr * 60);
            Target c = Mix(Math.Cos(a) * rr, rnd.Gauss() * 0.008, Math.Sin(a) * rr,
                           215, 200, 165, 150, 140, 120, (rr - 0.76) / 0.41);
            c.R *= shade; c.G *= shade; c.B *= shade;
            return c.N(0, 1, 0);
        }

        // Estrela 3D: duas pirâmides de 10 faces unidas pela borda (como um enfeite facetado)
        static readonly double[][] StarRim = BuildStarRim();
        static readonly double[] StarFront = { 0, -0.05, -0.36 }, StarBack = { 0, -0.05, 0.36 }, StarCenter = { 0, -0.05, 0 };

        static double[][] BuildStarRim()
        {
            var v = new double[10][];
            for (int k = 0; k < 10; k++)
            {
                double ang = Math.PI / 2 + k * Math.PI / 5, rad = (k % 2 == 0) ? 1.0 : 0.42;
                v[k] = V(Math.Cos(ang) * rad, Math.Sin(ang) * rad - 0.05, 0);
            }
            return v;
        }

        static Target Star(int i, int n, FastRandom rnd)
        {
            double pick = rnd.NextDouble();
            double[] p, nrm;
            if (pick < 0.3)
            {
                // Arestas: borda e cristas (ápice -> pontas)
                if (rnd.NextDouble() < 0.5)
                {
                    int k = rnd.Next(10);
                    p = Lerp(StarRim[k], StarRim[(k + 1) % 10], rnd.NextDouble());
                }
                else
                {
                    int k = rnd.Next(5) * 2;
                    p = Lerp(rnd.Next(2) == 0 ? StarFront : StarBack, StarRim[k], rnd.NextDouble());
                }
                nrm = V(p[0], p[1] + 0.05, p[2]);
                double d = Math.Sqrt(p[0] * p[0] + (p[1] + 0.05) * (p[1] + 0.05));
                return Mix(p[0], p[1], p[2], 255, 250, 200, 255, 200, 60, d).N(nrm[0], nrm[1], nrm[2]);
            }
            int f = rnd.Next(20), kk = f % 10;
            double[] apex = f < 10 ? StarFront : StarBack;
            p = InTriangle(rnd, apex, StarRim[kk], StarRim[(kk + 1) % 10]);
            nrm = FaceNormal(apex, StarRim[kk], StarRim[(kk + 1) % 10], StarCenter);
            double dist = Math.Sqrt(p[0] * p[0] + (p[1] + 0.05) * (p[1] + 0.05));
            return Mix(p[0], p[1], p[2], 255, 235, 120, 255, 150, 20, dist).N(nrm[0], nrm[1], nrm[2]);
        }

        static Target Galaxy(int i, int n, FastRandom rnd)
        {
            if (i % 7 == 0)
                return new Target(rnd.Gauss() * 0.11, rnd.Gauss() * 0.06, rnd.Gauss() * 0.11, 255, 235, 200);
            const int arms = 3;
            int arm = i % arms;
            double t = Math.Pow(rnd.NextDouble(), 0.75);
            double r = 0.08 + t * 0.98;
            double theta = arm * 2 * Math.PI / arms + t * 3.4 + rnd.Gauss() * 0.22 * (0.35 + t);
            double px = Math.Cos(theta) * r, pz = Math.Sin(theta) * r, py = rnd.Gauss() * 0.035 * (1.2 - t);
            if (rnd.NextDouble() < 0.05) return new Target(px, py, pz, 255, 255, 255).N(0, 1, 0);
            return (t < 0.3 ? Mix(px, py, pz, 255, 220, 185, 205, 140, 255, t / 0.3)
                            : Mix(px, py, pz, 205, 140, 255, 80, 165, 255, (t - 0.3) / 0.7)).N(0, 1, 0);
        }

        static Target Dna(int i, int n, FastRandom rnd)
        {
            const double R = 0.38, turns = 2.2, len = 2.2;
            if (i % 5 < 3)
            {
                int strand = i % 2;
                double t = rnd.NextDouble();
                double ang = t * turns * 2 * Math.PI + strand * Math.PI;
                double x = Math.Cos(ang) * R + rnd.Gauss() * 0.02, z = Math.Sin(ang) * R + rnd.Gauss() * 0.02;
                double y = -len / 2 + len * t;
                Target s = strand == 0 ? new Target(x, y, z, 60, 205, 255) : new Target(x, y, z, 255, 80, 200);
                return s.N(x, 0, z);
            }
            const int rungs = 24;
            int k = rnd.Next(rungs);
            double tr = (k + 0.5) / rungs, a2 = tr * turns * 2 * Math.PI, u = rnd.NextDouble(), m = 1 - 2 * u;
            double rx = Math.Cos(a2) * R * m, rz = Math.Sin(a2) * R * m, ry = -len / 2 + len * tr;
            Target rung = u < 0.5 ? new Target(rx, ry, rz, 120, 255, 130) : new Target(rx, ry, rz, 255, 230, 80);
            return rung.N(0, 1, 0);
        }

        static Target Cube(int i, int n, FastRandom rnd)
        {
            const double s = 0.62;
            var c = new double[3];
            bool edge = rnd.NextDouble() < 0.7;
            int axis;
            if (edge)
            {
                int e = rnd.Next(12);
                axis = e / 4;
                c[axis] = (rnd.NextDouble() * 2 - 1) * s;
                c[(axis + 1) % 3] = ((e & 1) != 0 ? 1 : -1) * s;
                c[(axis + 2) % 3] = ((e & 2) != 0 ? 1 : -1) * s;
            }
            else
            {
                int f = rnd.Next(6);
                axis = f / 2;
                c[axis] = (f % 2 == 0 ? 1 : -1) * s;
                c[(axis + 1) % 3] = (rnd.NextDouble() * 2 - 1) * s;
                c[(axis + 2) % 3] = (rnd.NextDouble() * 2 - 1) * s;
            }
            double m = edge ? 1.0 : 0.55;
            var t = new Target(c[0], c[1], c[2],
                (60 + 195 * (c[0] / s + 1) / 2) * m,
                (60 + 195 * (c[1] / s + 1) / 2) * m,
                (60 + 195 * (c[2] / s + 1) / 2) * m);
            if (!edge)
            {
                var nn = new double[3];
                nn[axis] = c[axis];
                t = t.N(nn[0], nn[1], nn[2]);
            }
            return t;
        }

        static readonly double[] PyrApex = { 0, 0.9, 0 }, PyrCenter = { 0, -0.2, 0 };
        static readonly double[][] PyrBase =
        {
            new[] { -0.8, -0.6, -0.8 }, new[] { 0.8, -0.6, -0.8 }, new[] { 0.8, -0.6, 0.8 }, new[] { -0.8, -0.6, 0.8 },
        };

        static Target Pyramid(int i, int n, FastRandom rnd)
        {
            double[] p;
            if (rnd.NextDouble() < 0.55)
            {
                int e = rnd.Next(8);
                p = Lerp(PyrBase[e % 4], e < 4 ? PyrBase[(e + 1) % 4] : PyrApex, rnd.NextDouble());
                return new Target(p[0], p[1], p[2], 255, 220, 130);
            }
            int k = rnd.Next(4);
            p = InTriangle(rnd, PyrBase[k], PyrBase[(k + 1) % 4], PyrApex);
            double[] nrm = FaceNormal(PyrBase[k], PyrBase[(k + 1) % 4], PyrApex, PyrCenter);
            return Mix(p[0], p[1], p[2], 160, 112, 50, 235, 185, 95, (p[1] + 0.6) / 1.5).N(nrm[0], nrm[1], nrm[2]);
        }

        static Target Infinity(int i, int n, FastRandom rnd)
        {
            double t = rnd.NextDouble() * 2 * Math.PI;
            double s = Math.Sin(t), c = Math.Cos(t), d = 1 + s * s;
            double ox = rnd.Gauss() * 0.03, oy = rnd.Gauss() * 0.03, oz = rnd.Gauss() * 0.05;
            double cx = 1.2 * c / d, cy = 1.2 * s * c / d, cz = 0.3 * s;
            double r, g, b;
            Hsv(t / (2 * Math.PI), 0.7, 1.0, out r, out g, out b);
            return new Target(cx + ox, cy + oy, cz + oz, r, g, b).N(ox, oy, oz - 0.02);
        }

        // Rosto feliz 3D: cabeça esférica com olhos, boca e bochechas na superfície da frente
        static Target Smiley(int i, int n, FastRandom rnd)
        {
            const double R = 0.85;
            double pick = rnd.NextDouble();
            double x = 0, y = 0, z = -R;
            if (pick < 0.16)
            {
                double side = rnd.Next(2) == 0 ? -1 : 1, a = rnd.NextDouble() * 2 * Math.PI, r = Math.Sqrt(rnd.NextDouble());
                x = side * 0.28 + Math.Cos(a) * r * 0.1; y = 0.22 + Math.Sin(a) * r * 0.17;
                z = -Math.Sqrt(Math.Max(0, R * R - x * x - y * y)) - 0.03;
                return new Target(x, y, z, 250, 250, 255);
            }
            if (pick < 0.32)
            {
                double a = Math.PI * (1.15 + 0.7 * rnd.NextDouble()), r = 0.45 + rnd.Gauss() * 0.025;
                x = Math.Cos(a) * r; y = Math.Sin(a) * r - 0.02;
                z = -Math.Sqrt(Math.Max(0, R * R - x * x - y * y)) - 0.03;
                return new Target(x, y, z, 255, 70, 60);
            }
            if (pick < 0.38)
            {
                double side = rnd.Next(2) == 0 ? -1 : 1, a = rnd.NextDouble() * 2 * Math.PI, r = Math.Sqrt(rnd.NextDouble()) * 0.09;
                x = side * 0.5 + Math.Cos(a) * r; y = -0.12 + Math.Sin(a) * r;
                z = -Math.Sqrt(Math.Max(0, R * R - x * x - y * y)) - 0.02;
                return new Target(x, y, z, 255, 120, 140);
            }
            for (int tries = 0; tries < 30; tries++)
            {
                RandomDir(rnd, out x, out y, out z);
                x *= R; y *= R; z *= R;
                if (z < 0 && FaceFeature(x, y)) continue;    // deixa espaço para olhos/boca
                break;
            }
            return new Target(x, y, z, 255, 200, 0);
        }

        static bool FaceFeature(double x, double y)
        {
            for (int sd = -1; sd <= 1; sd += 2)
            {
                double ex = (x - sd * 0.28) / 0.15, ey = (y - 0.22) / 0.23;
                if (ex * ex + ey * ey < 1) return true;
            }
            double r = Math.Sqrt(x * x + (y + 0.02) * (y + 0.02)), ang = Math.Atan2(y + 0.02, x);
            if (ang < 0) ang += 2 * Math.PI;
            return Math.Abs(r - 0.45) < 0.08 && ang > Math.PI * 1.1 && ang < Math.PI * 1.9;
        }

        static Target Flower(int i, int n, FastRandom rnd)
        {
            const double cx = 0, cy = 0.3, R = 0.62;
            double pick = rnd.NextDouble();
            if (pick < 0.58)
            {
                double th = rnd.NextDouble() * Math.PI, r0 = Math.Cos(5 * th);
                bool edge = pick < 0.16;
                double rad = r0 * (edge ? 1.0 : Math.Sqrt(rnd.NextDouble()));
                double petalA = th + Math.PI / 2;               // uma pétala aponta para cima
                double x = cx + Math.Cos(petalA) * rad * R, y = cy + Math.Sin(petalA) * rad * R;
                double z = 0.18 * rad * rad;
                Target t = edge ? new Target(x, y, z, 255, 90, 175)
                                : Mix(x, y, z, 255, 210, 235, 255, 70, 160, Math.Abs(rad));
                return t.N((x - cx) * 0.5, (y - cy) * 0.5, -1);
            }
            if (pick < 0.68)
            {
                double a = rnd.NextDouble() * 2 * Math.PI, r = Math.Sqrt(rnd.NextDouble()) * 0.13;
                return new Target(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r, -0.06, 255, 220, 60).N(0, 0, -1);
            }
            if (pick < 0.86)
            {
                double y = -1.0 + rnd.NextDouble() * 1.25, x = 0.05 * Math.Sin(y * 4) + rnd.Gauss() * 0.015;
                return new Target(x, y, 0.08 + rnd.Gauss() * 0.015, 70, 190, 80).N(x, 0, -0.5);
            }
            bool right = rnd.Next(2) == 0;
            double by = right ? -0.45 : -0.7, bx = 0.05 * Math.Sin(by * 4), dirA = right ? 0.6 : Math.PI - 0.6;
            double sAlong = rnd.NextDouble(), width = 0.12 * Math.Sin(Math.PI * sAlong) * (rnd.NextDouble() * 2 - 1);
            double lx = bx + Math.Cos(dirA) * sAlong * 0.45 - Math.Sin(dirA) * width;
            double ly = by + Math.Sin(dirA) * sAlong * 0.45 + Math.Cos(dirA) * width;
            return new Target(lx, ly, 0.08, 90, 215, 100).N(0, 0.3, -1);
        }

        static readonly double[][] Bolt =
        {
            new[] { 0.35, 1.0 }, new[] { -0.32, 0.06 }, new[] { 0.22, 0.06 }, new[] { -0.38, -1.0 },
        };

        static Target Lightning(int i, int n, FastRandom rnd)
        {
            double total = 0;
            var segLen = new double[Bolt.Length - 1];
            for (int k = 0; k < segLen.Length; k++)
            {
                double dx = Bolt[k + 1][0] - Bolt[k][0], dy = Bolt[k + 1][1] - Bolt[k][1];
                segLen[k] = Math.Sqrt(dx * dx + dy * dy); total += segLen[k];
            }
            double s = rnd.NextDouble() * total, acc = 0;
            int seg = 0;
            while (seg < segLen.Length - 1 && acc + segLen[seg] < s) { acc += segLen[seg]; seg++; }
            double f = (s - acc) / segLen[seg];
            double[] p = Bolt[seg], q = Bolt[seg + 1];
            double x = p[0] + (q[0] - p[0]) * f, y = p[1] + (q[1] - p[1]) * f;
            double nx = -(q[1] - p[1]) / segLen[seg], ny = (q[0] - p[0]) / segLen[seg];
            double width = 0.16 * (1 - 0.75 * s / total);
            if (rnd.NextDouble() < 0.12)
                return new Target(x + rnd.Gauss() * 0.25, y + rnd.Gauss() * 0.25, rnd.Gauss() * 0.2, 110, 150, 255);
            double off = rnd.Gauss() * 0.5;
            x += nx * width * off; y += ny * width * off;
            double z = rnd.Gauss() * width * 0.5;
            return Mix(x, y, z, 245, 250, 255, 110, 165, 255, Math.Abs(off)).N(nx * off, ny * off, -1);
        }

        // Rosquinha: toro com cobertura rosa ondulada e confeitos coloridos
        static Target Torus(int i, int n, FastRandom rnd)
        {
            const double R = 0.68, r = 0.28;
            double u = 0, v = 0;
            for (int tries = 0; tries < 20; tries++)          // amostragem proporcional à área
            {
                u = rnd.NextDouble() * 2 * Math.PI; v = rnd.NextDouble() * 2 * Math.PI;
                if (rnd.NextDouble() < (R + r * Math.Cos(v)) / (R + r)) break;
            }
            double cu = Math.Cos(u), su = Math.Sin(u), cv = Math.Cos(v), sv = Math.Sin(v);
            double x = (R + r * cv) * cu, z = (R + r * cv) * su, y = r * sv;
            var t = new Target(x, y, z, 225, 165, 95);
            if (sv > -0.15 + 0.12 * Math.Sin(5 * u))
            {
                if (rnd.NextDouble() < 0.08)
                {
                    double cr, cg, cb;
                    Hsv(rnd.NextDouble(), 0.8, 1.0, out cr, out cg, out cb);
                    t = new Target(x, y + 0.01, z, cr, cg, cb);
                }
                else t = new Target(x, y, z, 255, 115, 175);
            }
            return t.N(cv * cu, sv, cv * su);
        }

        // Átomo: núcleo de prótons/nêutrons, três órbitas e três elétrons (animados)
        static readonly double[][] OrbitAxis = BuildOrbitAxes();
        static readonly double[][] OrbitE1 = new double[3][], OrbitE2 = new double[3][];
        static readonly double[][] Nucleons = BuildNucleons();

        static double[][] BuildOrbitAxes()
        {
            var axes = new double[3][];
            double tilt = 70 * Math.PI / 180;
            for (int k = 0; k < 3; k++)
            {
                double yaw = k * Math.PI / 3;
                double[] n = V(Math.Sin(tilt) * Math.Sin(yaw), Math.Cos(tilt), Math.Sin(tilt) * Math.Cos(yaw));
                axes[k] = n;
            }
            return axes;
        }

        static double[][] BuildNucleons()
        {
            var p = new double[14][];
            for (int k = 0; k < 14; k++)
            {
                double x, y, z;
                Fibonacci(k, 14, out x, out y, out z);
                p[k] = V(x * 0.13, y * 0.13, z * 0.13);
            }
            return p;
        }

        static void OrbitBasis(int k, out double[] e1, out double[] e2)
        {
            if (OrbitE1[k] == null)
            {
                double[] n = OrbitAxis[k];
                // e1 = n × up (normalizado), e2 = n × e1
                double ax = n[1] * 0 - n[2] * 1, ay = n[2] * 0 - n[0] * 0, az = n[0] * 1 - n[1] * 0;
                double l = Math.Sqrt(ax * ax + ay * ay + az * az);
                double[] a = V(ax / l, ay / l, az / l);
                double[] b = V(n[1] * a[2] - n[2] * a[1], n[2] * a[0] - n[0] * a[2], n[0] * a[1] - n[1] * a[0]);
                OrbitE1[k] = a; OrbitE2[k] = b;
            }
            e1 = OrbitE1[k]; e2 = OrbitE2[k];
        }

        static Target Atom(int i, int n, FastRandom rnd)
        {
            double pick = rnd.NextDouble();
            if (pick < 0.22)
            {
                int k = rnd.Next(Nucleons.Length);
                double[] c = Nucleons[k];
                double x = c[0] + rnd.Gauss() * 0.045, y = c[1] + rnd.Gauss() * 0.045, z = c[2] + rnd.Gauss() * 0.045;
                return k % 2 == 0 ? new Target(x, y, z, 255, 80, 70) : new Target(x, y, z, 90, 140, 255);
            }
            double[] e1, e2;
            if (pick < 0.84)
            {
                int k = rnd.Next(3);
                OrbitBasis(k, out e1, out e2);
                double a = rnd.NextDouble() * 2 * Math.PI, ca = Math.Cos(a) * 0.95, sa = Math.Sin(a) * 0.95;
                return new Target(e1[0] * ca + e2[0] * sa + rnd.Gauss() * 0.01,
                                  e1[1] * ca + e2[1] * sa + rnd.Gauss() * 0.01,
                                  e1[2] * ca + e2[2] * sa + rnd.Gauss() * 0.01, 70, 150, 200);
            }
            int ek = i % 3;
            OrbitBasis(ek, out e1, out e2);
            return new Target(e1[0] * 0.95 + rnd.Gauss() * 0.04, e1[1] * 0.95 + rnd.Gauss() * 0.04,
                              e1[2] * 0.95 + rnd.Gauss() * 0.04, 200, 255, 255).T(ek + 1);
        }

        // Terra: oceanos, continentes procedurais, gelo nos polos, atmosfera e a Lua orbitando
        static Target Earth(int i, int n, FastRandom rnd)
        {
            const double R = 0.78;
            int surface = (int)(n * 0.84), halo = (int)(n * 0.08);
            if (i < surface)
            {
                double x, y, z;
                Fibonacci(i, surface, out x, out y, out z);
                double land = Math.Sin(3.1 * x + 1.3) * Math.Cos(2.7 * z - 0.4) + 0.6 * Math.Sin(5.3 * y + 2.1 * x)
                            + 0.4 * Math.Cos(7.1 * z + 3.3 * y) + 0.3 * Math.Sin(9.7 * x - 4.1 * z);
                if (Math.Abs(y) > 0.88) return new Target(x * R, y * R, z * R, 235, 245, 255);
                if (land > 0.35)
                {
                    bool desert = Math.Abs(y) < 0.3 && Math.Sin(11 * x + 5 * z) > 0.3;
                    return desert ? new Target(x * R, y * R, z * R, 205, 175, 95)
                                  : new Target(x * R, y * R, z * R, 60, 165, 70);
                }
                double deep = Clamp01(0.35 - land);
                return Mix(x * R, y * R, z * R, 40, 120, 235, 15, 55, 170, deep);
            }
            if (i < surface + halo)
            {
                double x, y, z;
                RandomDir(rnd, out x, out y, out z);
                return new Target(x * 0.86, y * 0.86, z * 0.86, 70, 120, 170);
            }
            double mx = 1.25 + rnd.Gauss() * 0.08, my = 0.15 + rnd.Gauss() * 0.08, mz = rnd.Gauss() * 0.08;
            return new Target(mx, my, mz, 200, 200, 210).N(mx - 1.25, my - 0.15, mz).T(1);
        }

        // Diamante lapidado: coroa (mesa + facetas), cintura e pavilhão até a ponta
        static readonly double[][] Girdle = Ring(8, 0.12, 0.82, Math.PI / 8);
        static readonly double[][] Table = Ring(8, 0.42, 0.48, Math.PI / 8);
        static readonly double[] Culet = { 0, -0.85, 0 }, TableCenter = { 0, 0.42, 0 }, GemCenter = { 0, 0, 0 };

        static double[][] Ring(int count, double y, double r, double phase)
        {
            var v = new double[count][];
            for (int k = 0; k < count; k++)
            {
                double a = phase + k * 2 * Math.PI / count;
                v[k] = V(Math.Cos(a) * r, y, Math.Sin(a) * r);
            }
            return v;
        }

        static Target Diamond(int i, int n, FastRandom rnd)
        {
            double[] p, nrm;
            int k = rnd.Next(8), k1 = (k + 1) % 8;
            double pick = rnd.NextDouble();
            if (pick < 0.32)
            {
                int e = rnd.Next(4);
                if (e == 0) p = Lerp(Girdle[k], Girdle[k1], rnd.NextDouble());
                else if (e == 1) p = Lerp(Table[k], Table[k1], rnd.NextDouble());
                else if (e == 2) p = Lerp(Girdle[k], Table[k], rnd.NextDouble());
                else p = Lerp(Girdle[k], Culet, rnd.NextDouble());
                return new Target(p[0], p[1], p[2], 240, 250, 255);
            }
            if (pick < 0.62)
            {
                bool first = rnd.Next(2) == 0;
                p = first ? InTriangle(rnd, Girdle[k], Girdle[k1], Table[k1]) : InTriangle(rnd, Girdle[k], Table[k1], Table[k]);
                nrm = FaceNormal(Girdle[k], Girdle[k1], Table[k1], GemCenter);
            }
            else if (pick < 0.75)
            {
                p = InTriangle(rnd, TableCenter, Table[k], Table[k1]);
                nrm = V(0, 1, 0);
            }
            else
            {
                p = InTriangle(rnd, Girdle[k], Girdle[k1], Culet);
                nrm = FaceNormal(Girdle[k], Girdle[k1], Culet, GemCenter);
            }
            // "Fogo" do diamante: cada faceta puxa para uma cor do arco-íris
            double r, g, b;
            Hsv(Math.Atan2(nrm[2], nrm[0]) / (2 * Math.PI) + nrm[1] * 0.3, 0.3, 1.0, out r, out g, out b);
            return new Target(p[0], p[1], p[2], r * 0.85, g * 0.92, b).N(nrm[0], nrm[1], nrm[2]);
        }

        // Árvore de Natal: três camadas de folhagem, festão dourado, bolas, estrela e tronco
        static Target Tree(int i, int n, FastRandom rnd)
        {
            double pick = rnd.NextDouble();
            if (pick < 0.62)
            {
                double w = rnd.NextDouble();
                int tier = w < 0.45 ? 0 : (w < 0.8 ? 1 : 2);
                double yb = -0.55 + tier * 0.45, yt = yb + 0.75, rb = 0.8 - tier * 0.2;
                double h = 1 - Math.Sqrt(rnd.NextDouble()), a = rnd.NextDouble() * 2 * Math.PI;
                double rad = rb * (1 - h) * (1 + 0.08 * Math.Sin(a * 9)) * (1 - 0.15 * rnd.NextDouble());
                double y = yb + h * (yt - yb);
                double sh = 0.75 + 0.25 * rnd.NextDouble();
                return new Target(Math.Cos(a) * rad, y, Math.Sin(a) * rad, 45 * sh, 160 * sh, 65 * sh)
                    .N(Math.Cos(a), 0.6, Math.Sin(a));
            }
            if (pick < 0.72)
            {
                double s = rnd.NextDouble(), y = -0.55 + s * 1.5, a = s * 5 * 2 * Math.PI;
                double rad = 0.82 * (1 - s) + 0.04;
                return new Target(Math.Cos(a) * rad, y, Math.Sin(a) * rad, 255, 210, 80);
            }
            if (pick < 0.83)
            {
                int ball = rnd.Next(22);
                double s = ((ball * 0.618034) % 1) * 0.85, a = ball * 2.39996;
                double y = -0.5 + s * 1.35, rad = 0.8 * (1 - s) + 0.05;
                double bx = Math.Cos(a) * rad + rnd.Gauss() * 0.035, by = y + rnd.Gauss() * 0.035, bz = Math.Sin(a) * rad + rnd.Gauss() * 0.035;
                switch (ball % 4)
                {
                    case 0: return new Target(bx, by, bz, 255, 50, 60);
                    case 1: return new Target(bx, by, bz, 70, 130, 255);
                    case 2: return new Target(bx, by, bz, 230, 230, 245);
                    default: return new Target(bx, by, bz, 255, 200, 40);
                }
            }
            if (pick < 0.9)
            {
                int k = rnd.Next(10);
                double[] p = InTriangle(rnd, V(0, -0.05, 0), StarRim[k], StarRim[(k + 1) % 10]);
                return new Target(p[0] * 0.17, 1.02 + p[1] * 0.17, rnd.Gauss() * 0.02, 255, 240, 120).N(0, 0, -1);
            }
            double ta = rnd.NextDouble() * 2 * Math.PI;
            return new Target(Math.Cos(ta) * 0.12, -0.95 + rnd.NextDouble() * 0.4, Math.Sin(ta) * 0.12, 130, 80, 40)
                .N(Math.Cos(ta), 0, Math.Sin(ta));
        }

        static Target Tornado(int i, int n, FastRandom rnd)
        {
            if (rnd.NextDouble() < 0.08)
            {
                double a = rnd.NextDouble() * 2 * Math.PI, r = 0.2 + rnd.NextDouble() * 0.5;
                return new Target(Math.Cos(a) * r, -1 + rnd.NextDouble() * 0.15, Math.Sin(a) * r, 150, 120, 85);
            }
            double h = rnd.NextDouble(), y = -1 + 2 * h, rad = (0.06 + 0.62 * Math.Pow(h, 1.7)) * (1 + rnd.Gauss() * 0.12);
            double ang = rnd.NextDouble() * 2 * Math.PI;
            return Mix(Math.Cos(ang) * rad, y, Math.Sin(ang) * rad, 105, 115, 140, 205, 215, 235, h)
                .N(Math.Cos(ang), 0.3, Math.Sin(ang));
        }

        static Target Waves(int i, int n, FastRandom rnd)
        {
            double x = (rnd.NextDouble() * 2 - 1) * 1.15, z = (rnd.NextDouble() * 2 - 1) * 1.15;
            double r, g, b;
            Hsv(0.5 + 0.12 * (x + 1.15) / 2.3 + 0.05 * z, 0.75, 1.0, out r, out g, out b);
            return new Target(x, 0, z, r, g, b).N(0, 1, 0);
        }
    }
}
