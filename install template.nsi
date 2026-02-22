; based on example2.nsi
;
; This script is based on example1.nsi, but it remember the directory, 
; has uninstall support and (optionally) installs start menu shortcuts.
;
; It will install JJFlexRadio.nsi into a directory that the user selects,

;--------------------------------

; The name of the installer
Name "MYPGM"
; The file to write (version appended via MYVER)
OutFile "Setup MYPGM_MYVER.exe"

; The default installation directory (architecture-specific Program Files)
InstallDir "MYPROGFILES\MYPGM"

; Registry key to check for directory (so if you install again, it will 
; overwrite the old one automatically)
InstallDirRegKey HKLM "Software\NSIS_MYPGM" "Install_Dir"

; Request application privileges for Windows Vista
RequestExecutionLevel admin

; LZMA solid compression - compresses all files as one stream for best size
SetCompressor /SOLID lzma
SetCompressorDictSize 64


; Version information for the installer bundle
VIProductVersion "MYVER"
VIFileVersion "MYVER"
VIAddVersionKey /LANG=1033 "ProductVersion" "MYVER"
VIAddVersionKey /LANG=1033 "FileVersion" "MYVER"
VIAddVersionKey /LANG=1033 "ProductName" "MYPGM"
VIAddVersionKey /LANG=1033 "FileDescription" "MYPGM installer"


; Get a welcome message
Function .onInit
MessageBox MB_OK "\
Welcome to MYPGM, an amateur radio monitoring/control program by Jim Shaffer, KE5AL (SK) and Noel Romey K5NER.$\r\
With assistance from Anthropic's Claude and ChatGPT's Codex.$\r\
MYPGM is designed with blind users in mind, but anyone is encouraged to try it out.$\r\r\
The application works well with braille displays, but speech output continues to improve. Stay tuned!$\r\
JJ Flex Radio would not exist without the hard work of Jim Shaffer. JJ buddy, we miss you.$\r\
JJ Flex Radio lives on! RIP my friend."
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
Section "MYPGM (required)"

  SectionIn RO

  ; install for all users.
  SetShellVarContext all  
  
  ; Set output path to the installation directory.
  SetOutPath $INSTDIR
  
  ; Put files there - recurse all built outputs
  File /r /x "*.pdb" /x "runPgm.bat" "MYOUTDIR\*.*"
  
  ; Write the installation path into the registry
  WriteRegStr HKLM "SOFTWARE\MYPGM" "Install_Dir" "$INSTDIR"
  
  ; Write the uninstall keys for Windows
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\MYPGM" "DisplayName" "MYPGM"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\MYPGM" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\MYPGM" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\MYPGM" "NoRepair" 1
  WriteUninstaller "$INSTDIR\uninstall.exe"
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Start Menu Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$SMPROGRAMS\MYPGM.lnk" "$INSTDIR\MYPGM.exe" ""
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Desktop Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$DESKTOP\MYPGM.lnk" "$INSTDIR\MYPGM.exe" ""
  
SectionEnd
;--------------------------------

; Uninstaller

Section "Uninstall"

  ; uninstall for all users.
  SetShellVarContext all  
  
  ; Remove registry keys
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\MYPGM"
  DeleteRegKey HKLM "SOFTWARE\MYPGM"

  ; Remove files
!include "deleteList.txt"
  Delete "$INSTDIR\uninstall.exe"

  ; Remove shortcuts, if any
  Delete "$SMPROGRAMS\MYPGM.lnk"
  Delete "$DESKTOP\MYPGM.lnk"

  ; Remove directories used
  RMDir /r "$INSTDIR\runtimes"
  RMDir "$INSTDIR"

SectionEnd
