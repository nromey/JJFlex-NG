; based on example2.nsi
;
; This script is based on example1.nsi, but it remember the directory, 
; has uninstall support and (optionally) installs start menu shortcuts.
;
; It will install JJFlexRadio.nsi into a directory that the user selects,

;--------------------------------

; The name of the installer
Name "JJFlexRadio"
; The file to write (version appended via 4.1.115.0)
OutFile "Setup JJFlexRadio_4.1.115.0.exe"

; The default installation directory (architecture-specific Program Files)
InstallDir "$PROGRAMFILES\JJFlexRadio"

; Registry key to check for directory (so if you install again, it will 
; overwrite the old one automatically)
InstallDirRegKey HKLM "Software\NSIS_JJFlexRadio" "Install_Dir"

; Request application privileges for Windows Vista
RequestExecutionLevel admin

; LZMA solid compression - compresses all files as one stream for best size
SetCompressor /SOLID lzma
SetCompressorDictSize 64


; Version information for the installer bundle
VIProductVersion "4.1.115.0"
VIFileVersion "4.1.115.0"
VIAddVersionKey /LANG=1033 "ProductVersion" "4.1.115.0"
VIAddVersionKey /LANG=1033 "FileVersion" "4.1.115.0"
VIAddVersionKey /LANG=1033 "ProductName" "JJFlexRadio"
VIAddVersionKey /LANG=1033 "FileDescription" "JJFlexRadio installer"


; Get a welcome message
Function .onInit
MessageBox MB_OK "\
Welcome to JJFlexRadio, an amateur radio monitoring/control program by Jim Shaffer, KE5AL (SK) and Noel Romey K5NER.$\r\
With assistance from Anthropic's Claude and ChatGPT's Codex.$\r\
JJFlexRadio is designed with blind users in mind, but anyone is encouraged to try it out.$\r\r\
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
Section "JJFlexRadio (required)"

  SectionIn RO

  ; install for all users.
  SetShellVarContext all  
  
  ; Set output path to the installation directory.
  SetOutPath $INSTDIR
  
  ; Put files there - recurse all built outputs
  File /r /x "*.pdb" /x "runPgm.bat" "c:\dev\JJFlex-NG\\bin\x86\Release\net8.0-windows\win-x86\*.*"
  
  ; Write the installation path into the registry
  WriteRegStr HKLM "SOFTWARE\JJFlexRadio" "Install_Dir" "$INSTDIR"
  
  ; Write the uninstall keys for Windows
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio" "DisplayName" "JJFlexRadio"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio" "NoRepair" 1
  WriteUninstaller "$INSTDIR\uninstall.exe"
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Start Menu Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$SMPROGRAMS\JJFlexRadio.lnk" "$INSTDIR\JJFlexRadio.exe" ""
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Desktop Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$DESKTOP\JJFlexRadio.lnk" "$INSTDIR\JJFlexRadio.exe" ""
  
SectionEnd
;--------------------------------

; Uninstaller

Section "Uninstall"

  ; uninstall for all users.
  SetShellVarContext all  
  
  ; Remove registry keys
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio"
  DeleteRegKey HKLM "SOFTWARE\JJFlexRadio"

  ; Remove files
!include "deleteList.txt"
  Delete "$INSTDIR\uninstall.exe"

  ; Remove shortcuts, if any
  Delete "$SMPROGRAMS\JJFlexRadio.lnk"
  Delete "$DESKTOP\JJFlexRadio.lnk"

  ; Remove directories used
  RMDir /r "$INSTDIR\runtimes"
  RMDir "$INSTDIR"

SectionEnd

