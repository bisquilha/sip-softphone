# Criado pelo Claude em 10/11/2026; reescrito em 11/11/2026 (pedido do Ana: o app deve abrir sozinho depois de instalar/atualizar e no logon).
# Chamado pelo instalador (Softphone.iss). Acoes:
#   Instalar: tira as tarefas antigas e ABRE o Softphone AGORA na sessao de quem esta logado (instalacao interativa ou silenciosa pela Loja/SYSTEM).
#   Remover : tira as tarefas antigas.
# O inicio com o Windows (logon de qualquer usuario) agora e uma chave HKLM\...\Run gravada pelo proprio instalador, sem tarefa agendada.
# Motivo: a tarefa "No logon" criada para o grupo Usuarios NUNCA iniciava sob demanda (erro 0x80070057, evento 101 "(default user)"), entao
# o app nao abria depois da instalacao. Aqui a abertura usa uma tarefa de uso unico no usuario logado (Interactive), que o Windows aceita.
# Se o app ja estiver aberto, a segunda copia sai em silencio (App.xaml.cs, argumento --manter).
param([Parameter(Mandatory)][ValidateSet('Instalar', 'Remover')][string]$Acao, [string]$Exe)
$ErrorActionPreference = 'Stop'
foreach ($antiga in 'Softphone (manter aberto)', 'Softphone (manter aberto)', 'Softphone (abrir agora)') {
    Unregister-ScheduledTask -TaskName $antiga -Confirm:$false -ErrorAction SilentlyContinue
}
if ($Acao -eq 'Remover') { exit 0 }

try {
    # quem esta no console (nulo se ninguem estiver logado, por exemplo so por RDP ou com a maquina no logon: entao o Run abre no proximo logon)
    $usuario = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
    if (-not $usuario) { exit 0 }
    $comando = New-ScheduledTaskAction -Execute $Exe -Argument '--minimizado --manter'
    $quem = New-ScheduledTaskPrincipal -UserId $usuario -LogonType Interactive -RunLevel Limited
    $cfg = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    $agora = 'Softphone (abrir agora)'
    Register-ScheduledTask -TaskName $agora -Action $comando -Principal $quem -Settings $cfg -Description 'Abre o Softphone uma vez, logo depois da instalacao (Minha Empresa TI).' -Force | Out-Null
    Start-ScheduledTask -TaskName $agora
    Start-Sleep -Seconds 6
    Unregister-ScheduledTask -TaskName $agora -Confirm:$false -ErrorAction SilentlyContinue
} catch {
    # nunca derruba a instalacao: no pior caso o app abre no proximo logon pela chave Run
    exit 0
}
