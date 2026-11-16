using System;
using System.Collections.Generic;

namespace Softphone.App;

/// <summary>
/// "Agendar no Outlook" (1.1.3, Claude Code): abre um compromisso novo no Outlook clássico deste PC, já como reunião,
/// com os convidados, o link e o texto. A pessoa só ajusta data/hora e envia pelo próprio Outlook: nada passa pelo
/// servidor nem pelo Exchange. O "novo Outlook" não aceita esse tipo de automação; aí quem chama copia o link.
/// </summary>
public static class OutlookReuniao
{
    public static bool Abrir(string assunto, string local, string texto, IEnumerable<string> emails, out string? erro)
    {
        erro = null;
        try
        {
            var tipo = Type.GetTypeFromProgID("Outlook.Application");
            if (tipo is null) { erro = "o Outlook clássico não está instalado neste computador"; return false; }
            dynamic ol = Activator.CreateInstance(tipo)!;
            dynamic item = ol.CreateItem(1);   // olAppointmentItem
            item.MeetingStatus = 1;            // olMeeting: vira convite com participantes
            item.Subject = assunto;
            item.Location = local;
            item.Body = texto;
            // próxima meia hora cheia, 30 minutos (a pessoa ajusta)
            var agora = DateTime.Now;
            var inicio = agora.Date.AddHours(agora.Hour).AddMinutes(agora.Minute < 30 ? 30 : 60);
            item.Start = inicio;
            item.Duration = 30;
            foreach (var e in emails)
            {
                dynamic r = item.Recipients.Add(e);
                r.Type = 1;                    // olRequired
            }
            try { item.Recipients.ResolveAll(); } catch { }
            item.Display(false);
            return true;
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            Log.Escrever("Agendar no Outlook: " + ex);
            return false;
        }
    }
}
