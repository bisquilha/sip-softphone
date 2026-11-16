<#
.SYNOPSIS
    Gera o pacote do Softphone (rodar no PC de desenvolvimento).
.DESCRIPTION
    publicar\Softphone.exe          arquivo único, não precisa de .NET instalado nas máquinas
    publicar\softphone.padrao.json  padrões da TI (servidor do PABX); fica ao lado do .exe
    publicar\Softphone-<versão>.zip os dois acima, para distribuir
    publicar\instalador\Softphone-Setup-<versão>.exe instalador Windows assinado
    publicar\loja\Homelab-softphone.<versão>.nupkg pacote Chocolatey com o Setup
    Antes de publicar, roda os testes do motor contra o PABX simulado (pule com -SemTestes).
    Depois assina com o certificado da CORP-ROOT-CA via ..\_comum\Assinar-Minha Empresa.ps1 (pule com -SemAssinar).
#>
param(
    [switch]$SemTestes,
    [switch]$SemAssinar,
    [switch]$SemLoja,
    [string]$Saida = (Join-Path $PSScriptRoot '..\publicar')
)
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

if (-not $SemTestes) {
    Write-Host 'Testes do motor (PABX simulado)...' -ForegroundColor Cyan
    & $dotnet run --project "$raiz\src\Softphone.PabxTeste\Softphone.PabxTeste.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam: não publicado.' }
}

$Saida = [IO.Path]::GetFullPath($Saida)
$raizAbsoluta = [IO.Path]::GetFullPath($raiz.Path).TrimEnd('\') + '\'
if (-not $Saida.StartsWith($raizAbsoluta, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A pasta de saida deve estar dentro do projeto, em uma subpasta dedicada aos arquivos publicados.'
}
if (Test-Path $Saida) {
    $resolvido = (Resolve-Path -LiteralPath $Saida).Path
    if ($resolvido -ne $Saida -or (Get-Item -LiteralPath $Saida).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Pasta de saida redirecionada: limpeza cancelada.' }
    Remove-Item -LiteralPath $resolvido -Recurse -Force
}
Write-Host 'Publicando o app...' -ForegroundColor Cyan
& $dotnet publish "$raiz\src\Softphone.App\Softphone.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $Saida -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o app.' }

# Assinatura com o certificado da CORP-ROOT-CA (script comum a todos os apps internos). Todo PC do domínio
# confia nessa CA, e a política do KES "confia em aplicativos assinados": o Softphone entra como confiável
# (sem isso o Kaspersky bloqueia o microfone).
if (-not $SemAssinar) {
    & (Join-Path $raiz '..\_comum\Assinar-Minha Empresa.ps1') -Arquivos (Join-Path $Saida 'Softphone.exe') | Out-Host
}

[xml]$proj = Get-Content "$raiz\src\Softphone.App\Softphone.App.csproj"
$versao = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$zip = Join-Path $Saida "Softphone-$versao.zip"
Compress-Archive -Path (Join-Path $Saida 'Softphone.exe'), (Join-Path $Saida 'softphone.padrao.json') -DestinationPath $zip -Force
Get-ChildItem $Saida | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

# Instalador Windows e pacote da Loja. OpenAI Codex, 05/11/2026.
if (-not $SemAssinar) {
    & (Join-Path $PSScriptRoot 'Criar-Instalador.ps1') -Publicado $Saida | Out-Host
    if (-not $SemLoja) { & (Join-Path $PSScriptRoot 'Pacote-Loja-Softphone.ps1') -Publicado $Saida | Out-Host }
}
