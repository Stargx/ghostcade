; Inno Setup script for Attractor.
; Per-user install (no admin) into %LocalAppData%\Programs\Attractor so the
; app's portable data\ folder beside the exe stays writable. Framework-dependent
; build; the .NET 10 Desktop Runtime is detected and offered for download if missing.

#define AppName "Attractor"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "Cold Beam Games"
#define AppExe "Attractor.exe"
#define AppUrl "https://github.com/Stargx/attractor"
; -DPublishDir=... is passed by CI; falls back to the local publish path.
#ifndef PublishDir
  #define PublishDir "..\src\Attractor.App\bin\Release\net10.0-windows\publish"
#endif

[Setup]
AppId={{8E4F2C7A-3B6D-4E1A-9C2F-AA17C0BE9D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
WizardStyle=modern
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Attractor.App\assets\img\attractor.ico
OutputDir=Output
OutputBaseFilename=Attractor-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';

function HasDotNet10Desktop(): Boolean;
var
  ResultCode: Integer;
  TmpFile, Cmd: string;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\runtimes.txt');
  // `dotnet --list-runtimes` via cmd so we can redirect to a file we can read
  Cmd := '/C dotnet --list-runtimes > "' + TmpFile + '" 2>&1';
  if Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TmpFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Pos('Microsoft.WindowsDesktop.App 10.', Lines[I]) > 0 then
        begin
          Result := True;
          Break;
        end;
  end;
end;

procedure InstallDotNet();
var
  ResultCode: Integer;
begin
  try
    DownloadTemporaryFile(DotNetUrl, 'windowsdesktop-runtime.exe', '', nil);
  except
    MsgBox('Could not download the .NET 10 Desktop Runtime automatically.' + #13#10 +
           'Please install it from https://dotnet.microsoft.com/download/dotnet/10.0 ' +
           'and run Attractor afterwards.', mbInformation, MB_OK);
    Exit;
  end;
  Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /quiet /norestart',
       '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not HasDotNet10Desktop() then
  begin
    if MsgBox('Attractor needs the .NET 10 Desktop Runtime, which was not found.' + #13#10 +
              'Download and install it now?', mbConfirmation, MB_YESNO) = IDYES then
      InstallDotNet();
  end;
end;
