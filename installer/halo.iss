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

; Inno Setup 6.4.0 is the floor, not plain "6". Two things this script now uses arrived in that
; release (Inno Setup whatsnew.htm, 6.4.0, "Pascal Scripting changes"): ExecAndCaptureOutput, so
; the verbs' stdout/stderr can be kept instead of thrown away, and the CopyFile spelling of the
; old FileCopy. Fail here, with a reason, rather than at "Unknown identifier" halfway down.
#if Ver < EncodeVer(6,4,0)
  #error This installer needs Inno Setup 6.4.0 or newer (ExecAndCaptureOutput, CopyFile).
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

; Logging is ON, and it was not. Both directives default to "no"
; (https://jrsoftware.org/ishelp/topic_setup_setuplogging.htm,
;  https://jrsoftware.org/ishelp/topic_setup_uninstalllogging.htm) and Log() is "ignored if
; logging is not enabled" (https://jrsoftware.org/ishelp/topic_isxfunc_log.htm) — so until this
; line existed, EVERY Log() call in this script wrote nothing at all. That includes the ones whose
; own comments promise "a line in the /LOG file when nobody is watching" for a silent install, and
; the PawnIO-already-present and autostart-unregister paths, which are precisely the outcomes
; nobody is watching. DeinitializeSetup below then copies the log out of %TEMP% and into Halo's own
; logs folder, because "Setup Log 2026-09-17 #001.txt" in %TEMP% is somewhere nobody looks.
;
; UninstallLogging "has no effect if CreateUninstallRegKey is not set to yes". That default IS yes
; (https://jrsoftware.org/ishelp/topic_setup_createuninstallregkey.htm) and this installer relies
; on the key for its ARP entry anyway — stated here so the dependency is visible rather than
; true by luck.
SetupLogging=yes
UninstallLogging=yes
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Halo with the optional pieces"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
; The PawnIO component only exists when no PawnIO is on the machine (any version). Its own
; setup refuses to install over an existing copy — see PawnIoInstalled below.
Name: "app"; Description: "Halo (widgets, collector, settings)"; Types: full custom; Flags: fixed
Name: "pawnio"; Description: "PawnIO driver for CPU temps, fans, drive temps"; Types: full; \
    Check: not PawnIoInstalled
Name: "autostart"; Description: "Start with Windows"; Types: full

[InstallDelete]
; Fonts that earlier builds shipped and the public release deliberately removed: SegMDL2.ttf is
; Microsoft proprietary and MaterialIcons.ttf was unreferenced (THIRD-PARTY-NOTICES.md).
;
; This is not tidiness. The payload below is copied with "ignoreversion", which overwrites but
; never DELETES, and there is nothing else in this script that removes a file during an install -
; so upgrading over a build that shipped these leaves both on disk for good. Dx.LoadFonts adds
; every *.ttf in the folder to the private font collection by glob (src\Halo.Widgets\Dx.cs:58),
; so a leftover font is still LOADED, not merely present: an upgraded install keeps a proprietary
; Microsoft font in Halo's own font collection. Verified 2026-09-14 by planting files a newer
; version does not ship and upgrading over the top - all of them survived.
;
; Deliberately two exact paths and "Type: files", not a recursive sweep of assets\: a wrong path
; in a recursive delete is worse than a stale file, and the exposure is specifically these fonts.
; [InstallDelete] runs BEFORE [Files] copies anything, which is what makes this safe - a font the
; current version still ships is deleted here and restored by the copy a moment later.
Type: files; Name: "{app}\assets\fonts\SegMDL2.ttf"
Type: files; Name: "{app}\assets\fonts\MaterialIcons.ttf"

[Files]
; dist\app is the layout contract, shipped verbatim: three exes over one shared
; self-contained runtime, plus assets\, presentmon\, redist\ and the licence files.
; PawnIO_setup.exe is installed regardless of the component so System check can re-offer
; it later; the component only decides whether it is RUN now.
Source: "{#HaloAppDir}\*"; DestDir: "{app}"; Components: app; \
    Excludes: "portable.marker,data\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
; The standard "Create a desktop shortcut" checkbox. Halo is started by the user from a
; shortcut, so this is the one most people will actually click.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
; "Halo" is the entry that starts the pair: Halo.Widgets.exe brings the overlay up and its
; --start-collector flag then asks for the elevation the collector needs (UAC prompt by
; design — src\Halo.Widgets\CollectorLauncher.cs). Halo.Collector.exe itself stays
; asInvoker so --dump, --migrate-config and the smoketests never prompt.
;
; The two single-process entries are deliberately kept as they were, for anyone who wants
; one half on its own.
Name: "{group}\Halo"; Filename: "{app}\Halo.Widgets.exe"; Parameters: "--start-collector"
Name: "{group}\Halo Settings"; Filename: "{app}\Halo.Settings.exe"
Name: "{group}\Halo Widgets"; Filename: "{app}\Halo.Widgets.exe"
Name: "{autodesktop}\Halo"; Filename: "{app}\Halo.Widgets.exe"; Parameters: "--start-collector"; \
    Tasks: desktopicon

[Run]
; Both at the original (medium-integrity) user, which is what runasoriginaluser buys us —
; the token-inspection dance the old install-halo.ps1 did by hand. Widgets first, then
; Settings, which opens on System check the first time.
;
; postinstall is load-bearing, not cosmetic. A [Run] entry WITHOUT it is processed before
; CurStepChanged(ssPostInstall) — measured 2026-09-13 in a /LOG: "Installation process
; succeeded." and "-- Run entry --" 13 ms apart, RegisterAutostart below not yet run — so
; the widgets would start before the collector task exists, give it 5 s
; (Halo.Widgets App.GenerateFirstRunLayout) and then write the minimal offline layout.
; With postinstall the two entries run when the user clicks Finish, after the collector
; has been registered and started.
;
; --start-collector for the same reason the shortcut has it: with "Start with Windows"
; unticked nothing has registered a collector task, so this is the only thing that gives
; the first session real sensor data. With it ticked, RegisterAutostart has already run
; schtasks /Run and the flag finds a live collector and stays quiet.
Filename: "{app}\Halo.Widgets.exe"; Parameters: "--start-collector"; \
    Description: "{cm:LaunchProgram,Halo}"; \
    Flags: nowait postinstall runasoriginaluser skipifsilent
Filename: "{app}\Halo.Settings.exe"; Description: "Open Halo Settings (System check)"; \
    Flags: nowait postinstall runasoriginaluser skipifsilent

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

var
  { What this install decided, accumulated as it runs and written out as install-state.json at the
    end of ssPostInstall. See WriteInstallState for why the file exists. }
  HaloAutostartResult: String;
  HaloPawnIoResult: String;
  HaloRebootPending: Boolean;
  HaloLogCopyName: String;

{ ---------------------------------------------------------------------------
  Where Halo's own data (and therefore its logs) live, from in here.

  Same caveat the uninstaller's data-folder prompt carries: this installer is elevated, so
  "localappdata" resolves for whoever APPROVED the elevation. In the normal case - an admin
  installing their own copy - that is the person who will run Halo. If a standard user typed an
  administrator's credentials, the install log lands in the administrator's profile instead.
  install-state.json records "installedBy" so a wrong guess is visible in the data rather than
  silent. There is no better constant available: Inno has no "the user at the keyboard" path, and
  Halo.Settings.exe resolves that itself (AutostartManager.InteractiveUser) precisely because of
  this. No brace constants in this comment - a closing brace would end it early.
  --------------------------------------------------------------------------- }
function HaloDataDir: String;
begin
  Result := ExpandConstant('{localappdata}\Halo');
end;

function HaloLogsDir: String;
begin
  Result := AddBackslash(HaloDataDir) + 'logs';
end;

{ One name per run, computed once, so install-state.json can name the same file DeinitializeSetup
  will write. }
function HaloLogCopyFileName: String;
begin
  if HaloLogCopyName = '' then
    HaloLogCopyName := 'install-{#HaloVersion}-' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + '.log';
  Result := HaloLogCopyName;
end;

function JsonEsc(const S: String): String;
begin
  { Backslash first, then quote: the other order would double the backslash this one just added. }
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

{ ---------------------------------------------------------------------------
  Run one of Halo.Settings.exe's verbs and KEEP what it said.

  Plain Exec threw the reasons away, and those reasons are the whole diagnosis. AutostartManager
  .Register writes the real Task Scheduler error to Console.Error; this script ran it with SW_HIDE
  and no capture and kept nothing but the exit code, so "Could not register Halo to start with
  Windows" was the entire story a user ever got.

  ExecAndCaptureOutput needs Inno 6.4.0+ (guarded at the top of this file), must always be
  ewWaitUntilTerminated, and hides console programs regardless of ShowCmd
  (https://jrsoftware.org/ishelp/topic_isxfunc_execandcaptureoutput.htm). Halo.Settings.exe skips
  its AttachConsole when output is already redirected, which is exactly this case, so the pipe
  stays intact and its Console.Error writes really do land here.

  This is defence in depth, not the only copy: the verbs also write the same reasons to their own
  log file under Halo's logs folder now. Two independent records of the same failure is the point.
  --------------------------------------------------------------------------- }
function ExecHaloSettings(const Params: String; var ResultCode: Integer): Boolean;
var
  Output: TExecOutput;
  I: Integer;
begin
  Log('Halo: running Halo.Settings.exe ' + Params);
  Result := ExecAndCaptureOutput(ExpandConstant('{app}\Halo.Settings.exe'), Params, '',
                                 SW_HIDE, ewWaitUntilTerminated, ResultCode, Output);
  if not Result then
  begin
    Log('Halo: could not START Halo.Settings.exe ' + Params);
    exit;
  end;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    if Trim(Output.StdOut[I]) <> '' then Log('Halo: [out] ' + Output.StdOut[I]);
  for I := 0 to GetArrayLength(Output.StdErr) - 1 do
    if Trim(Output.StdErr[I]) <> '' then Log('Halo: [err] ' + Output.StdErr[I]);
  if Output.Error then
    Log('Halo: output capture failed or was truncated; see Halo' + SQ + 's own settings-*.log');
  Log('Halo: Halo.Settings.exe ' + Params + ' exited ' + IntToStr(ResultCode));
end;

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

{ ---------------------------------------------------------------------------
  PawnIO is a shared kernel driver (FanControl, LibreHardwareMonitor and HWiNFO install the
  same one) and its own setup refuses to run over an existing copy: "A previous installation
  of PawnIO was found. Please uninstall it first before installing again.", exit 183 =
  ERROR_ALREADY_EXISTS (measured 2026-09-13 on a PC where FanControl had installed 2.2.0).
  So when any PawnIO is present, whatever its version, the component is not offered and the
  bundled setup is not run. System check in Halo Settings can still (re)install later.
  Detection mirrors what PawnIO_setup.exe itself checks: its ARP key, in both registry views,
  with the install folder as a fallback.
  --------------------------------------------------------------------------- }
const
  PawnIoUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  ERROR_ALREADY_EXISTS = 183;

function PawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIoUninstallKey)
         or RegKeyExists(HKLM32, PawnIoUninstallKey)
         or DirExists(ExpandConstant('{commonpf64}\PawnIO'))
         or DirExists(ExpandConstant('{commonpf32}\PawnIO'));
end;

function PawnIoInstalledVersion: String;
begin
  if not RegQueryStringValue(HKLM64, PawnIoUninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM32, PawnIoUninstallKey, 'DisplayVersion', Result) then
      Result := 'unknown version';
end;

procedure InstallPawnIo;
var
  ResultCode: Integer;
begin
  { The component is hidden by its Check when PawnIO is present, but /COMPONENTS on the
    command line or a PawnIO installed by something else between the wizard page and this
    point can still land here. Skipping is right at every version: the setup cannot upgrade
    in place anyway. A log line, never a dialog. }
  if PawnIoInstalled then
  begin
    Log('Halo: PawnIO ' + PawnIoInstalledVersion + ' is already installed; not running the bundled installer');
    HaloPawnIoResult := 'already-installed (' + PawnIoInstalledVersion + ')';
    exit;
  end;

  if not WizardIsComponentSelected('pawnio') then
  begin
    HaloPawnIoResult := 'not-selected';
    exit;
  end;

  if not ExecHaloSettings('--install-pawnio', ResultCode) then
  begin
    HaloPawnIoResult := 'could-not-start';
    SayInstall('Could not start the PawnIO installer. CPU temperatures, fan speeds and drive'
      + ' temperatures will read N/A.' + #13#10#13#10
      + 'You can install it later from Halo Settings > System check.', mbError);
    exit;
  end;
  HaloPawnIoResult := 'exit ' + IntToStr(ResultCode);

  { 183 = ERROR_ALREADY_EXISTS: the setup found a PawnIO the check above missed. Nothing was
    changed and the existing driver keeps working, so it is a log line, not an error. }
  if ResultCode = ERROR_ALREADY_EXISTS then
    Log('Halo: PawnIO installer reported an existing installation (183); leaving it as is')
  { 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: installed, but the driver does not load until a
    restart. Halo's NeedRestart event function cannot carry this — Inno queries it during
    ssInstall, before this code runs (Setup.Install.pas:2880) — so say it plainly instead
    of silently rebooting anyone. It is also recorded as rebootPending in install-state.json:
    that is the fact which explains "PawnIO is installed and the temperatures still read N/A"
    in the session straight after an install. }
  else if ResultCode = 3010 then
  begin
    HaloRebootPending := True;
    SayInstall('PawnIO was installed and needs a restart before the driver loads.' + #13#10#13#10
      + 'Until you restart, CPU temperatures, fan speeds and drive temperatures will read'
      + ' N/A. Everything else works.', mbInformation);
  end
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
  begin
    { Upgrading with "Start with Windows" unticked. The previous release registered
      Halo Collector and Halo Widgets tasks; they survive the file copy, still point into the
      install folder, and would start Halo at the next logon anyway - an explicit opt-out that
      does nothing. The verb only stops and deletes tasks whose action points inside the
      install folder, so a source build registered by install-dev.ps1, or a second install
      somewhere else, is left alone. On a fresh install there is nothing to remove and this
      is a no-op. Never a dialog: "nothing to unregister" and "unregistered" look the same
      from here, and the user did not ask for autostart either way.

      It is not silent any more, though. The verb now names every task it removed and, more
      importantly, every task it deliberately LEFT ALONE and why - that second list is the
      safety property this whole branch rests on, and ExecHaloSettings keeps it. }
    if not ExecHaloSettings('--unregister-autostart', ResultCode) then
      HaloAutostartResult := 'declined; unregister could not start'
    else
      HaloAutostartResult := 'declined; unregister exit ' + IntToStr(ResultCode);
    exit;
  end;

  { --user is deliberately omitted. Inno's "username" constant under UAC is whoever's
    credentials approved the elevation, which is not necessarily the person at the
    keyboard; the verb resolves the interactive console user itself
    (WTSGetActiveConsoleSessionId in AutostartManager.InteractiveUser) and now logs which
    account it settled on, and whether it had to fall back. }
  if not ExecHaloSettings('--register-autostart', ResultCode) then
    HaloAutostartResult := 'register could not start'
  else if ResultCode <> 0 then
    HaloAutostartResult := 'register FAILED, exit ' + IntToStr(ResultCode)
  else
    HaloAutostartResult := 'registered';

  if HaloAutostartResult <> 'registered' then
  begin
    { Do not quote the button's caption here: it is conditional now. With no tasks registered -
      which is exactly where a failure lands - it reads "Turn on autostart", and a dialog naming
      a caption the user cannot find is worse than one that just says where to go. }
    SayInstall('Could not register Halo to start with Windows.' + #13#10#13#10
      + 'Open Halo Settings > System check and use the autostart button there to try again.'
      + #13#10#13#10
      + 'What went wrong is written down, in:' + #13#10
      + '  ' + AddBackslash(HaloLogsDir) + HaloLogCopyFileName + #13#10
      + 'and in the settings-*.log files beside it.', mbError);
    exit;
  end;

  { The collector task exists now; start it so this session has sensor data without a
    logoff. Widgets and Settings are launched by the postinstall [Run] entries when the
    user clicks Finish — as the original user, and only after this has run. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN "\Halo\Collector"', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('Halo: schtasks /Run of the Collector task returned ' + IntToStr(ResultCode));
  HaloAutostartResult := HaloAutostartResult + ', schtasks /Run exit ' + IntToStr(ResultCode);
end;

function JsonBool(const Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

{ ---------------------------------------------------------------------------
  install-state.json - what this install decided.

  AutostartStatus asks for this file by name, in its own comment in
  src\Halo.Settings\Services\AutostartManager.cs: "nothing distinguishes that from a deliberate
  decline without recording installer intent". A --register-autostart that failed and a user who
  unticked "Start with Windows" both end with no scheduled tasks at all, System check renders both
  as "Autostart off", and afterwards the machine holds no evidence of which one happened. It holds
  this now.

  Written to the data folder's ROOT and deliberately not into config\: LiveConfigService watches
  that folder for *.json and reacts to what appears there, and this is a record rather than user
  configuration. Hand-rolled JSON because Inno's Pascal has no serializer and this is one flat
  object. No brace constants in this comment - a closing brace would end it early.
  --------------------------------------------------------------------------- }
procedure WriteInstallState;
var
  Dir, Path, Json, PawnVersion: String;
begin
  Dir := HaloDataDir;
  if not ForceDirectories(Dir) then
  begin
    Log('Halo: could not create ' + Dir + ', so install-state.json was not written');
    exit;
  end;
  if PawnIoInstalled then PawnVersion := PawnIoInstalledVersion else PawnVersion := '';
  Path := AddBackslash(Dir) + 'install-state.json';
  Json :=
    '{' + #13#10 +
    '  "version": "' + JsonEsc('{#HaloVersion}') + '",' + #13#10 +
    '  "installedAtLocal": "' + JsonEsc(GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':')) + '",' + #13#10 +
    '  "installDir": "' + JsonEsc(ExpandConstant('{app}')) + '",' + #13#10 +
    '  "installedBy": "' + JsonEsc(ExpandConstant('{username}')) + '",' + #13#10 +
    '  "silent": ' + JsonBool(WizardSilent) + ',' + #13#10 +
    '  "components": "' + JsonEsc(WizardSelectedComponents(False)) + '",' + #13#10 +
    '  "tasks": "' + JsonEsc(WizardSelectedTasks(False)) + '",' + #13#10 +
    '  "autostart": "' + JsonEsc(HaloAutostartResult) + '",' + #13#10 +
    '  "pawnIo": "' + JsonEsc(HaloPawnIoResult) + '",' + #13#10 +
    '  "pawnIoVersion": "' + JsonEsc(PawnVersion) + '",' + #13#10 +
    '  "rebootPending": ' + JsonBool(HaloRebootPending) + ',' + #13#10 +
    '  "setupLog": "' + JsonEsc(ExpandConstant('{log}')) + '",' + #13#10 +
    '  "setupLogCopy": "' + JsonEsc(AddBackslash(HaloLogsDir) + HaloLogCopyFileName) + '"' + #13#10 +
    '}' + #13#10;
  if SaveStringToFile(Path, Json, False) then
    Log('Halo: wrote ' + Path + ' -> autostart: ' + HaloAutostartResult + ' | pawnIO: ' + HaloPawnIoResult)
  else
    Log('Halo: could not write ' + Path);
end;

{ ---------------------------------------------------------------------------
  Put Inno's own log where somebody will find it.

  With SetupLogging on, the log lands in the TEMP folder as "Setup Log 2026-09-17 #001.txt" -
  technically present, practically invisible. This copies it in beside Halo's other logs as
  install-<version>-<timestamp>.log.

  Two things here were checked rather than assumed.

  (1) The log file is still OPEN at this point and stays open until Setup exits, so the copy is
  missing the last handful of lines Inno writes after this - including the one written below. It is
  not missing anything earlier: Inno writes each record straight through WriteFile with no buffer of
  its own (Shared.FileClass.pas, TFile.WriteBuffer calls WriteFile per Write), so everything logged
  up to now is already on disk.

  (2) The copy is possible at all because Setup holds the file with fsRead sharing
  (Setup.LoggingFunc.pas, StartLogging: TTextFileWriter.Create(Filename, fdCreateNew, faWrite,
  fsRead)) and Win32 CopyFile tolerates a writer holding it that way - measured 2026-09-17 against
  a file held open faWrite+fsRead, where CopyFileW returned success and copied the flushed content.

  The fallback is not decoration: if the copy cannot be made, leave a stub that NAMES Inno's log
  path, so Halo's logs folder still points at the real thing instead of staying silent.
  --------------------------------------------------------------------------- }
procedure CopySetupLogIntoHaloLogs;
var
  Source, Dir, Dest: String;
begin
  Source := ExpandConstant('{log}');
  { Empty when logging is disabled. SetupLogging=yes makes that a can't-happen, except that /LOG-
    on the command line still turns it off - so this is a real check, not a formality. }
  if Source = '' then
  begin
    Log('Halo: no setup log to copy - logging is disabled');
    exit;
  end;
  Dir := HaloLogsDir;
  if not ForceDirectories(Dir) then
  begin
    Log('Halo: could not create ' + Dir + ', so the setup log stays at ' + Source);
    exit;
  end;
  Dest := AddBackslash(Dir) + HaloLogCopyFileName;
  if CopyFile(Source, Dest, False) then
    Log('Halo: copied this setup log to ' + Dest)
  else if SaveStringToFile(Dest,
      'Halo {#HaloVersion} install' + #13#10#13#10
      + 'Setup could not copy its own log into this folder while it was still open.' + #13#10
      + 'The full setup log is at:' + #13#10
      + '  ' + Source + #13#10#13#10
      + 'Halo' + SQ + 's own record of this install is install-state.json, one folder up,' + #13#10
      + 'and the settings-*.log files beside this one hold what the setup verbs said.' + #13#10, False) then
    Log('Halo: could not copy the setup log; wrote a pointer to it at ' + Dest)
  else
    Log('Halo: could not copy the setup log or write a pointer to it; it stays at ' + Source);
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
    { Last, so it records what the two above actually did rather than what was about to be
      attempted. }
    WriteInstallState;
  end;
end;

procedure DeinitializeSetup;
begin
  { Runs on every exit, a cancelled or failed install included - which is the case where the log is
    worth the most. }
  CopySetupLogIntoHaloLogs;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Processes are stopped by image path. The scheduled tasks are NOT ended here by name:
      a task called \Halo\Collector may belong to a different Halo (a source build registered
      with install-dev.ps1, or an older install) — ending it killed exactly that during the
      2026-09-13 test. The [UninstallRun] entry's --unregister-autostart stops and deletes
      only the tasks whose action points into the install folder, while Halo.Settings.exe
      still exists. That entry is a [UninstallRun] line rather than script, so its output
      cannot be captured the way ExecHaloSettings captures the install-side verbs - but the
      verb writes its own settings-*.log now, and with UninstallLogging on its exit code
      lands in the uninstall log. No brace constants in this comment: a closing brace would
      end it early. }
    StopHaloProcesses(ExpandConstant('{app}'));
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

procedure DeinitializeUninstall;
var
  Source, Dir, Dest: String;
begin
  { The uninstall log, on the same principle as the install one - but only when Halo's data folder
    survived. The prompt above may have just deleted it at the user's request, and recreating the
    folder to drop a log into it would undo precisely what they asked for. }
  Source := ExpandConstant('{log}');
  if (Source = '') or (not DirExists(HaloDataDir)) then exit;
  Dir := HaloLogsDir;
  if not ForceDirectories(Dir) then exit;
  Dest := AddBackslash(Dir) + 'uninstall-{#HaloVersion}-'
    + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + '.log';
  if CopyFile(Source, Dest, False) then
    Log('Halo: copied this uninstall log to ' + Dest)
  else
    Log('Halo: could not copy the uninstall log; it stays at ' + Source);
end;
