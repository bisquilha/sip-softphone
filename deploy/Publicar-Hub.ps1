<#
.SYNOPSIS
    Gera o pacote do Hub (servidor do chat) para instalar no softphone-server.
.DESCRIPTION
    Saída: Downloads\Softphone\servidor\softphone-hub.tgz (app publicado + Dockerfile + compose + nginx + krb5 + instalador).
    No PC:  scp "$env:USERPROFILE\Downloads\Softphone\servidor\softphone-hub.tgz" root@192.0.2.5:/opt/softphone/
    Na VM:  cd /opt/softphone && tar xzf softphone-hub.tgz && bash hub/instalar-hub.sh   (reuniões: bash jitsi/instalar-jitsi.sh)
#>
param([string]$Saida = (Join-Path $env:USERPROFILE 'Downloads\Softphone\servidor'))
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$tmp = Join-Path $env:TEMP "softphone-hub-pacote"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
$hub = Join-Path $tmp 'hub'
New-Item -ItemType Directory -Force $hub | Out-Null

Write-Host 'Publicando o Hub (Linux, roda dentro do contêiner)...' -ForegroundColor Cyan
& $dotnet publish "$raiz\src\Softphone.Hub\Softphone.Hub.csproj" -c Release -o (Join-Path $hub 'app') --no-self-contained -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o Hub.' }
Remove-Item (Join-Path $hub 'app\*.pdb') -ErrorAction SilentlyContinue

foreach ($f in 'Dockerfile', 'docker-compose.yml', 'nginx.conf', 'krb5.conf', 'hub.env.exemplo', 'instalar-hub.sh', 'atualizar-hub.sh') {
    # Linux: fim de linha LF
    $texto = [IO.File]::ReadAllText((Join-Path $raiz "servidor\hub\$f")) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $hub $f), $texto, (New-Object Text.UTF8Encoding $false))
}

# Versão do pacote: o atualizar-hub.sh confere que o Hub que subiu é esta (R13)
$versao = [regex]::Match([IO.File]::ReadAllText("$raiz\src\Softphone.Hub\Program.cs"), 'const string Versao = "([^"]+)"').Groups[1].Value
if (-not $versao) { throw 'Não achei a versão do Hub em Program.cs.' }
[IO.File]::WriteAllText((Join-Path $hub 'VERSAO'), "$versao`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Hub $versao" -ForegroundColor Cyan

# Jitsi (reuniões): instalador próprio, na pasta jitsi do pacote
$jit = Join-Path $tmp 'jitsi'
New-Item -ItemType Directory -Force $jit | Out-Null
foreach ($f in Get-ChildItem (Join-Path $raiz 'servidor\jitsi') -File) {
    $texto = [IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $jit $f.Name), $texto, (New-Object Text.UTF8Encoding $false))
}

# Base de conhecimento da IA: só o modelo (a TI escreve o conteúdo real na VM)
$con = Join-Path $tmp 'conhecimento'
New-Item -ItemType Directory -Force $con | Out-Null
$texto = [IO.File]::ReadAllText((Join-Path $raiz 'servidor\conhecimento\MODELO.md.exemplo')) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $con 'MODELO.md.exemplo'), $texto, (New-Object Text.UTF8Encoding $false))

New-Item -ItemType Directory -Force $Saida | Out-Null
$tgz = Join-Path $Saida 'softphone-hub.tgz'
if (Test-Path $tgz) { Remove-Item $tgz -Force }
tar -czf $tgz -C $tmp hub jitsi conhecimento
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compactar.' }
Remove-Item $tmp -Recurse -Force

# Script do AD (roda no DC01): vai assinado, ao lado do pacote
$ad = Join-Path $Saida 'Gravar-Ramais-AD.ps1'
Copy-Item (Join-Path $raiz 'deploy\Gravar-Ramais-AD.ps1') $ad -Force
& (Join-Path $raiz '..\_comum\Assinar-Minha Empresa.ps1') -Arquivos $ad | Out-Null

Get-Item $tgz, $ad | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
