Unicode true
RequestExecutionLevel user
CRCCheck force
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APP_DIR "$LOCALAPPDATA\Programs\AgentSleep"
!define MENU_DIR "$SMPROGRAMS\AgentSleep"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentSleep"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "AgentSleep v1.0"
OutFile "${SETUP_OUTPUT}"
InstallDir "${APP_DIR}"
VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "AgentSleep"
VIAddVersionKey "ProductVersion" "1.0.0"
VIAddVersionKey "FileVersion" "1.0.0"
VIAddVersionKey "FileDescription" "AgentSleep per-user setup for Windows x64"
VIAddVersionKey "LegalCopyright" "See the included LICENSE"

!define MUI_WELCOMEPAGE_TEXT "AgentSleep installs for your Windows account in AppData\Local\Programs\AgentSleep. You can open it from the Start menu after setup."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Var Stage

!macro RequireFree FILE FAILURE
  ${If} ${FileExists} "$INSTDIR\${FILE}"
    # Probe the installed path only. A loaded app or watchdog keeps that image busy.
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\${FILE}", i 0x40010000, i 0, p 0, i 3, i 0x80, p 0) p.r0'
    ${If} $0 == -1
      Goto ${FAILURE}
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
!macroend

!macro Publish FILE FAILURE
  System::Call 'kernel32::MoveFileExW(w "$Stage\${FILE}", w "$INSTDIR\${FILE}", i 9) i.r0'
  ${If} $0 == 0
    Goto ${FAILURE}
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "AgentSleep requires Windows x64." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  StrCpy $INSTDIR "${APP_DIR}"
FunctionEnd

Function CleanStage
  ${If} $Stage != ""
    SetOutPath "$TEMP"
    Delete "$Stage\AgentSleep.exe"
    Delete "$Stage\AgentSleep.DryRun.exe"
    Delete "$Stage\LICENSE"
    Delete "$Stage\Recovery.txt"
    Delete "$Stage\Desktop-Check.html"
    Delete "$Stage\Uninstall.exe"
    RMDir "$Stage"
  ${EndIf}
FunctionEnd

Section "AgentSleep"
  !insertmacro RequireFree "AgentSleep.exe" setup_busy
  !insertmacro RequireFree "AgentSleep.DryRun.exe" setup_busy

  ClearErrors
  CreateDirectory "$INSTDIR"
  IfErrors setup_failed
  GetTempFileName $Stage "$INSTDIR"
  IfErrors setup_failed
  Delete "$Stage"
  IfErrors setup_failed
  SetOutPath "$Stage"
  IfErrors setup_failed
  SetOverwrite try
  File "${REPO_ROOT}\builds\windows-x64\AgentSleep.exe"
  IfErrors setup_failed
  File "${REPO_ROOT}\builds\windows-x64\AgentSleep.DryRun.exe"
  IfErrors setup_failed
  File "${REPO_ROOT}\LICENSE"
  IfErrors setup_failed
  File "${REPO_ROOT}\installer\Recovery.txt"
  IfErrors setup_failed
  File /oname=Desktop-Check.html "${REPO_ROOT}\fixture\index.html"
  IfErrors setup_failed
  WriteUninstaller "$Stage\Uninstall.exe"
  IfErrors setup_failed
  SetOutPath "$INSTDIR"

  !insertmacro RequireFree "AgentSleep.exe" setup_busy
  !insertmacro RequireFree "AgentSleep.DryRun.exe" setup_busy
  !insertmacro Publish "AgentSleep.DryRun.exe" setup_failed
  !insertmacro Publish "LICENSE" setup_failed
  !insertmacro Publish "Recovery.txt" setup_failed
  !insertmacro Publish "Desktop-Check.html" setup_failed
  !insertmacro Publish "Uninstall.exe" setup_failed
  !insertmacro Publish "AgentSleep.exe" setup_failed

  ClearErrors
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "AgentSleep"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "1.0.0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\AgentSleep.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  CreateDirectory "${MENU_DIR}"
  CreateShortCut "${MENU_DIR}\AgentSleep.lnk" "$INSTDIR\AgentSleep.exe"
  CreateShortCut "${MENU_DIR}\Restore AgentSleep Displays.lnk" "$INSTDIR\AgentSleep.exe" "--restore"
  IfErrors setup_failed
  Call CleanStage
  Goto setup_end

setup_busy:
  Call CleanStage
  MessageBox MB_ICONEXCLAMATION|MB_OK "An installed AgentSleep file is in use. Use Restore displays if a session is active, then exit the installed tray and close its read-only check before retrying setup." /SD IDOK
  SetErrorLevel 2
  Abort
setup_failed:
  Call CleanStage
  MessageBox MB_ICONSTOP|MB_OK "AgentSleep setup could not complete. Close the installed app and try setup again." /SD IDOK
  SetErrorLevel 2
  Abort
setup_end:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ${If} $INSTDIR != "${APP_DIR}"
    MessageBox MB_ICONSTOP|MB_OK "Run the AgentSleep uninstaller from its installed location or Windows Settings." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro RequireFree "AgentSleep.exe" uninstall_busy
  !insertmacro RequireFree "AgentSleep.DryRun.exe" uninstall_busy
  SetOutPath "$TEMP"
  ClearErrors
  # Remove the image first. A process started after the probe still blocks deletion.
  Delete "$INSTDIR\AgentSleep.exe"
  IfErrors uninstall_busy
  Delete "$INSTDIR\AgentSleep.DryRun.exe"
  IfErrors uninstall_busy
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\Recovery.txt"
  Delete "$INSTDIR\Desktop-Check.html"
  Delete "${MENU_DIR}\AgentSleep.lnk"
  Delete "${MENU_DIR}\Restore AgentSleep Displays.lnk"
  RMDir "${MENU_DIR}"

  ClearErrors
  ReadRegStr $0 HKCU "${RUN_KEY}" "AgentSleep"
  StrCmpS $0 '$\"$INSTDIR\AgentSleep.exe$\"' 0 +2
    DeleteRegValue HKCU "${RUN_KEY}" "AgentSleep"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Goto uninstall_end

uninstall_busy:
  MessageBox MB_ICONEXCLAMATION|MB_OK "An installed AgentSleep file is in use. Restore your displays, exit the installed tray, and allow its watchdog to finish. Close the installed read-only check before retrying uninstall." /SD IDOK
  SetErrorLevel 2
  Abort
uninstall_end:
SectionEnd
