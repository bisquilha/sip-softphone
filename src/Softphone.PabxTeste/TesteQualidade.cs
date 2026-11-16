using Softphone.Core;

/// <summary>Medidor de qualidade com relógio de mentira (sem rede): perda, jitter, RTT do RTCP e "sem áudio".</summary>
static class TesteQualidade
{
    public static void Rodar(Action<bool, string> ok)
    {
        // 1) rede perfeita: 20 ms entre pacotes, nada perdido
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            ushort seq = 65000; uint ts = 1000;
            for (int i = 0; i < 300; i++) { m.Pacote(seq, ts); seq++; ts += 160; agora += 20; if (i % 50 == 0) m.Instante(); }
            var q = m.Instante();
            ok(q.PerdaPct < 0.1 && q.JitterMs < 1 && q.Nivel == NivelQualidade.Boa && q.Perdidos == 0, $"perfeita (com volta do número de sequência): {q.Texto}");
        }
        // 2) 10% de perda
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            ushort seq = 1; uint ts = 0;
            for (int i = 0; i < 500; i++) { if (i % 10 != 3) m.Pacote(seq, ts); seq++; ts += 160; agora += 20; if (i % 50 == 0) m.Instante(); }
            var q = m.Instante();
            ok(q.PerdaPct is > 8 and < 12 && q.Nivel == NivelQualidade.Ruim, $"10% de perda: {q.Texto}");
            var r = m.Resumo();
            ok(r != null && r.PerdaPct is > 8 and < 12, $"resumo da ligação: perda {r?.PerdaPct:N1}%, nota média {r?.MosMedio:N2}");
        }
        // 3) jitter: chegada alterna 10 ms adiantada / atrasada
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            ushort seq = 1; uint ts = 0;
            for (int i = 0; i < 400; i++) { agora = i * 20 + (i % 2 == 0 ? 0 : 20); m.Pacote(seq++, ts); ts += 160; }
            var q = m.Instante();
            ok(q.JitterMs is > 12 and < 25, $"variação medida: {q.JitterMs:N1} ms (esperado ~20)");
        }
        // 4) sem áudio por mais de 4 s; em espera não conta
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            m.Pacote(1, 0); agora = 5000;
            ok(m.Instante().Nivel == NivelQualidade.SemAudio, "parou de chegar áudio: 'sem áudio chegando'");
            m.Pausado = true;
            ok(m.Instante().Nivel != NivelQualidade.SemAudio, "em espera não acusa falta de áudio");
        }
        // 5) RTT pelo relatório RTCP (LSR/DLSR): SR enviado há 300 ms, o outro lado segurou 100 ms -> RTT 200 ms
        {
            var m = new MedidorQualidade();
            var envio = DateTime.UtcNow.AddMilliseconds(-300);
            uint lsr = MedidorQualidade.NtpMeio(envio);
            uint dlsr = (uint)(0.100 * 65536);
            m.Pacote(1, 0);
            m.RelatorioRemoto(26, lsr, dlsr, DateTime.UtcNow);
            var q = m.Instante();
            ok(q.RttMs is > 180 and < 220 && q.PerdaRemotaPct is > 9 and < 11, $"ida e volta {q.RttMs:N0} ms, o outro lado perdeu {q.PerdaRemotaPct:N1}% do meu áudio");
        }
        // 6) nota: tabela do modelo E
        ok(MedidorQualidade.Mos(0, 5, null) > 4.3 && MedidorQualidade.Mos(10, 20, 300) < 3.6, $"nota: limpa {MedidorQualidade.Mos(0, 5, null):N2}, ruim {MedidorQualidade.Mos(10, 20, 300):N2}");

        // 7) R11 (revisão do Codex): 5 s boas e depois 15 s sem áudio -> o resumo não pode dizer "Boa"
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            for (ushort i = 0; i < 250; i++) { m.Pacote(i, (uint)i * 160); agora += 20; if (i % 50 == 49) m.Instante(); }
            for (int i = 0; i < 15; i++) { agora += 1000; m.Instante(); }
            var instante = m.Instante();
            var r = m.Resumo();
            ok(instante.Nivel == NivelQualidade.SemAudio && r != null && r.Nivel != NivelQualidade.Boa,
                $"áudio sumiu no meio: o resumo deixa de ser 'Boa' (agora {instante.Nivel}; resumo {r?.Nivel}, nota {r?.MosMedio:N2})");
        }
        // 8) nunca chegou áudio: depois do tempo de medição vira "sem áudio" (não fica "sem dados" para sempre)
        {
            long agora = 0;
            var m = new MedidorQualidade(() => agora);
            m.Instante();
            agora = 8000;
            var q = m.Instante();
            ok(q.Nivel == NivelQualidade.SemAudio, $"ligação que nunca recebeu áudio acusa 'sem áudio' ({q.Nivel})");
        }
    }
}
