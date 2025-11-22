; based on example2.nsi
;
; This script is based on example1.nsi, but it remember the directory, 
; has uninstall support and (optionally) installs start menu shortcuts.
;
; It will install JJFlexRadio.nsi into a directory that the user selects,

;--------------------------------

; The name of the installer
Name "JJFlexRadio 3.2.37"
; The file to write
OutFile "Setup JJFlexRadio 3.2.37.exe"

; The default installation directory
InstallDir "$PROGRAMFILES\jjshaffer\JJFlexRadio 3.2.37"

; Registry key to check for directory (so if you install again, it will 
; overwrite the old one automatically)
InstallDirRegKey HKLM "Software\NSIS_JJFlexRadio 3.2.37" "Install_Dir"

; Request application privileges for Windows Vista
RequestExecutionLevel admin


; Get a welcome message
Function .onInit
MessageBox MB_OK "\
Welcome to JJFlexRadio, an amateur radio monitoring/control program by Jim Shaffer, KE5AL.$\r\
JJFlexRadio is designed with blind users in mind.$\r\
It works best with a screen reader using a braille display."
FunctionEnd

;--------------------------------

; Pages

Page components
Page directory
Page instfiles

UninstPage uninstConfirm
UninstPage instfiles

;--------------------------------

; The stuff to install
Section "JJFlexRadio (required)"

  SectionIn RO

  ; install for all users.
  SetShellVarContext all  
  
  ; Set output path to the installation directory.
  SetOutPath $INSTDIR
  
  ; Put files there
  File /x src "bin\release\*.*"
  
  ; Write the installation path into the registry
  WriteRegStr HKLM "SOFTWARE\JJFlexRadio 3.2.37" "Install_Dir" "$INSTDIR"
  
  ; Write the uninstall keys for Windows
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio 3.2.37" "DisplayName" "JJFlexRadio 3.2.37"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio 3.2.37" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio 3.2.37" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio 3.2.37" "NoRepair" 1
  WriteUninstaller "$INSTDIR\uninstall.exe"
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Start Menu Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$SMPROGRAMS\JJFlexRadio 3.2.37.lnk" "$INSTDIR\JJFlexRadio 3.2.37.exe" ""
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Desktop Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$DESKTOP\JJFlexRadio 3.2.37.lnk" "$INSTDIR\JJFlexRadio 3.2.37.exe" ""
  
SectionEnd
;--------------------------------

; Uninstaller

Section "Uninstall"

  ; uninstall for all users.
  SetShellVarContext all  
  
  ; Remove registry keys
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio 3.2.37"
  DeleteRegKey HKLM "SOFTWARE\JJFlexRadio 3.2.37"

  ; Remove files
!include "deleteList.txt"
  Delete "$INSTDIR\uninstall.exe"

  ; Remove shortcuts, if any
  Delete "$SMPROGRAMS\JJFlexRadio 3.2.37.lnk"
  Delete "$DESKTOP\JJFlexRadio 3.2.37.lnk"

  ; Remove directories used
  RMDir "$INSTDIR"

SectionEnd
