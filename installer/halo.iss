; Halo installer — Inno Setup 6 (packaging plan P1/P3/P5, ticket 05 deliverable 2).
;
; Built by tools\build.ps1 -Installer, which passes the version and the staged payload
; folder in. Compiling by hand works too; it falls back to the defaults below.
;
;   ISCC.exe /DHaloVersion=1.0.0 /DHaloAppDir=..\dist\app /O..\dist installer\halo.iss
;
; Everything the installer does beyond copying files is delegated to Halo.Settings.exe's
; CLI verbs (--install-pawnio, --register-autostart, --unregister-autostart) so the
; scheduled-task definition and the PawnIO invocation exist in exactly one place:
; src\Halo.Settings\Services\AutostartManager.cs and CommandLineDispatcher.cs.

#ifndef HaloVersion
  #define HaloVersion "0.0.0"
#endif
#ifndef HaloAppDir
  #define HaloAppDir "..\dist\app"
#endif

#define HaloName "Halo"
#define HaloPublisher "finaea"

[Setup]
; Never change AppId — it is what makes "install over the top" an upgrade instead of a
; second copy in a second folder.
AppId={{D05EF3BB-1A5D-4FDA-9ABA-3C284E051B67}
AppName={#HaloName}
AppVersion={#HaloVersion}
AppVerName={#HaloName} {#HaloVersion}
AppPublisher={#HaloPublisher}
VersionInfoVersion={#HaloVersion}
DefaultDirName={autopf}\{#HaloName}
DefaultGroupName={#HaloName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputBaseFilename=Halo-Setup-{#HaloVersion}
UninstallDisplayName={#HaloName} {#HaloVersion}
UninstallDisplayIcon={app}\Halo.Widgets.exe
SetupIconFile=..\assets\halo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; The collector's scheduled task runs at RunLevel Highest and the files land in Program
; Files, so this is an admin install: x64 only, Windows 10 1809+ (10.0.17763). 1809 is
; Halo's floor, not .NET's — .NET 10 also lists Windows 10 1607/21H2 Enterprise
; (https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) — it is
; simply the oldest Windows 10 servicing branch still getting updates (LTSC 2019).
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Halo's own processes are stopped explicitly in CurStepChanged(ssInstall) below, scoped
; by image path, so Restart Manager never gets to offer to close an unrelated app.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Halo with the optional pieces"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "app"; Description: "Halo (widgets, collector, settings)"; Types: full custom; Flags: fixed
Name: "pawnio"; Description: "PawnIO driver for CPU temps, fans, drive temps"; Types: full
Name: "autostart"; Description: "Start with Windows"; Types: full

[Files]
; dist\app is the layout contract, shipped verbatim: three exes over one shared
; self-contained runtime, plus assets\, presentmon\, redist\ and the licence files.
; PawnIO_setup.exe is installed regardless of the component so System check can re-offer
; it later; the component only decides whether it is RUN now.
Source: "{#HaloAppDir}\*"; DestDir: "{app}"; Components: app; \
    Excludes: "portable.marker,data\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Halo Settings"; Filename: "{app}\Halo.Settings.exe"
Name: "{group}\Halo Widgets"; Filename: "{app}\Halo.Widgets.exe"

[Run]
; Both at the original (medium-integrity) user, which is what runasoriginaluser buys us —
; the token-inspection dance the old install-halo.ps1 did by hand. Widgets first, then
; Settings, which opens on System check the first time.
Filename: "{app}\Halo.Widgets.exe"; Flags: nowait runasoriginaluser skipifsilent
Filename: "{app}\Halo.Settings.exe"; Flags: nowait runasoriginaluser skipifsilent

[UninstallRun]
; Runs after CurUninstallStepChanged(usUninstall) below (which has already stopped the
; tasks and the processes) and before any file is deleted, so the exe still exists.
Filename: "{app}\Halo.Settings.exe"; Parameters: "--unregister-autostart"; \
    Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "HaloUnregisterAutostart"

[UninstallDelete]
; Anything Halo wrote under its own program folder after install (nothing does today, but
; a leftover would keep Program Files\Halo alive).
Type: filesandordirs; Name: "{app}"

[Code]
const
  SQ = #39;   { a single quote, so the PowerShell one-liners below stay readable }

{ A MsgBox still pops in /SILENT and /VERYSILENT and blocks forever with nobody to click
  it, so every message below goes through these: a dialog when someone is watching, a line
  in the /LOG file when nobody is. }
procedure SayInstall(const Text: String; Kind: TMsgBoxType);
begin
  if WizardSilent then Log('Halo: ' + Text) else MsgBox(Text, Kind, MB_OK);
end;

procedure SayUninstall(const Text: String; Kind: TMsgBoxType);
begin
  if UninstallSilent then Log('Halo: ' + Text) else MsgBox(Text, Kind, MB_OK);
end;

{ ---------------------------------------------------------------------------
  Stop everything of ours that is running out of the install folder.

  Scoped by image path so a Halo built from source somewhere else, or a separately
  installed Intel PresentMon, is never touched — the same rule tools\uninstall-halo.ps1
  used (uninstall-halo.ps1:78-98). Inno's Pascal has no process enumeration, so the CIM
  query runs in in-box PowerShell (5.1 ships with every Windows 10 1809+).

  Widgets is killed first on purpose: it owns the watchdog that restarts the collector
  task, so killing the collector while widgets is alive just resurrects it.
  --------------------------------------------------------------------------- }
procedure RunPowerShell(const Script: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PsQuote(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, SQ, SQ + SQ, True);
  Result := SQ + Result + SQ;
end;

procedure StopHaloProcesses(const AppDir: String);
var
  Root: String;
begin
  Root := PsQuote(RemoveBackslashUnlessRoot(AppDir));
  RunPowerShell(
    '$root=' + Root + '; ' +
    '$mine = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { ' +
      '($_.Name -like ' + SQ + 'Halo.*' + SQ + ' -or $_.Name -like ' + SQ + 'PresentMon*' + SQ + ') ' +
      '-and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, ' + SQ + 'OrdinalIgnoreCase' + SQ + ') }); ' +
    '$mine | Where-Object { $_.Name -like ' + SQ + 'Halo.Widgets*' + SQ + ' } | ' +
      'ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; ' +
    'Start-Sleep -Milliseconds 300; ' +
    '$mine | Where-Object { $_.Name -notlike ' + SQ + 'Halo.Widgets*' + SQ + ' } | ' +
      'ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }');
end;

procedure StopHaloTasks;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "\Halo\Widgets"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "\Halo\Collector"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ --------------------------------------------------------------------------- }

procedure InstallPawnIo;
var
  ResultCode: Integer;
begin
  if not WizardIsComponentSelected('pawnio') then
    exit;

  if not Exec(ExpandConstant('{app}\Halo.Settings.exe'), '--install-pawnio', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SayInstall('Could not start the PawnIO installer. CPU temperatures, fan speeds and drive'
      + ' temperatures will read N/A.' + #13#10#13#10
      + 'You can install it later from Halo Settings > System check.', mbError);
    exit;
  end;

  { 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: installed, but the driver does not load until a
    restart. Halo's NeedRestart event function cannot carry this — Inno queries it during
    ssInstall, before this code runs (Setup.Install.pas:2880) — so say it plainly instead
    of silently rebooting anyone. }
  if ResultCode = 3010 then
    SayInstall('PawnIO was installed and needs a restart before the driver loads.' + #13#10#13#10
      + 'Until you restart, CPU temperatures, fan speeds and drive temperatures will read'
      + ' N/A. Everything else works.', mbInformation)
  else if ResultCode <> 0 then
    SayInstall('The PawnIO installer returned ' + IntToStr(ResultCode) + '.' + #13#10#13#10
      + 'CPU temperatures, fan speeds and drive temperatures will read N/A. You can retry'
      + ' from Halo Settings > System check.', mbError);
end;

procedure RegisterAutostart;
var
  ResultCode: Integer;
begin
  if not WizardIsComponentSelected('autostart') then
    exit;

  { --user is deliberately omitted. Inno's "username" constant under UAC is whoever's
    credentials approved the elevation, which is not necessarily the person at the
    keyboard; the verb resolves the interactive console user itself
    (WTSGetActiveConsoleSessionId in AutostartManager.InteractiveUser). }
  if (not Exec(ExpandConstant('{app}\Halo.Settings.exe'), '--register-autostart', '',
               SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    SayInstall('Could not register Halo to start with Windows.' + #13#10#13#10
      + 'Open Halo Settings > System check and use "Repair autostart" to try again.',
      mbError);
    exit;
  end;

  { The collector task exists now; start it so this session has sensor data without a
    logoff. Widgets is launched by the [Run] entry as the original user instead. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN "\Halo\Collector"', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { Upgrade over the top: whatever is running out of the old folder holds its files
      open. Stop the tasks first so the widgets watchdog cannot restart the collector
      between the kill and the copy. }
    if DirExists(ExpandConstant('{app}')) then
    begin
      StopHaloTasks;
      StopHaloProcesses(ExpandConstant('{app}'));
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    InstallPawnIo;
    RegisterAutostart;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopHaloTasks;
    StopHaloProcesses(ExpandConstant('{app}'));
    { The [UninstallRun] entry removes the two scheduled tasks next, while
      Halo.Settings.exe still exists. }
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { Config and widget layouts. The uninstaller is elevated, so the "localappdata"
      constant resolves for whoever approved the elevation — the same person in the normal
      case of an admin uninstalling their own install, someone else if a standard user
      typed an admin's credentials. The prompt names the exact folder so a wrong guess is
      visible instead of silent. }
    DataDir := ExpandConstant('{localappdata}\Halo');
    if DirExists(DataDir) then
    begin
      { A silent uninstall keeps the data: deleting somebody's layouts because nobody was
        there to answer a dialog is the wrong default. }
      if UninstallSilent then
        Log('Halo: keeping ' + DataDir + ' (silent uninstall)')
      else if MsgBox('Delete Halo' + SQ + 's settings and widget layouts?' + #13#10#13#10 + DataDir
         + #13#10#13#10 + 'Choose No to keep them for a future install.',
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;

    { PawnIO is a shared kernel driver: FanControl, LibreHardwareMonitor and HWiNFO users
      depend on the same install. Removing it here would break them. }
    if DirExists(ExpandConstant('{commonpf64}\PawnIO')) or DirExists(ExpandConstant('{commonpf32}\PawnIO')) then
      SayUninstall('The PawnIO driver was left installed.' + #13#10#13#10
        + 'It is a shared driver — FanControl, LibreHardwareMonitor and HWiNFO use the same'
        + ' one. Remove it from Settings > Apps if nothing else needs it.',
        mbInformation);
  end;
end;
