<# Instalador Inno Setup assinado. OpenAI Codex, 05/11/2026. #>
param(
    [string]$Publicado = (Join-Path $PSScriptRoot '..\publicar'),
    [string]$Saida = '',
    [string]$Compilador = ''
)
$ErrorActionPreference = 'Stop'
$Publicado = (Resolve-Path -LiteralPath $Publicado).Path
if (-not $Saida) { $Saida = Join-Path $Publicado 'instalador' }
$Saida = [IO.Path]::GetFullPath($Saida)
$exe = Join-Path $Publicado 'Softphone.exe'
foreach ($arquivo in @($exe, (Join-Path $Publicado 'softphone.padrao.json'))) {
    if (-not (Test-Path -LiteralPath $arquivo -PathType Leaf)) { throw "Falta arquivo: $arquivo" }
}
$sig = Get-AuthenticodeSignature -FilePath $exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Issuer -notmatch 'CN=CORP-ROOT-CA') {
    throw 'Publique e assine o Softphone.exe com o certificado corporativo antes de gerar o instalador.'
}
if (-not $Compilador) {
    $candidatos = @(
        (Join-Path $PSScriptRoot '..\.tools\InnoSetup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $Compilador = $candidatos | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Compilador) { $Compilador = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
}
if (-not $Compilador) { throw 'Instale o Inno Setup 6 ou informe -Compilador com o caminho de ISCC.exe.' }
$assinador = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\_comum\Assinar-Minha Empresa.ps1')).Path
# ISCC substitui $f pelo arquivo entre aspas; assina tanto o Setup quanto o desinstalador.
$powerShellAtual = (Get-Process -Id $PID).Path
$comandoAssinar = '$q' + $powerShellAtual + '$q -NoProfile -ExecutionPolicy Bypass -File $q' + $assinador + '$q -Arquivos $f'
New-Item -ItemType Directory -Path $Saida -Force | Out-Null
& $Compilador ('/DPublicado=' + $Publicado) ('/DSaida=' + $Saida) ('/SHomelab=' + $comandoAssinar) (Join-Path $PSScriptRoot 'Softphone.iss')
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar instalador: $LASTEXITCODE" }
$versao = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
$setup = Join-Path $Saida "Softphone-Setup-$versao.exe"
$sigSetup = Get-AuthenticodeSignature -FilePath $setup
if ($sigSetup.Status -ne 'Valid' -or $sigSetup.SignerCertificate.Thumbprint -ne $sig.SignerCertificate.Thumbprint) {
    throw 'Assinatura do instalador diferente da assinatura corporativa esperada.'
}
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
