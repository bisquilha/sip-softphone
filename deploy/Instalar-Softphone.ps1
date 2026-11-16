<#
.SYNOPSIS
  Abre o instalador Windows assinado do Softphone.
.DESCRIPTION
  OpenAI Codex, 05/11/2026. Usa o mesmo instalador da Loja, com cadastro em Aplicativos instalados.
  Distribua preferencialmente o proprio Softphone-Setup-<versao>.exe.
#>
param([string]$Instalador = '', [switch]$Silencioso)
$ErrorActionPreference = 'Stop'
if (-not $Instalador) {
    $pastas = @($PSScriptRoot, (Join-Path $PSScriptRoot '..\publicar\instalador'))
    $candidatos = foreach ($pasta in $pastas) {
        Get-ChildItem -LiteralPath $pasta -Filter 'Softphone-Setup-*.exe' -ErrorAction SilentlyContinue
    }
    $Instalador = ($candidatos | Sort-Object { [version]$_.VersionInfo.FileVersion } -Descending | Select-Object -First 1).FullName
}
if (-not $Instalador) { throw 'Instalador nao encontrado. Gere com deploy\Criar-Instalador.ps1 ou informe -Instalador.' }
$Instalador = (Resolve-Path -LiteralPath $Instalador).Path
$sig = Get-AuthenticodeSignature -FilePath $Instalador
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Issuer -notmatch 'CN=CORP-ROOT-CA') { throw 'Instalador sem assinatura corporativa valida.' }
$argumentos = @('/NORESTART')
if ($Silencioso) { $argumentos += @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/ALLUSERS') }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$inicio = @{ FilePath = $Instalador; ArgumentList = $argumentos; Wait = $true; PassThru = $true }
if (-not $admin) { $inicio.Verb = 'RunAs' }
$processo = Start-Process @inicio
if ($processo.ExitCode -ne 0) { throw "Instalacao nao concluida (codigo $($processo.ExitCode)). Consulte o log do instalador." }
