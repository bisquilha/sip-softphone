<#
.SYNOPSIS
  Verifica o ciclo do instalador numa variante isolada (HKCU e pasta de teste).
.DESCRIPTION
  OpenAI Codex, 05/11/2026. Nao instala nem remove o Softphone real.
  A variante altera nome, id, privilegios, atalhos e consulta de processos para o nome de teste.
  Nao substitui validacao como SYSTEM em outro PC do dominio.
#>
param([string]$Publicado = (Join-Path $PSScriptRoot '..\distribuicao\1.1.4'))
$ErrorActionPreference = 'Stop'
$Publicado = (Resolve-Path -LiteralPath $Publicado).Path
$teste = Join-Path $Publicado ('teste-instalador-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $teste | Out-Null
$fonte = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Softphone.iss') -Raw
$icon = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\src\Softphone.App\Assets\softphone.ico')).Path
$fonte = $fonte.Replace('AppId=Softphone', 'AppId=SoftphoneCodexTeste').Replace('AppName=Softphone', 'AppName=SoftphoneCodexTeste')
$fonte = $fonte.Replace('PrivilegesRequired=admin', 'PrivilegesRequired=lowest').Replace('DefaultDirName={autopf}\Softphone', ('DefaultDirName=' + (Join-Path $teste 'app')))
$fonte = $fonte.Replace('SetupIconFile=..\src\Softphone.App\Assets\softphone.ico', ('SetupIconFile=' + $icon))
$fonte = $fonte.Replace('Name: "{commonprograms}\Softphone"', 'Name: "{userprograms}\SoftphoneCodexTeste"').Replace('Name: "{commondesktop}\Softphone"', 'Name: "{userdesktop}\SoftphoneCodexTeste"')
$fonte = $fonte.Replace('OutputBaseFilename=Softphone-Setup-{#Versao}', 'OutputBaseFilename=NAO-DISTRIBUIR-Teste')
# Nunca altera o cadastro real durante este teste.
$fonte = $fonte -replace '(?m)^    RegDeleteKeyIncludingSubkeys\(.*$', '    Log(''Teste isolado: cadastro legado preservado.'');'
$iscc = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\.tools\InnoSetup\ISCC.exe')).Path
$assinador = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\_comum\Assinar-Minha Empresa.ps1')).Path
$assinatura = '$q' + (Get-Process -Id $PID).Path + '$q -NoProfile -ExecutionPolicy Bypass -File $q' + $assinador + '$q -Arquivos $f'
function Compilar([string]$texto) {
    $iss = Join-Path $teste 'teste.iss'
    Set-Content -LiteralPath $iss -Value $texto -Encoding UTF8
    & $iscc '/Q' ('/DPublicado=' + $Publicado) ('/DSaida=' + $teste) ('/SHomelab=' + $assinatura) $iss
    if ($LASTEXITCODE -ne 0) { throw 'Compilacao da variante de teste falhou.' }
}
function Rodar([string]$arquivo, [string[]]$argumentos) {
    $p = Start-Process -FilePath $arquivo -ArgumentList $argumentos -WindowStyle Hidden -Wait -PassThru
    return $p.ExitCode
}
$exeTeste = Join-Path $teste 'NAO-DISTRIBUIR-Teste.exe'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoftphoneCodexTeste_is1'
$app = Join-Path $teste 'app'
if (Test-Path $key) { throw 'Existe instalacao de teste anterior: remova-a antes.' }
# Se o app real estiver aberto, verifica primeiro que a protecao recusa a instalacao.
if (Get-Process Softphone -ErrorAction SilentlyContinue) {
    Compilar $fonte
    $codigo = Rodar $exeTeste @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + (Join-Path $teste 'bloqueio.log') + '"'))
    if ($codigo -ne 7 -or (Test-Path $key)) { throw "Bloqueio de aplicativo aberto falhou: $codigo" }
    Write-Host '[OK] Instalador recusa atualizar com o aplicativo aberto (codigo 7).'
}
$fonte = $fonte.Replace("Name = ''Softphone.exe'' OR Name LIKE ''Softphone.antigo-%.exe''", "Name = ''SoftphoneCodexTeste.exe''")
Compilar $fonte
$instalado = $false
try {
    foreach ($etapa in @('instalar', 'reinstalar')) {
        $codigo = Rodar $exeTeste @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + (Join-Path $teste "$etapa.log") + '"'))
        if ($codigo -ne 0) { throw "$etapa falhou: $codigo" }
        $instalado = $true
        $reg = Get-ItemProperty -LiteralPath $key
        if ($reg.DisplayVersion -ne (Get-Item (Join-Path $Publicado 'Softphone.exe')).VersionInfo.FileVersion) { throw 'Versao incorreta no cadastro.' }
        if (-not (Test-Path (Join-Path $app 'Softphone.exe')) -or -not $reg.UninstallString) { throw 'Faltam aplicativo ou desinstalador.' }
        $origem = (Get-FileHash (Join-Path $Publicado 'Softphone.exe')).Hash
        if ((Get-FileHash (Join-Path $app 'Softphone.exe')).Hash -ne $origem) { throw 'Executavel instalado diferente da origem.' }
        if ((Get-AuthenticodeSignature (Join-Path $app 'unins000.exe')).Status -ne 'Valid') { throw 'Desinstalador sem assinatura valida.' }
        $atalho = Join-Path ([Environment]::GetFolderPath('Programs')) 'SoftphoneCodexTeste.lnk'
        if (-not (Test-Path -LiteralPath $atalho)) { throw 'Atalho nao criado.' }
        Write-Host "[OK] $etapa`: arquivos, versao na lista de programas, desinstalador assinado e atalho."
    }
    $codigo = Rodar (Join-Path $app 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $teste 'desinstalar.log') + '"'))
    if ($codigo -ne 0) { throw "Desinstalacao falhou: $codigo" }
    $instalado = $false
    Start-Sleep -Seconds 2
    if ((Test-Path $key) -or (Test-Path $atalho) -or (Test-Path (Join-Path $app 'Softphone.exe'))) { throw 'Desinstalacao deixou cadastro, aplicativo ou atalho.' }
    Write-Host '[OK] Desinstalacao remove aplicativo, cadastro e atalho.'
    Write-Host "TESTES DO INSTALADOR PASSARAM. Logs: $teste"
} finally {
    if ($instalado -and (Test-Path (Join-Path $app 'unins000.exe'))) {
        $null = Rodar (Join-Path $app 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    }
}
