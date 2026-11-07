; Blazma Animation — per-user Windows installer (no administrator rights needed).
; Built by scripts/build-release.sh:
;   makensis -DVERSION=1.0.0-rc1 -DNUMVER=1.0.0 -DSRCDIR=<staged app folder> -DOUTFILE=<setup.exe> -DREPO=<repo root> installer.nsi
;   [-DUNINSTLIST=<generated .nsh>]
; SRCDIR is the same folder as the portable zip (without portable.txt). UNINSTLIST (written by build-release.sh from that folder)
; lists exactly the files and folders the installer puts in $INSTDIR, so the uninstaller removes those and nothing else.

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!ifndef VERSION
  !error "pass -DVERSION=x.y.z"
!endif
!ifndef SRCDIR
  !error "pass -DSRCDIR=<folder with BlazmaAnimation.exe>"
!endif
!ifndef NUMVER
  !define NUMVER "${VERSION}"
!endif
!ifndef OUTFILE
  !define OUTFILE "BlazmaAnimation-${VERSION}-setup.exe"
!endif
!ifndef REPO
  !define REPO "..\.."
!endif

!define APPNAME "Blazma Animation"
!define APPKEY  "BlazmaAnimation"
!define EXE     "BlazmaAnimation.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPKEY}"
!define PROGID  "BlazmaAnimation.Project"
; Created by the running app on Windows (see AppController); with the process names below it tells us the app is open.
!define RUNNING_MUTEX "BlazmaAnimation-Running"
!define MODELSDIR "$LOCALAPPDATA\${APPKEY}\models"

Name "${APPNAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APPKEY}"
InstallDirRegKey HKCU "Software\${APPKEY}" "InstallDir"
RequestExecutionLevel user
BrandingText "${APPNAME} ${VERSION}"
Icon "${REPO}\packaging\assets\blazma.ico"
UninstallIcon "${REPO}\packaging\assets\blazma.ico"

VIProductVersion "${NUMVER}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "mr-kateba"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2025-2026 mr-kateba. All rights reserved."

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

!define MUI_ICON "${REPO}\packaging\assets\blazma.ico"
!define MUI_UNICON "${REPO}\packaging\assets\blazma.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Run ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Arabic"

LangString MSG_64BIT ${LANG_ENGLISH} "${APPNAME} needs 64-bit Windows (x64)."
LangString MSG_64BIT ${LANG_ARABIC} "يحتاج ${APPNAME} إلى نسخة Windows بنظام 64 بت (x64)."
LangString MSG_RUNNING ${LANG_ENGLISH} "${APPNAME} (or one of its AI tools, blazma-mcp / blazma-anim) is running.$\r$\nSave your work and close it, then click Retry."
LangString MSG_RUNNING ${LANG_ARABIC} "${APPNAME} (أو إحدى أدواته blazma-mcp / blazma-anim) يعمل الآن.$\r$\nاحفظ عملك وأغلقه ثم اضغط إعادة المحاولة."
LangString MSG_MODELS ${LANG_ENGLISH} "Also delete the downloaded AI models?$\r$\n$\r$\n$1 MB in $\"${MODELSDIR}$\"$\r$\n$\r$\nKeep them (No) if you plan to reinstall; they can also be downloaded again later."
LangString MSG_MODELS ${LANG_ARABIC} "هل تريد حذف نماذج الذكاء الاصطناعي التي نُزِّلت أيضًا؟$\r$\n$\r$\n$1 ميغابايت في $\"${MODELSDIR}$\"$\r$\n$\r$\nأبقِها (لا) إن كنت ستعيد التثبيت؛ ويمكن تنزيلها مجددًا لاحقًا."

; Asks until the app and its tools are closed (Retry) or the user gives up (Cancel). $0 is clobbered.
!macro WAIT_UNTIL_CLOSED UN
Function ${UN}WaitUntilClosed
  retry:
    StrCpy $0 0
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${RUNNING_MUTEX}") p .r1'
    ${If} $1 P<> 0
      System::Call 'kernel32::CloseHandle(p r1)'
      StrCpy $0 1
    ${EndIf}
    ${If} $0 == 0
      ; Same check by process name (an older build without the mutex, or the agent tools started by an AI client).
      nsExec::ExecToStack 'cmd /c tasklist /NH /FO CSV | findstr /I /C:"${EXE}" /C:"BlazmaAnimation.console.exe" /C:"blazma-mcp.exe" /C:"blazma-anim.exe"'
      Pop $1
      Pop $2
      ${If} $1 == 0
        StrCpy $0 1
      ${EndIf}
    ${EndIf}
    ${If} $0 == 1
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(MSG_RUNNING)" /SD IDCANCEL IDRETRY retry
      Abort
    ${EndIf}
FunctionEnd
!macroend
!insertmacro WAIT_UNTIL_CLOSED ""
!insertmacro WAIT_UNTIL_CLOSED "un."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(MSG_64BIT)" /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

Section "-check running"
  Call WaitUntilClosed
SectionEnd

Section "${APPNAME} (required)" SecApp
  SectionIn RO
  SetOutPath "$INSTDIR"
  ; upgrade: drop the previous engine/runtime files so no stale DLLs survive
  RMDir /r "$INSTDIR\data_Blazma.UI_windows_x86_64"
  RMDir /r "$INSTDIR\tools"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\legal"
  File /r "${SRCDIR}\*.*"

  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "Software\${APPKEY}" "InstallDir" "$INSTDIR"

  ; Start menu
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\uninstall.exe"

  ; Apps & features entry (per user)
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Associate .blazanim files" SecAssoc
  WriteRegStr HKCU "Software\Classes\.blazanim" "" "${PROGID}"
  WriteRegStr HKCU "Software\Classes\.blazanim\OpenWithProgids" "${PROGID}" ""
  WriteRegStr HKCU "Software\Classes\${PROGID}" "" "Blazma Animation Project"
  WriteRegStr HKCU "Software\Classes\${PROGID}\DefaultIcon" "" "$INSTDIR\${EXE},0"
  WriteRegStr HKCU "Software\Classes\${PROGID}\shell\open\command" "" '"$INSTDIR\${EXE}" "%1"'
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

LangString DESC_App ${LANG_ENGLISH} "The application, the AI agent tools (blazma-mcp, blazma-anim) and documentation."
LangString DESC_App ${LANG_ARABIC} "التطبيق وأدوات الذكاء الاصطناعي (blazma-mcp وblazma-anim) والتوثيق."
LangString DESC_Assoc ${LANG_ENGLISH} "Open .blazanim projects by double-clicking them (current user only)."
LangString DESC_Assoc ${LANG_ARABIC} "فتح ملفات .blazanim بالنقر المزدوج (للمستخدم الحالي فقط)."
LangString DESC_Desktop ${LANG_ENGLISH} "Add a shortcut on the desktop."
LangString DESC_Desktop ${LANG_ARABIC} "إضافة اختصار على سطح المكتب."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} $(DESC_App)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAssoc} $(DESC_Assoc)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_Desktop)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  Call un.WaitUntilClosed
FunctionEnd

Section "Uninstall"
  ; Projects and the user's settings (%APPDATA%\BlazmaAnimation) are never touched.
  Delete "$DESKTOP\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"

  ; File association: remove only what is ours. The .blazanim default goes only if it still points to us; other programs'
  ; OpenWithProgids entries (and the key, if they use it) stay.
  ReadRegStr $0 HKCU "Software\Classes\.blazanim" ""
  ${If} $0 == "${PROGID}"
    DeleteRegValue HKCU "Software\Classes\.blazanim" ""
  ${EndIf}
  DeleteRegValue HKCU "Software\Classes\.blazanim\OpenWithProgids" "${PROGID}"
  DeleteRegKey /ifempty HKCU "Software\Classes\.blazanim\OpenWithProgids"
  DeleteRegKey /ifempty HKCU "Software\Classes\.blazanim"
  DeleteRegKey HKCU "Software\Classes\${PROGID}"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\${APPKEY}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'

  ; Only what the installer put there (exact names), so a custom install folder with other files is never wiped.
!ifdef UNINSTLIST
  !include "${UNINSTLIST}"
!else
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\BlazmaAnimation.console.exe"
  Delete "$INSTDIR\BlazmaAnimation.pck"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\VERSION.txt"
  RMDir /r "$INSTDIR\data_Blazma.UI_windows_x86_64"
  RMDir /r "$INSTDIR\tools"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\legal"
!endif
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"

  ; Downloaded AI models (GBs, outside the install folder): offered, default No (also when silent).
  ${If} ${FileExists} "${MODELSDIR}\*.*"
    ${GetSize} "${MODELSDIR}" "/S=0M" $1 $2 $3
    MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION "$(MSG_MODELS)" /SD IDNO IDNO keepModels
    RMDir /r "${MODELSDIR}"
    RMDir "$LOCALAPPDATA\${APPKEY}"
    keepModels:
  ${EndIf}
SectionEnd
