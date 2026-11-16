<#
    Softphone - troca do MicroSIP pelo Softphone em estacoes (Tactical RMM, GPO ou manual, como SYSTEM/administrador).

    Dois passos, nesta ordem (dois softphones no mesmo ramal se atrapalham, por isso o MicroSIP sai ANTES):
      1. REMOVE o MicroSIP de forma permanente: encerra o programa, desinstala (instalador ou MSI), apaga copias portateis,
         atalhos, inicializacao automatica (Run de todas as contas), tarefas agendadas, regras de firewall e as associacoes
         tel: / sip: / callto: que apontavam para ele. Chocolatey: "choco uninstall microsip", se for o caso.
      2. SO se o passo 1 deu certo: instala (ou atualiza) o Softphone pelo pacote do feed interno (Chocolatey).

    Seguro de rodar sempre: sem MicroSIP e com o Softphone em dia, nao faz nada. Rodando de novo (tarefa diaria no Tactical)
    ele tambem pega quem baixar o MicroSIP outra vez: assim a remocao e permanente.

    SEM -Executar o script so MOSTRA o que faria (simulacao). Estacoes apenas: em servidor sai sem fazer nada.
    A remocao e COMPLETA: inclui os dados de cada usuario (%APPDATA%\MicroSIP: configuracao com a senha SIP em texto e historico).
    Antes de apagar, o microsip.ini de cada usuario e copiado para C:\ProgramData\Softphone-Troca\copia-microsip\<usuario>\ (so
    administradores e SYSTEM leem; apague a pasta quando nao precisar mais). -SemCopia dispensa a copia; -ManterDados nao apaga os dados.
    A conta do Softphone vem do AD (ramal, login e senha), entao quem tem ramal cadastrado la nao perde nada.

    Codigos de saida: 0 ok | 1 erro em alguma acao | 2 MicroSIP ainda presente (nao instalou) | 3 Chocolatey ausente | 4 Softphone nao ficou instalado
    Log: C:\ProgramData\Softphone-Troca.log
    Arquivo sem acentos de proposito (o PowerShell 5.1 le script sem BOM como ANSI).
#>
param(
    [switch]$Executar,
    [switch]$ManterDados,        # nao apaga %APPDATA%\MicroSIP (por padrao a remocao e COMPLETA, dados inclusos)
    [switch]$SemCopia,           # nao guarda a copia do microsip.ini antes de apagar os dados
    [switch]$SoRemover,
    [string]$Pacote = 'corp-softphone',
    [string]$Feed = '\\192.0.2.246\infra\loja-pacotes',
    [string]$VersaoMinima = '1.2.9',
    [string]$RaizTeste = ''      # so para testes: pasta extra onde procurar MicroSIP
)

$ErrorActionPreference = 'Continue'
$script:erros = 0
$script:achou = 0
$script:log = Join-Path $env:ProgramData 'Softphone-Troca.log'
try { Add-Content -Path $script:log -Value '' -ErrorAction Stop } catch { $script:log = Join-Path $env:TEMP 'Softphone-Troca.log' }
try { if ((Test-Path $script:log) -and (Get-Item $script:log).Length -gt 300KB) { Remove-Item $script:log -Force } } catch { }

function Escrever($texto) {
    $linha = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $texto
    try { Add-Content -Path $script:log -Value $linha } catch { }
    Write-Output $linha
}

# Faz (ou so mostra, na simulacao) uma acao. Conta como achado de MicroSIP.
function Acao($descricao, [scriptblock]$bloco) {
    $script:achou++
    if (-not $Executar) { Escrever "[simulacao] $descricao"; return }
    try { & $bloco; Escrever "feito: $descricao" }
    catch { $script:erros++; Escrever "ERRO em '$descricao': $($_.Exception.Message)" }
}

function Texto-Do-MicroSIP($valor) { return ("$valor" -match '(?i)microsip') }

# ---------------------------------------------------------------- 0. so estacoes
try {
    $tipo = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).ProductType
    if ($tipo -ne 1) { Escrever 'Este computador e um servidor: o Softphone so se instala em estacoes. Nada feito.'; exit 0 }
} catch { Escrever "Nao foi possivel conferir o tipo do Windows: $($_.Exception.Message)" }

Escrever ("inicio ({0}) em {1}" -f ($(if ($Executar) { 'EXECUTANDO' } else { 'SIMULACAO' }), $env:COMPUTERNAME))

# Pastas de perfil (sem os perfis do sistema).
$perfis = @(Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @('Public', 'Default', 'Default User', 'All Users') })

# ---------------------------------------------------------------- 1. MicroSIP
Escrever 'PASSO 1: remover o MicroSIP'

# 1.1 programa em execucao (qualquer sessao)
foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'microsip*' })) {
    $id = $p.Id
    Acao "encerrar o processo $($p.ProcessName) (PID $id)" { Stop-Process -Id $id -Force -ErrorAction Stop }
}

# 1.2 instalacoes registradas (maquina toda e, quando a conta esta carregada, por usuario)
$raizesUninstall = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($sid in @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^S-1-5-21-[0-9-]+$' })) {
    $raizesUninstall += "Registry::HKEY_USERS\$($sid.PSChildName)\Software\Microsoft\Windows\CurrentVersion\Uninstall"
}
foreach ($raiz in $raizesUninstall) {
    foreach ($chave in @(Get-ChildItem -Path $raiz -ErrorAction SilentlyContinue)) {
        $item = Get-ItemProperty -LiteralPath $chave.PSPath -ErrorAction SilentlyContinue
        if (-not $item -or -not (Texto-Do-MicroSIP $item.DisplayName)) { continue }
        $cmd = if ($item.QuietUninstallString) { $item.QuietUninstallString } else { $item.UninstallString }
        if (-not $cmd) { continue }
        $nome = "$($item.DisplayName) $($item.DisplayVersion)".Trim()
        Acao "desinstalar '$nome'" {
            if ($cmd -match '(?i)msiexec' -and $cmd -match '\{[0-9A-Fa-f-]{36}\}') {
                $guid = [regex]::Match($cmd, '\{[0-9A-Fa-f-]{36}\}').Value
                $proc = Start-Process msiexec.exe -ArgumentList "/x $guid /qn /norestart" -PassThru -WindowStyle Hidden
            } else {
                if (-not $item.QuietUninstallString) { $cmd = "$cmd /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" }
                $proc = Start-Process cmd.exe -ArgumentList "/c `"$cmd`"" -PassThru -WindowStyle Hidden
            }
            if (-not $proc.WaitForExit(300000)) { try { $proc.Kill() } catch { }; throw 'o desinstalador passou de 5 minutos e foi encerrado' }
        }
    }
}

# 1.3 Chocolatey
$chocoRaiz = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { Join-Path $env:ProgramData 'chocolatey' }
$choco = Join-Path $chocoRaiz 'bin\choco.exe'
if (Test-Path $choco) {
    $lista = & $choco list -r 2>$null
    if (-not $lista) { $lista = & $choco list --local-only -r 2>$null }
    foreach ($l in @($lista | Where-Object { $_ -match '^microsip\|' })) {
        Acao "choco uninstall microsip ($l)" { & $choco uninstall microsip -y --no-progress | Out-Null; if ($LASTEXITCODE -notin 0, 1605, 1614, 1641, 3010) { throw "choco saiu com codigo $LASTEXITCODE" } }
    }
}

# 1.4 pastas com o programa (instalado ou portatil) e arquivos soltos
$pastas = New-Object System.Collections.Generic.List[string]
foreach ($p in @("$env:ProgramFiles\MicroSIP", "${env:ProgramFiles(x86)}\MicroSIP", 'C:\MicroSIP')) { if ($p) { $pastas.Add($p) } }
foreach ($perfil in $perfis) {
    $pastas.Add((Join-Path $perfil.FullName 'AppData\Local\MicroSIP'))
    $pastas.Add((Join-Path $perfil.FullName 'AppData\Local\Programs\MicroSIP'))
    foreach ($sub in 'Desktop', 'Downloads', 'Documents') {
        $base = Join-Path $perfil.FullName $sub
        if (Test-Path $base -ErrorAction SilentlyContinue) { foreach ($d in @(Get-ChildItem $base -Directory -Filter 'MicroSIP*' -ErrorAction SilentlyContinue)) { $pastas.Add($d.FullName) } }
    }
}
if ($RaizTeste) { foreach ($e in @(Get-ChildItem $RaizTeste -Recurse -Filter 'microsip.exe' -ErrorAction SilentlyContinue)) { $pastas.Add($e.DirectoryName) } }
foreach ($pasta in @($pastas | Select-Object -Unique)) {
    if (-not (Test-Path $pasta -ErrorAction SilentlyContinue)) { continue }
    # So apaga se for mesmo o MicroSIP (tem o executavel) ou se a pasta se chama MicroSIP dentro de um local conhecido.
    $temExe = [bool](Get-ChildItem $pasta -Filter 'microsip*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $temExe -and $pasta -notmatch '(?i)\\MicroSIP$') { continue }
    $alvo = $pasta
    Acao "apagar a pasta $alvo" { Remove-Item -LiteralPath $alvo -Recurse -Force -ErrorAction Stop }
}

# 1.5 atalhos (menu Iniciar, inicializacao, area de trabalho)
$locaisAtalho = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup'),
    'C:\Users\Public\Desktop'
)
foreach ($perfil in $perfis) {
    $locaisAtalho += (Join-Path $perfil.FullName 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs')
    $locaisAtalho += (Join-Path $perfil.FullName 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup')
    $locaisAtalho += (Join-Path $perfil.FullName 'Desktop')
}
if ($RaizTeste) { $locaisAtalho += $RaizTeste }
foreach ($local in @($locaisAtalho | Select-Object -Unique)) {
    if (-not (Test-Path $local -ErrorAction SilentlyContinue)) { continue }
    foreach ($f in @(Get-ChildItem $local -Filter 'MicroSIP*.lnk' -ErrorAction SilentlyContinue)) {
        $arq = $f.FullName
        Acao "apagar o atalho $arq" { Remove-Item -LiteralPath $arq -Force -ErrorAction Stop }
    }
    foreach ($d in @(Get-ChildItem $local -Directory -Filter 'MicroSIP*' -ErrorAction SilentlyContinue)) {
        if ($d.FullName -in @($pastas)) { continue }
        $dir = $d.FullName
        Acao "apagar a pasta de atalhos $dir" { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop }
    }
}

# 1.6 inicializacao automatica e associacoes tel:/sip:/callto: (Run e Classes), maquina toda
function Limpar-Chaves($raizRegistro, $rotulo) {
    foreach ($runKey in "$raizRegistro\Software\Microsoft\Windows\CurrentVersion\Run", "$raizRegistro\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run") {
        $props = (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue)
        if (-not $props) { continue }
        foreach ($v in @($props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and ((Texto-Do-MicroSIP $_.Value) -or (Texto-Do-MicroSIP $_.Name)) })) {
            $k = $runKey; $n = $v.Name
            Acao "remover da inicializacao ($rotulo): $n" { Remove-ItemProperty -LiteralPath $k -Name $n -Force -ErrorAction Stop }
        }
    }
    foreach ($proto in 'tel', 'sip', 'sips', 'callto') {
        $cmdKey = "$raizRegistro\Software\Classes\$proto\shell\open\command"
        $valor = (Get-ItemProperty -LiteralPath $cmdKey -ErrorAction SilentlyContinue).'(default)'
        if (Texto-Do-MicroSIP $valor) {
            $chaveProto = "$raizRegistro\Software\Classes\$proto"
            Acao "remover a associacao $proto`: ($rotulo) que apontava para o MicroSIP" { Remove-Item -LiteralPath $chaveProto -Recurse -Force -ErrorAction Stop }
        }
    }
    foreach ($sw in "$raizRegistro\Software\MicroSIP", "$raizRegistro\Software\WOW6432Node\MicroSIP") {
        if (Test-Path -LiteralPath $sw) { $chaveSw = $sw; Acao "remover a chave $chaveSw" { Remove-Item -LiteralPath $chaveSw -Recurse -Force -ErrorAction Stop } }
    }
}
Limpar-Chaves 'HKLM:' 'maquina'

# 1.7 por usuario: contas carregadas (HKEY_USERS) e as demais, carregando o NTUSER.DAT em separado
foreach ($sid in @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^S-1-5-21-[0-9-]+$' })) {
    Limpar-Chaves "Registry::HKEY_USERS\$($sid.PSChildName)" "usuario $($sid.PSChildName)"
}
if ($Executar) {
    $carregados = @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    foreach ($perfil in $perfis) {
        $dat = Join-Path $perfil.FullName 'NTUSER.DAT'
        if (-not (Test-Path $dat -ErrorAction SilentlyContinue)) { continue }
        $nomeHive = 'LojaTroca_' + ($perfil.Name -replace '[^A-Za-z0-9]', '_')
        & reg.exe load "HKU\$nomeHive" $dat 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { continue }   # em uso (ja carregado acima) ou sem acesso
        try { Limpar-Chaves "Registry::HKEY_USERS\$nomeHive" "usuario $($perfil.Name)" }
        finally {
            [gc]::Collect(); [gc]::WaitForPendingFinalizers()
            & reg.exe unload "HKU\$nomeHive" 2>$null | Out-Null
        }
    }
}

# 1.8 tarefas agendadas e regras de firewall do MicroSIP
try {
    foreach ($t in @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -like 'MicroSIP*' })) {
        $tn = $t.TaskName; $tp = $t.TaskPath
        Acao "remover a tarefa agendada $tn" { Unregister-ScheduledTask -TaskName $tn -TaskPath $tp -Confirm:$false -ErrorAction Stop }
    }
} catch { }
try {
    foreach ($f in @(Get-NetFirewallApplicationFilter -ErrorAction Stop | Where-Object { Texto-Do-MicroSIP $_.Program })) {
        $regras = @($f | Get-NetFirewallRule -ErrorAction SilentlyContinue)
        foreach ($r in $regras) {
            $nr = $r.Name
            Acao "remover a regra de firewall '$($r.DisplayName)'" { Remove-NetFirewallRule -Name $nr -ErrorAction Stop }
        }
    }
} catch { }

# 1.9 dados do usuario (configuracao com a senha SIP em texto e historico): remocao completa, com copia do .ini antes
foreach ($perfil in $perfis) {
    $dados = Join-Path $perfil.FullName 'AppData\Roaming\MicroSIP'
    if (-not (Test-Path $dados -ErrorAction SilentlyContinue)) { continue }
    if ($ManterDados) { Escrever "dados mantidos de $($perfil.Name): $dados (-ManterDados)"; continue }
    $d = $dados; $nomePerfil = $perfil.Name
    if (-not $SemCopia) {
        Acao "guardar a copia do microsip.ini de $nomePerfil em Softphone-Troca\copia-microsip" {
            $destino = Join-Path $env:ProgramData ("Softphone-Troca\copia-microsip\" + $nomePerfil)
            New-Item -ItemType Directory -Path $destino -Force | Out-Null
            foreach ($ini in @(Get-ChildItem $d -Filter '*.ini' -ErrorAction SilentlyContinue)) { Copy-Item -LiteralPath $ini.FullName -Destination $destino -Force }
            # so administradores e SYSTEM leem (o .ini tem a senha SIP em texto)
            & icacls.exe (Join-Path $env:ProgramData 'Softphone-Troca') /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
        }
    }
    Acao "apagar os dados do MicroSIP de $nomePerfil ($d)" { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop }
}
# ---------------------------------------------------------------- conferencia do passo 1
$restou = New-Object System.Collections.Generic.List[string]
if ($Executar) {
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'microsip*' })) { $restou.Add("processo $($p.Id)") }
    foreach ($raiz in $raizesUninstall[0..1]) {
        foreach ($chave in @(Get-ChildItem -Path $raiz -ErrorAction SilentlyContinue)) {
            $item = Get-ItemProperty -LiteralPath $chave.PSPath -ErrorAction SilentlyContinue
            if ($item -and (Texto-Do-MicroSIP $item.DisplayName)) { $restou.Add("instalacao '$($item.DisplayName)'") }
        }
    }
    foreach ($pasta in @($pastas | Select-Object -Unique)) {
        if ((Test-Path $pasta -ErrorAction SilentlyContinue) -and (Get-ChildItem $pasta -Filter 'microsip*.exe' -ErrorAction SilentlyContinue)) { $restou.Add("pasta $pasta") }
    }
}

if ($script:achou -eq 0) { Escrever 'MicroSIP: nada encontrado (ja removido).' }
if ($restou.Count -gt 0) {
    Escrever ('O MicroSIP ainda esta presente: ' + ($restou -join '; ') + '. O Softphone NAO foi instalado (nao pode conviver com ele).')
    exit 2
}

# ---------------------------------------------------------------- 2. Softphone
if ($SoRemover) { Escrever 'PASSO 2 ignorado (-SoRemover).'; if ($script:erros -gt 0) { exit 1 } else { Escrever 'fim ok'; exit 0 } }
Escrever 'PASSO 2: instalar o Softphone'

$exe = Join-Path $env:ProgramFiles 'Softphone\Softphone.exe'
$versaoAtual = $null
if (Test-Path $exe) { try { $versaoAtual = [version](Get-Item $exe).VersionInfo.FileVersion } catch { } }
$minima = [version]$VersaoMinima

if ($versaoAtual -and $versaoAtual -ge $minima) {
    Escrever "Softphone $versaoAtual ja instalado (minimo $VersaoMinima): nada a fazer."
    if ($script:erros -gt 0) { exit 1 } else { Escrever 'fim ok'; exit 0 }
}
if (-not (Test-Path $choco)) { Escrever "Chocolatey nao encontrado em $choco. Softphone nao instalado."; exit 3 }

$desc = if ($versaoAtual) { "atualizar o Softphone $versaoAtual -> $VersaoMinima ou mais novo" } else { 'instalar o Softphone' }
if (-not $Executar) {
    Escrever "[simulacao] $desc (choco upgrade $Pacote -y --source=$Feed)"
    Escrever 'fim da simulacao: nada foi alterado. Rode com -Executar para aplicar.'
    exit 0
}
Escrever "$desc (choco upgrade $Pacote, fonte $Feed)"
$psi = New-Object System.Diagnostics.ProcessStartInfo($choco, "upgrade $Pacote -y --no-progress --source=`"$Feed`"")
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$saida = $proc.StandardOutput.ReadToEndAsync()
if (-not $proc.WaitForExit(1800000)) {
    try { $proc.Kill() } catch { }
    Escrever 'O Chocolatey passou de 30 minutos e foi encerrado.'; exit 4
}
$resumo = ($saida.Result -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 6) -join ' | '
Escrever "choco saiu com codigo $($proc.ExitCode): $resumo"

$versaoNova = $null
if (Test-Path $exe) { try { $versaoNova = [version](Get-Item $exe).VersionInfo.FileVersion } catch { } }
if ($versaoNova -and $versaoNova -ge $minima) { Escrever "Softphone $versaoNova instalado em $exe."; if ($script:erros -gt 0) { exit 1 } else { Escrever 'fim ok'; exit 0 } }
Escrever "O Softphone nao ficou instalado em $exe (versao encontrada: $versaoNova)."
exit 4
