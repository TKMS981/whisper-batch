; Whisper Batch の Windows インストーラー（Inno Setup 6）
; ビルドは scripts/make-installer.ps1 から行う（アプリのビルドと Visual C++ ランタイムの取得を含む）。
;
;   ISCC.exe /DAppVersion=1.1.1 /DSourceDir=<build\WhisperBatch> /DRedistDir=<build\redist> /DOutputDir=<build\installer> WhisperBatch.iss

#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\WhisperBatch"
#endif
#ifndef RedistDir
  #define RedistDir "..\build\redist"
#endif
#ifndef OutputDir
  #define OutputDir "..\build\installer"
#endif

#define AppName "Whisper Batch"
#define AppExe "WhisperBatch.exe"

[Setup]
; AppId は変えない（変えると上書きインストール・アンインストールが別のアプリ扱いになる）
AppId={{6E0F4F2B-8C1D-4A57-9B3E-2D7A5C9E41B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=TKMS981
AppPublisherURL=https://github.com/TKMS981/whisper-batch
AppSupportURL=https://github.com/TKMS981/whisper-batch/issues
AppUpdatesURL=https://github.com/TKMS981/whisper-batch/releases
AppCopyright=Copyright (c) 2026 TKMS981 — MIT License
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 既定は自分だけにインストール（管理者権限不要）。すべてのユーザー向けも選べる
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=WhisperBatch-{#AppVersion}-win-x64-setup
SetupIconFile=..\App\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
; CUDA の DLL が大きい（展開後 約 1.2 GB）ので強めに圧縮する
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=8
DiskSpanning=no
; 起動中のアプリは閉じてから更新する
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
ShowLanguageDialog=auto

[Languages]
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ja.DesktopIcon=デスクトップにショートカットを作る
en.DesktopIcon=Create a desktop shortcut
ja.SendToIcon=エクスプローラーの「送る」メニューに追加する（選んだファイルをキューに追加）
en.SendToIcon=Add to the "Send to" menu in File Explorer (adds the selected files to the queue)
ja.OpenWith=エクスプローラーの「プログラムから開く」に追加する
en.OpenWith=Add to "Open with" in File Explorer
ja.Shortcuts=ショートカット:
en.Shortcuts=Shortcuts:
ja.Integration=エクスプローラー:
en.Integration=File Explorer:
ja.InstallingVC=Visual C++ ランタイムをインストールしています…
en.InstallingVC=Installing the Visual C++ Runtime…
ja.VCFailed=Visual C++ ランタイム（x64）をインストールできませんでした（コード %1）。Microsoft のサイトから「Visual C++ 再頒布可能パッケージ」をインストールしてから Whisper Batch を起動してください。
en.VCFailed=Could not install the Visual C++ Runtime (x64) (code %1). Please install the "Visual C++ Redistributable" from Microsoft before starting Whisper Batch.
ja.DeleteModels=ダウンロードしたモデル（Whisper・ReazonSpeech、%1）も削除しますか？%n%n保存先: %2%n%n残しておくと、再インストールしたときにダウンロードし直す必要がありません。
en.DeleteModels=Also delete the downloaded models (Whisper and ReazonSpeech, %1)?%n%nLocation: %2%n%nIf you keep them, you won't need to download them again after reinstalling.
ja.GpuNote=NVIDIA GPU（CUDA 12 対応ドライバー）があれば GPU で処理します。ない場合は CPU で処理します（時間がかかります）。
en.GpuNote=An NVIDIA GPU (with a CUDA 12 driver) is used when available; otherwise processing runs on the CPU (slower).

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked
Name: "sendto"; Description: "{cm:SendToIcon}"; GroupDescription: "{cm:Integration}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith}"; GroupDescription: "{cm:Integration}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RedistDir}\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsVCRedist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{usersendto}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: sendto

[Registry]
; 「プログラムから開く」の候補に出す（既定のアプリにはしない）
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
#define Ext(e) 'Root: HKA; Subkey: "Software\Classes\Applications\' + AppExe + '\SupportedTypes"; ValueType: string; ValueName: ".' + e + '"; ValueData: ""; Tasks: openwith'
#define ExtOpen(e) 'Root: HKA; Subkey: "Software\Classes\.' + e + '\OpenWithList\' + AppExe + '"; Flags: uninsdeletekey; Tasks: openwith'
#sub EmitExt
  #emit Ext(Exts[I])
  #emit ExtOpen(Exts[I])
#endsub
#dim Exts[30]
#define Exts[0] "wav"
#define Exts[1] "mp3"
#define Exts[2] "m4a"
#define Exts[3] "aac"
#define Exts[4] "wma"
#define Exts[5] "flac"
#define Exts[6] "ogg"
#define Exts[7] "opus"
#define Exts[8] "aif"
#define Exts[9] "aiff"
#define Exts[10] "caf"
#define Exts[11] "mp4"
#define Exts[12] "m4v"
#define Exts[13] "mov"
#define Exts[14] "3gp"
#define Exts[15] "mkv"
#define Exts[16] "webm"
#define Exts[17] "avi"
#define Exts[18] "wmv"
#define Exts[19] "mpg"
#define Exts[20] "mpeg"
#define Exts[21] "ts"
#define Exts[22] "m2ts"
#define Exts[23] "mts"
#define Exts[24] "m4b"
#define Exts[25] "oga"
#define Exts[26] "aifc"
#define Exts[27] "3g2"
#define Exts[28] "qt"
#define Exts[29] "asf"
#define I
#for {I = 0; I < 30; I++} EmitExt

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:InstallingVC}"; Flags: waituntilterminated; Check: NeedsVCRedist; AfterInstall: CheckVCResult
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 変換用の一時ファイル（アプリが削除するが、異常終了した場合に備えて）
Type: filesandordirs; Name: "{%TEMP}\WhisperBatch-Conversions"

[Code]
var
  VCNeeded: Integer;

{ whisper.cpp（ggml）は Visual C++ 2015-2022 ランタイム（x64、14.40 以降）の vcomp140.dll などを使う }
function NeedsVCRedist: Boolean;
var
  Installed, Major, Minor: Cardinal;
  Key: String;
begin
  if VCNeeded = 0 then
  begin
    Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
    if RegQueryDWordValue(HKLM64, Key, 'Installed', Installed) and (Installed = 1)
       and RegQueryDWordValue(HKLM64, Key, 'Major', Major)
       and RegQueryDWordValue(HKLM64, Key, 'Minor', Minor)
       and ((Major > 14) or ((Major = 14) and (Minor >= 40))) then
      VCNeeded := 2
    else
      VCNeeded := 1;
  end;
  Result := VCNeeded = 1;
end;

procedure CheckVCResult;
begin
  VCNeeded := 0;
  if NeedsVCRedist then
    SuppressibleMsgBox(FmtMessage(CustomMessage('VCFailed'), ['?']), mbError, MB_OK, IDOK);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + CustomMessage('GpuNote');
end;

{ フォルダの中のファイルの合計サイズ（ReazonSpeech はモデルごとのサブフォルダに置くので、サブフォルダも数える） }
function FolderSize(const Dir: String): Int64;
var
  FindRec: TFindRec;
begin
  Result := 0;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        Result := Result + (Int64(FindRec.SizeHigh) shl 32) + FindRec.SizeLow
      else if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        Result := Result + FolderSize(AddBackslash(Dir) + FindRec.Name);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ アンインストール時、ダウンロードしたモデルを消すか確認する。設定ファイルと、設定で選んだ背景画像の複製は消す（サイレント時はどれも残す）。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Models, Question: String;
  Size: Int64;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Models := ExpandConstant('{localappdata}\WhisperBatch\Models');
  if DirExists(Models) and not UninstallSilent then
  begin
    Size := FolderSize(Models);
    Question := FmtMessage(CustomMessage('DeleteModels'), [Format('%d MB', [Size div 1000000]), Models]);
    if (Size > 0) and (MsgBox(Question, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{localappdata}\WhisperBatch'), True, True, True);
  end;
  if not UninstallSilent then
  begin
    DelTree(ExpandConstant('{userappdata}\WhisperBatch'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\WhisperBatch\Background'), True, True, True);
  end;
end;
