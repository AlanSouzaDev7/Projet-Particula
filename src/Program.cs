// =====================================================================================
//  Partículas 3D — pontos que se transformam em objetos a partir de gestos da mão
//  (câmera), traços desenhados no ar ou com o mouse.
//
//  Uso:
//    Particulas.exe                  -> abre a animação
//    Particulas.exe --testar-camera  -> liga o rastreador de mãos e mostra o que ele vê
//    Particulas.exe --selftest       -> testa o reconhecedor de traços
//    Particulas.exe --benchmark      -> mede o custo por quadro e a banda enviada ao terminal
//    Particulas.exe --preview        -> imprime uma prévia em texto de cada forma
//    Particulas.exe --testar-camera --simular  -> integração completa com mãos sintéticas
//    Particulas.exe --verificar-render         -> confere a saída diferencial do terminal
//    Particulas.exe --verificar-fundo          -> mede se o céu atrapalha as figuras
//    Particulas.exe --exportar-png arquivo.png -> salva uma imagem com 6 figuras
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Particulas
{
    static class Program
    {
        static int Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a == "--selftest") return SelfTest.Run();
                if (a == "--preview") { App.Preview(); return 0; }
                if (a == "--benchmark") { App.Benchmark(); return 0; }
                if (a == "--verificar-render") return App.VerifyRender();
                if (a == "--verificar-fundo") return App.VerifyBackground();
                if (a == "--exportar-png")
                {
                    App.ExportPng(args.Length > 1 ? args[args.Length - 1] : "previa.png");
                    return 0;
                }
                if (a == "--testar-camera")
                    return Array.IndexOf(args, "--simular") >= 0 ? TestSimulated() : TestCamera();
            }
            return new App().Run();
        }

        // Integração completa sem câmera: o rastreador envia mãos sintéticas pose por pose
        // (avisando qual está enviando) e conferimos o que chega aqui, já interpretado.
        static int TestSimulated()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            using (var t = new HandTracker())
            {
                t.Start(true);
                var sw = Stopwatch.StartNew();
                string truth = "";
                double since = 0;
                var total = new SortedDictionary<string, int>();
                var right = new SortedDictionary<string, int>();
                var wrong = new SortedDictionary<string, string>();
                double confSum = 0;
                int confN = 0;
                while (sw.Elapsed.TotalSeconds < 60 && !t.Failed)
                {
                    if (t.SimTruth != truth) { truth = t.SimTruth; since = sw.Elapsed.TotalSeconds; }
                    double age;
                    HandFrame f = t.Snapshot(out age);
                    // Ignora o primeiro meio segundo de cada pose (a fusão temporal ainda está trocando)
                    if (truth.Length > 0 && age < 0.3 && f.Hands.Count > 0 && sw.Elapsed.TotalSeconds - since > 0.5)
                    {
                        Hand h = f.Hands[0];
                        int c;
                        total.TryGetValue(truth, out c); total[truth] = c + 1;
                        if (h.Pose == truth)
                        {
                            right.TryGetValue(truth, out c); right[truth] = c + 1;
                            confSum += h.Conf; confN++;
                        }
                        else wrong[truth] = h.Pose + (h.Quality != "ok" ? "/" + h.Quality : "");
                    }
                    Thread.Sleep(40);
                }
                int all = 0, ok = 0;
                foreach (var kv in total)
                {
                    int r;
                    right.TryGetValue(kv.Key, out r);
                    all += kv.Value; ok += r;
                    string w;
                    wrong.TryGetValue(kv.Key, out w);
                    Console.WriteLine("  " + kv.Key.PadRight(10) + (100.0 * r / kv.Value).ToString("0").PadLeft(4) + "%" +
                                      (w != null && r < kv.Value ? "   (às vezes: " + w + ")" : ""));
                }
                Console.WriteLine("poses simuladas: " + total.Count + "   acerto após estabilizar: " +
                                  (100.0 * ok / Math.Max(1, all)).ToString("0.0") + "%   confiança média: " +
                                  (100.0 * confSum / Math.Max(1, confN)).ToString("0") + "%");
                return total.Count >= 17 && ok >= 0.9 * all ? 0 : 1;
            }
        }

        // Liga o rastreador por alguns segundos e mostra o que chega dele (sem abrir a animação)
        static int TestCamera()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            using (var t = new HandTracker())
            {
                t.Start();
                var sw = Stopwatch.StartNew();
                string lastStatus = "";
                double readyAt = -1;
                int withHands = 0, samples = 0;
                var poses = new SortedDictionary<string, int>();
                while (sw.Elapsed.TotalSeconds < 20 && !t.Failed)
                {
                    if (t.Status != lastStatus) { lastStatus = t.Status; Console.WriteLine("estado: " + lastStatus); }
                    if (t.Ready)
                    {
                        if (readyAt < 0) readyAt = sw.Elapsed.TotalSeconds;
                        double age;
                        HandFrame f = t.Snapshot(out age);
                        samples++;
                        if (f.Hands.Count > 0 && age < 0.5) withHands++;
                        foreach (var h in f.Hands)
                        {
                            int c;
                            poses.TryGetValue(h.Pose, out c);
                            poses[h.Pose] = c + 1;
                        }
                        if (f.Duo != "-")
                        {
                            int c;
                            poses.TryGetValue("[2 mãos] " + f.Duo, out c);
                            poses["[2 mãos] " + f.Duo] = c + 1;
                        }
                        if (sw.Elapsed.TotalSeconds - readyAt > 6) break;
                    }
                    Thread.Sleep(50);
                }
                Console.WriteLine("estado final: " + t.Status);
                if (t.LastError.Length > 0) Console.WriteLine("último erro do Python: " + t.LastError);
                if (readyAt >= 0)
                {
                    double span = sw.Elapsed.TotalSeconds - readyAt;
                    Console.WriteLine("câmera pronta em " + readyAt.ToString("0.0") + " s; " +
                                      (t.FramesReceived / Math.Max(0.1, span)).ToString("0.0") + " quadros/s do rastreador");
                }
                Console.WriteLine("amostras: " + samples + ", com mão: " + withHands);
                foreach (var kv in poses) Console.WriteLine("  pose " + kv.Key + ": " + kv.Value);
                return t.Failed ? 1 : 0;
            }
        }
    }
}
