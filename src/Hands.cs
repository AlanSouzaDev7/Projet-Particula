using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Particulas
{
    sealed class Hand
    {
        public string Pose;
        public char Side;                                // 'L' ou 'R' (só para identificar a mão)
        public double Conf;                              // confiança da pose (0..1)
        public string Alt;                               // pose mais provável (mesmo se Pose = "outra")
        public string Quality;                           // "ok", "longe" ou "borda"
        public double Cx, Cy, Size;                      // centro da palma (0..1, espelhado) e tamanho
        public double PinchX, PinchY;                    // ponto entre o polegar e o indicador
        public double TipX, TipY;                        // ponta do indicador
        public readonly double[] Pts = new double[42];   // 21 pontos (x, y) em 0..1
    }

    sealed class HandFrame
    {
        public static readonly HandFrame Empty = new HandFrame();
        public readonly List<Hand> Hands = new List<Hand>();
        public string Duo = "-";                         // gesto feito com as duas mãos juntas
    }

    // Inicia o maos.py (Python + MediaPipe) e lê, em segundo plano, as mãos que ele detecta.
    // Protocolo (uma linha por quadro):
    //   F <n> <duo>|<pose> <lado> <conf> <provável> <qualidade> <cx> <cy> <tam> <pinçaX> <pinçaY>
    //               <pontaX> <pontaY> <x0> <y0> ... <x20> <y20>|...
    sealed class HandTracker : IDisposable
    {
        const int Fields = 12 + 42;
        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process proc;
        HandFrame latest = HandFrame.Empty;
        double stamp = -10;
        volatile string status = "iniciando a câmera...";
        volatile bool ready, failed;
        volatile string lastError = "";
        volatile string simTruth = "";
        int framesReceived;

        public string LastError { get { return lastError; } }
        public string SimTruth { get { return simTruth; } }      // pose que a simulação está enviando
        public string Status { get { return status; } }
        public bool Ready { get { return ready; } }
        public bool Failed { get { return failed; } }
        public int FramesReceived { get { return framesReceived; } }

        public static string Home
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Particulas"); }
        }

        // simulate = true: o rastreador não usa a câmera e envia mãos sintéticas em todas as poses
        public void Start(bool simulate = false)
        {
            string python = Path.Combine(Home, @"venv\Scripts\python.exe");
            string model = Path.Combine(Home, "hand_landmarker.task");
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maos.py");
            if (!File.Exists(script)) script = Path.Combine(Home, "maos.py");
            if (!File.Exists(python) || !File.Exists(script) || (!simulate && !File.Exists(model)))
            {
                Fail("câmera desligada: rode instalar.bat");
                return;
            }

            var psi = new ProcessStartInfo(python, "-u \"" + script + "\" " +
                (simulate ? "--simular" : "--modelo \"" + model + "\""));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;    // isola o Python da entrada do console deste programa
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["GLOG_minloglevel"] = "2";
            psi.EnvironmentVariables["TF_CPP_MIN_LOG_LEVEL"] = "2";
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                // Logs do MediaPipe são descartados; guardamos só a última linha de erro de verdade
                proc.ErrorDataReceived += (s, e) =>
                {
                    string l = e.Data;
                    if (string.IsNullOrWhiteSpace(l) || l.StartsWith("W0000") || l.StartsWith("I0000") ||
                        l.StartsWith("INFO") || l.StartsWith("WARNING")) return;
                    lastError = l.Trim();
                };
                proc.Exited += (s, e) =>
                {
                    Thread.Sleep(200);   // dá tempo de o stderr terminar de chegar
                    if (!failed) Fail("o rastreador de mãos parou" + (lastError.Length > 0 ? ": " + lastError : ""));
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Fail("não consegui iniciar o Python (" + ex.Message + ")");
            }
        }

        void Fail(string msg)
        {
            status = msg;
            failed = true;
            lock (gate) latest = HandFrame.Empty;
        }

        void OnLine(string line)
        {
            if (line.StartsWith("F "))
            {
                var frame = new HandFrame();
                string[] parts = line.Split('|');
                string[] head = parts[0].Split(' ');
                if (head.Length >= 3) frame.Duo = head[2];
                for (int k = 1; k < parts.Length; k++)
                {
                    string[] t = parts[k].Split(' ');
                    if (t.Length < Fields) continue;
                    var h = new Hand
                    {
                        Pose = t[0], Side = t[1].Length > 0 ? t[1][0] : '?',
                        Conf = Num(t[2]), Alt = t[3], Quality = t[4],
                        Cx = Num(t[5]), Cy = Num(t[6]), Size = Num(t[7]),
                        PinchX = Num(t[8]), PinchY = Num(t[9]), TipX = Num(t[10]), TipY = Num(t[11]),
                    };
                    for (int j = 0; j < 42; j++) h.Pts[j] = Num(t[12 + j]);
                    frame.Hands.Add(h);
                }
                lock (gate) { latest = frame; stamp = clock.Elapsed.TotalSeconds; }
                Interlocked.Increment(ref framesReceived);
                if (!ready) { ready = true; status = "câmera ativa"; }   // (normalmente o "OK" já chegou antes)
            }
            else if (line.StartsWith("OK"))
            {
                ready = true;
                status = line.Contains("simulacao") ? "simulação" : (line.Contains("perfil") ? "câmera ativa (perfil pessoal)" : "câmera ativa");
            }
            else if (line.StartsWith("S ")) simTruth = line.Substring(2).Trim();
            else if (line.StartsWith("ERR ")) Fail(line.Substring(4));
        }

        static double Num(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Último quadro recebido e há quantos segundos chegou
        public HandFrame Snapshot(out double age)
        {
            lock (gate)
            {
                age = clock.Elapsed.TotalSeconds - stamp;
                return latest;
            }
        }

        public void Dispose()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    // Filtro "One Euro" (Casiez et al., 2012): suaviza muito quando a mão está parada
    // (tira o tremor) e pouco quando ela se move rápido (sem atraso perceptível).
    sealed class OneEuro
    {
        readonly double minCutoff, beta, dCutoff;
        double x, dx;
        bool init;

        public OneEuro(double minCutoff, double beta, double dCutoff)
        {
            this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff;
        }

        static double Alpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }

        public double Filter(double value, double dt)
        {
            if (!init) { init = true; x = value; dx = 0; return value; }
            double d = (value - x) / dt;
            dx += Alpha(dCutoff, dt) * (d - dx);
            double cutoff = minCutoff + beta * Math.Abs(dx);
            x += Alpha(cutoff, dt) * (value - x);
            return x;
        }

        public void Reset() { init = false; }
    }
}
