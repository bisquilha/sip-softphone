<#
.SYNOPSIS
    Sobe um Hub de TESTE local (porta 5280, dados numa pasta temporária, IA falsa) e roda os testes do Hub contra ele.
.DESCRIPTION
    Hub:Teste=true libera o login sem senha (/api/entrar-teste) — só aqui, nunca em produção.
    A IA falsa (whisper + Anthropic de mentira) sobe dentro do próprio teste, em http://localhost:5299.
    Admin de teste: admin.teste. Reuniões com um Jitsi de mentira (só gera o passe).
.EXAMPLE
    .\deploy\Testar-Hub.ps1
#>
param([int]$Porta = 5280, [switch]$ManterHub)
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

# Hub antigo de teste ainda na porta: derruba
Get-NetTCPConnection -LocalPort $Porta -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

$dados = Join-Path $env:TEMP "softphone-hub-teste-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item -ItemType Directory -Force $dados | Out-Null
& $dotnet build "$raiz\src\Softphone.Hub\Softphone.Hub.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o Hub.' }
& $dotnet build "$raiz\src\Softphone.PabxTeste\Softphone.PabxTeste.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar os testes.' }

$vars = @{
    'Hub__Teste' = 'true'; 'Hub__Dados' = $dados; 'Hub__Admins__0' = 'admin.teste'
    'Hub__Ia__WhisperUrl' = 'http://localhost:5299'; 'Hub__Ia__AnthropicUrl' = 'http://localhost:5299'
    'Hub__Ia__AnthropicChave' = 'sk-ant-api03-chave-teste'
    'Hub__Jitsi__Url' = 'https://reuniao.teste.local'; 'Hub__Jitsi__Segredo' = 'segredo-de-teste-com-32-caracteres-ok'
    'ASPNETCORE_URLS' = "http://localhost:$Porta"; 'ASPNETCORE_ENVIRONMENT' = 'Development'
}
$antes = @{}
foreach ($k in $vars.Keys) { $antes[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
$log = Join-Path $dados 'hub.log'
$hubDll = Join-Path $raiz 'src\Softphone.Hub\bin\Release\net8.0\Softphone.Hub.dll'
$hub = Start-Process $dotnet -ArgumentList "`"$hubDll`"" -WorkingDirectory $dados -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
foreach ($k in $antes.Keys) { [Environment]::SetEnvironmentVariable($k, $antes[$k]) }
try {
    $ok = $false
    for ($i = 0; $i -lt 40 -and -not $ok; $i++) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://localhost:$Porta/saude" | Out-Null; $ok = $true } catch {} }
    if (-not $ok) { Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 30; throw 'O Hub de teste não subiu.' }
    & $dotnet "$raiz\src\Softphone.PabxTeste\bin\Release\net8.0\Softphone.PabxTeste.dll" --hub "http://localhost:$Porta"
    $resultado = $LASTEXITCODE
}
finally {
    if (-not $ManterHub) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
}
if ($resultado -ne 0) { Write-Host "Log do Hub: $log" -ForegroundColor Yellow; throw 'Testes do Hub falharam.' }

# Testes do painel web (JavaScript, sem navegador): precisam do Node no PC de desenvolvimento
$node = Get-Command node -ErrorAction SilentlyContinue
$pastaPainel = Join-Path $raiz 'testes\painel'
if (-not (Test-Path $pastaPainel)) {
    Write-Host 'Sem testes do painel web (testes\painel não existe): etapa pulada.' -ForegroundColor Yellow
} elseif ($node) {
    foreach ($t in Get-ChildItem (Join-Path $raiz 'testes\painel') -Filter '*.test.cjs') {
        & $node.Source $t.FullName
        if ($LASTEXITCODE -ne 0) { throw "Testes do painel falharam ($($t.Name))." }
    }
} else { Write-Host 'Node não encontrado: testes do painel web (testes\painel) não rodaram.' -ForegroundColor Yellow }
