using Softphone.Core;

/// <summary>
/// Aviso de gravação na conferência a três (Claude Code, 05/11/2026 — achado R04 da revisão do Codex): quem grava uma
/// perna grava as duas vozes, então o aviso de qualquer perna tem que chegar às duas.
/// </summary>
static class TesteAvisoConferencia
{
    /// <summary>Áudio de mentira: guarda o último quadro codificado que sairia para o PABX.</summary>
    sealed class Sonda : AudioChamadaBase
    {
        public byte[]? Ultimo;
        public Sonda() { OnAudioSourceEncodedSample += (_, bytes) => Ultimo = bytes; }
        public void Emitir() => EnviarPcm(new short[160]); // microfone em silêncio: só o aviso pode fazer barulho
        public override Task StartAudio() => Task.CompletedTask;
        public override Task CloseAudio() => Task.CompletedTask;
        public override Task StartAudioSink() => Task.CompletedTask;
        public override Task CloseAudioSink() => Task.CompletedTask;
        protected override void TocarPcm(short[] pcm, int taxa) { }
    }

    // silêncio em G.711 A-law (PCMA) = 0xD5
    static bool TemSom(byte[]? quadro) => quadro != null && quadro.Any(b => b != 0xD5);

    public static void Rodar(Action<bool, string> ok)
    {
        var aviso = Enumerable.Repeat((short)8000, 3200).ToArray();

        // gravação começa na segunda perna: as duas ouvem o aviso
        using (var principal = new Sonda())
        using (var segunda = new Sonda())
        {
            principal.IniciarConferencia(segunda);
            segunda.TocarAviso(aviso);
            principal.Emitir();
            ok(TemSom(segunda.Ultimo) && TemSom(principal.Ultimo), $"gravação na 2ª perna: o aviso chega às duas pernas (2ª: {TemSom(segunda.Ultimo)}, 1ª: {TemSom(principal.Ultimo)})");
        }

        // gravação começa na principal: a segunda perna também ouve
        using (var principal = new Sonda())
        using (var segunda = new Sonda())
        {
            principal.IniciarConferencia(segunda);
            principal.TocarAviso(aviso);
            principal.Emitir();
            ok(TemSom(segunda.Ultimo) && TemSom(principal.Ultimo), $"gravação na 1ª perna: o aviso chega às duas pernas (2ª: {TemSom(segunda.Ultimo)}, 1ª: {TemSom(principal.Ultimo)})");
        }

        // fora da conferência continua como antes, e sem aviso não sai barulho
        using (var so = new Sonda())
        {
            so.Emitir();
            bool mudoSemAviso = !TemSom(so.Ultimo);
            so.TocarAviso(aviso);
            so.Emitir();
            ok(mudoSemAviso && TemSom(so.Ultimo), "ligação comum: sem aviso fica em silêncio, com aviso o outro lado ouve");
        }
    }
}
