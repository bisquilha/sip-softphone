; Criado pelo OpenAI Codex em 05/11/2026. Ver docs/REGISTRO-CODEX.md.
; 10/11/2026 (Claude Code, pedido do Ana): produto renomeado para Softphone em tudo que o usuario ve: nome, pasta de
; instalacao (Program Files\Softphone), executavel (Softphone.exe), atalhos, tarefa agendada. A instalacao antiga (Softphone) e
; removida pelo proprio instalador antes de instalar; os dados do usuario (%APPDATA%\Softphone) o app move para %APPDATA%\Softphone.
#ifndef Publicado
  #error Publicado obrigatorio
#endif
#ifndef Saida
  #error Saida obrigatoria
#endif
#define Versao GetVersionNumbersString(Publicado + "\Softphone.exe")

[Setup]
AppId=Softphone
AppName=Softphone
AppVersion={#Versao}
AppPublisher=Minha Empresa Industrial
VersionInfoVersion={#Versao}
DefaultDirName={autopf}\Softphone
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#Saida}
OutputBaseFilename=Softphone-Setup-{#Versao}
SetupIconFile=..\src\Softphone.App\Assets\softphone.ico
UninstallDisplayIcon={app}\Softphone.exe
UninstallDisplayName=Softphone
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
SignTool=Minha Empresa
SignedUninstaller=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "{#Publicado}\Softphone.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Publicado}\softphone.padrao.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "manter-aberto.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; 1.2.6 (11/11/2026): abre o Softphone no logon de QUALQUER usuario da maquina (a tarefa agendada de grupo nao funcionava). Sai junto com o desinstalador.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Softphone"; ValueData: """{app}\Softphone.exe"" --minimizado --manter"; Flags: uninsdeletevalue

[Icons]
Name: "{commonprograms}\Softphone"; Filename: "{app}\Softphone.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\Softphone"; Filename: "{app}\Softphone.exe"; WorkingDir: "{app}"

[Code]
function AplicativoAberto: Boolean;
var Localizador, Servico, Processos: Variant;
begin
  { Nao encerra processos aqui: so confere. Vale para o nome novo (Softphone) e o antigo (Softphone). }
  Localizador := CreateOleObject('WbemScripting.SWbemLocator');
  Servico := Localizador.ConnectServer('', 'root\CIMV2');
  Processos := Servico.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Softphone.exe'' OR Name = ''Softphone.exe'' OR Name LIKE ''Softphone.antigo-%.exe'' OR Name LIKE ''Softphone.antigo-%.exe''');
  Result := Processos.Count > 0;
end;

{ Desde a 1.1.10 (pedido do Ana, 10/11/2026): o instalador fecha o app sozinho, em vez de pedir ao usuario.
  Uma ligacao em andamento e encerrada. Os nomes sao exatos para nao derrubar o proprio instalador. }
procedure FecharAplicativo;
var Codigo, I: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process | Where-Object { $_.Name -in ''Softphone'',''Softphone'' -or $_.Name -like ''Softphone.antigo-*'' -or $_.Name -like ''Softphone.antigo-*'' } | Stop-Process -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  for I := 1 to 10 do
  begin
    if not AplicativoAberto then Exit;
    Sleep(500);
  end;
end;

procedure TirarTarefas;
var Codigo: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Softphone (manter aberto)" /F', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Softphone (manter aberto)" /F', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
end;

procedure TarefaManterAberto(const Acao: String);
var Codigo: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\manter-aberto.ps1') + '" -Acao ' + Acao +
    ' -Exe "' + ExpandConstant('{app}\Softphone.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
end;

{ Instalacao antiga (Softphone, ate a 1.2.0): desinstala em silencio, tira atalhos e a pasta antiga. Os dados do usuario ficam. }
procedure RemoverSoftphoneAntigo;
var Chave, Comando: String; Codigo: Integer;
begin
  Chave := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Softphone_is1';
  Comando := '';
  if not RegQueryStringValue(HKLM64, Chave, 'UninstallString', Comando) then RegQueryStringValue(HKLM32, Chave, 'UninstallString', Comando);
  if Comando <> '' then
    Exec(RemoveQuotes(Comando), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  DeleteFile(ExpandConstant('{commonprograms}\Softphone.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\Softphone.lnk'));
  DelTree(ExpandConstant('{autopf}\Softphone'), True, True, True);
  RegDeleteKeyIncludingSubkeys(HKLM64, Chave);
  RegDeleteKeyIncludingSubkeys(HKLM32, Chave);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  try
    { tira as tarefas antes de fechar o app, senao ela abre de novo no meio da instalacao }
    TirarTarefas;
    if AplicativoAberto then FecharAplicativo;
    if AplicativoAberto then
      Result := 'Nao foi possivel fechar o Softphone. Feche-o pelo icone ao lado do relogio (Sair) e tente novamente.'
    else
      RemoverSoftphoneAntigo;
  except
    Result := 'Nao foi possivel conferir se o Softphone esta aberto. Reinicie o Windows e tente novamente.';
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    TirarTarefas;
    if AplicativoAberto then FecharAplicativo;
    Result := not AplicativoAberto;
    if not Result then
      SuppressibleMsgBox('Nao foi possivel fechar o Softphone. Feche-o pelo icone ao lado do relogio (Sair) antes de desinstalar.', mbError, MB_OK, IDOK);
  except
    SuppressibleMsgBox('Nao foi possivel conferir se o Softphone esta aberto. Reinicie o Windows e tente novamente.', mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Migra o cadastro do antigo pacote Chocolatey, evitando duas entradas. }
    RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Softphone');
    RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Softphone');
    { mantem o app sempre aberto e o reabre agora, na sessao de quem esta logado }
    TarefaManterAberto('Instalar');
  end;
end;