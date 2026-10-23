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
                if (a == "--testar-camera") return TestCamera();
            }
            return new App().Run();
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
