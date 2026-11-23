using System.Text;

namespace MonitorDial;

/// <summary>
/// Teste de ponta a ponta no monitor real: escreve valores em cada controle do catálogo,
/// confere a leitura e restaura o estado original. Usa a mesma camada DDC do app.
/// </summary>
public static class SelfTest
{
    public static int Run(string reportPath)
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string text)
        {
            if (ok) pass++; else fail++;
            log.AppendLine((ok ? "PASSOU  " : "FALHOU  ") + text);
            try { File.WriteAllText(reportPath + ".parcial", log.ToString()); } catch { }
        }

        try
        {
            var monitors = DdcMonitor.Enumerate();
            if (monitors.Count == 0)
            {
                File.WriteAllText(reportPath, "FALHOU  nenhum monitor com DDC/CI encontrado\nRESULTADO: 0 passaram, 1 falhou\n");
                return 2;
            }
            var mon = monitors[0];
            log.AppendLine($"Monitor: {mon.Description}");

            // Estado original de todos os controles.
            var original = new Dictionary<string, int>();
            var maxOf = new Dictionary<string, int>();
            foreach (var c in ControlCatalog.All)
            {
                bool ok = mon.TryGet(c.Code, out int cur, out int max);
                Check(ok, $"{c.Label} (0x{c.Code:X2}): leitura inicial = {cur}");
                if (ok) { original[c.Key] = cur; maxOf[c.Key] = max; }
            }

            foreach (var c in ControlCatalog.All)
            {
                if (!original.ContainsKey(c.Key)) continue;
                int orig = original[c.Key];

                // Com o volume em 0 o monitor se declara mudo e recusa sair do mudo.
                // O volume volta ao valor original na restauração final.
                if (c.Key == "mute" && mon.TryGet(0x62, out int volume, out _) && volume == 0)
                {
                    mon.TrySet(0x62, 10);
                    WaitForCode(mon, 0x62, 10, 2000);
                    mon.TryGet(c.Code, out orig, out _);
                }

                IEnumerable<int> values = c.Kind switch
                {
                    ControlKind.Range => RangeSamples(c, c.Max == 0 ? maxOf[c.Key] : c.Max, orig),
                    ControlKind.Choice => c.Options!.Select(o => o.Value)
                        .Where(v => c.NeedsFreeSyncOff is null || !c.NeedsFreeSyncOff.Contains(v)),
                    _ => new[] { orig == c.OnValue ? c.OffValue : c.OnValue, orig },
                };
                foreach (int v in values)
                {
                    bool set = mon.TrySet(c.Code, c.ToWire(v));
                    int read = WaitFor(mon, c, v);
                    Check(set && read == v, $"{c.Label} (0x{c.Code:X2}): escreveu {v}, leu {read}");
                }
                // Volta este controle ao original antes de seguir.
                mon.TrySet(c.Code, c.ToWire(orig));
                int back = WaitFor(mon, c, orig);
                Check(back == orig, $"{c.Label} (0x{c.Code:X2}): restaurado para {orig} (leu {back})");
            }

            // Opções que dependem do FreeSync: com ele ligado o monitor deve recusar,
            // com ele desligado deve aceitar.
            var freeSync = ControlCatalog.All.First(c => c.Key == "freeSync");
            foreach (var c in ControlCatalog.All.Where(c => c.NeedsFreeSyncOff is not null && original.ContainsKey(c.Key)))
            {
                int orig = original[c.Key];
                mon.TrySet(freeSync.Code, freeSync.OnValue);
                WaitFor(mon, freeSync, freeSync.OnValue);
                Thread.Sleep(freeSync.SettleMs);
                foreach (int v in c.NeedsFreeSyncOff!)
                {
                    mon.TrySet(c.Code, c.ToWire(v));
                    Thread.Sleep(c.SettleMs);
                    mon.TryGet(c.Code, out int read, out _);
                    Check(read != v, $"{c.Label} (0x{c.Code:X2}) com FreeSync ligado: {v} recusado como esperado (leu {read})");
                }
                mon.TrySet(freeSync.Code, freeSync.OffValue);
                WaitFor(mon, freeSync, freeSync.OffValue);
                Thread.Sleep(freeSync.SettleMs);   // o monitor ignora comandos enquanto ressincroniza
                foreach (int v in c.NeedsFreeSyncOff!)
                {
                    bool set = mon.TrySet(c.Code, c.ToWire(v));
                    int read = WaitFor(mon, c, v);
                    Check(set && read == v, $"{c.Label} (0x{c.Code:X2}) com FreeSync desligado: escreveu {v}, leu {read}");
                }
                mon.TrySet(c.Code, c.ToWire(orig));
                Check(WaitFor(mon, c, orig) == orig, $"{c.Label} (0x{c.Code:X2}): restaurado para {orig}");
                mon.TrySet(freeSync.Code, original[freeSync.Key]);
                Check(WaitFor(mon, freeSync, original[freeSync.Key]) == original[freeSync.Key], "FreeSync: restaurado");
            }

            // Comandos de restauração.
            mon.TrySet(0x10, 40);
            WaitForCode(mon, 0x10, 40, 1500);
            bool r1 = mon.TrySet(ControlCatalog.ResetLuminance, 1);
            Thread.Sleep(2000);
            mon.TryGet(0x10, out int bAfter, out _);
            Check(r1 && bAfter != 40, $"Restaurar brilho e contraste (0x05): brilho 40 -> {bAfter}");

            mon.TrySet(0x16, 40);
            WaitForCode(mon, 0x16, 40, 1500);
            bool r2 = mon.TrySet(ControlCatalog.ResetColor, 1);
            Thread.Sleep(2000);
            mon.TryGet(0x16, out int rAfter, out _);
            Check(r2 && rAfter != 40, $"Restaurar cor (0x08): vermelho 40 -> {rAfter}");

            // Restauração final de tudo. Ordem: modo de imagem primeiro; RGB antes da
            // temperatura de cor, porque escrever RGB troca a temperatura para "Personalizado".
            log.AppendLine("--- restauração final ---");
            var order = ControlCatalog.All
                .OrderBy(c => c.Key == "pictureMode" ? 0 : c.Key == "colorTemp" ? 2 : 1)
                .ToList();
            foreach (var c in order)
            {
                if (!original.ContainsKey(c.Key)) continue;
                mon.TryGet(c.Code, out int cur, out _);
                if (cur == original[c.Key] && c.Key != "colorTemp") continue;
                mon.TrySet(c.Code, c.ToWire(original[c.Key]));
                Thread.Sleep(Math.Max(c.SettleMs, 400));
            }
            Thread.Sleep(1500);
            foreach (var c in ControlCatalog.All)
            {
                if (!original.ContainsKey(c.Key)) continue;
                mon.TryGet(c.Code, out int cur, out _);
                Check(cur == original[c.Key], $"{c.Label}: estado final {cur} (original {original[c.Key]})");
            }
            mon.Dispose();
        }
        catch (Exception ex)
        {
            fail++;
            log.AppendLine("FALHOU  exceção: " + ex);
        }

        log.AppendLine($"RESULTADO: {pass} passaram, {fail} falharam");
        File.WriteAllText(reportPath, log.ToString());
        return fail == 0 ? 0 : 1;
    }

    private static IEnumerable<int> RangeSamples(VcpControl c, int max, int orig)
    {
        int Snap(int v) => v / c.Step * c.Step;
        var set = new List<int> { c.Min, Snap((c.Min + max) / 2), max, Snap(max * 3 / 10) };
        return set.Distinct();
    }

    private static int WaitFor(DdcMonitor mon, VcpControl c, int expected) =>
        WaitForCode(mon, c.Code, expected, Math.Max(c.SettleMs, 300) + 3000);

    private static int WaitForCode(DdcMonitor mon, byte code, int expected, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cur = -1;
        Thread.Sleep(250);
        do
        {
            if (mon.TryGet(code, out cur, out _) && cur == expected) return cur;
            Thread.Sleep(300);
        } while (sw.ElapsedMilliseconds < timeoutMs);
        return cur;
    }
}
