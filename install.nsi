; based on example2.nsi
;
; This script is based on example1.nsi, but it remember the directory, 
; has uninstall support and (optionally) installs start menu shortcuts.
;
; It will install JJFlexRadio.nsi into a directory that the user selects,
;
; TOKENS substituted by install.bat:
;   JJFlexRadio        package identity ??? "JJFlexRadio". Drives the install directory,
;                the registry keys, and the shortcut names. Deliberately NOT the
;                executable name: keeping it stable is what makes an upgrade land
;                on top of an existing 4.x install instead of beside it.
;   jjflexible        executable base name ??? "jjflexible" (so "$INSTDIR\jjflexible.exe").
;   4.1.16.418        4-part version
;   C:\dev\jjflex-rename\\bin\x86\Release\net10.0-windows\win-x86     build output directory to package
;   $PROGRAMFILES  $PROGRAMFILES64 or $PROGRAMFILES

;--------------------------------

; The name of the installer
Name "JJFlexRadio"
; The file to write (version appended via 4.1.16.418)
OutFile "Setup JJFlexRadio_4.1.16.418.exe"

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
VIProductVersion "4.1.16.418"
VIFileVersion "4.1.16.418"
VIAddVersionKey /LANG=1033 "ProductVersion" "4.1.16.418"
VIAddVersionKey /LANG=1033 "FileVersion" "4.1.16.418"
VIAddVersionKey /LANG=1033 "ProductName" "JJ Flexible Radio Access"
VIAddVersionKey /LANG=1033 "FileDescription" "JJ Flexible Radio Access installer"


; Get a welcome message
Function .onInit
MessageBox MB_OK "\
Welcome to JJ Flexible Radio Access, an amateur radio monitoring/control program by Jim Shaffer, KE5AL (SK) and Noel Romey K5NER.$\r\
With assistance from Anthropic's Claude and ChatGPT's Codex.$\r\
JJ Flexible Radio Access is designed with blind users in mind, but anyone is encouraged to try it out.$\r\r\
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
  File /r /x "*.pdb" /x "runPgm.bat" "C:\dev\jjflex-rename\\bin\x86\Release\net10.0-windows\win-x86\*.*"

  ; Include changelog
  File "docs\CHANGELOG.md"

  ; --- Upgrade cleanup: retire the pre-rename executable -------------------
  ; Up to 4.x the app shipped as JJFlexRadio.exe. deleteList.txt is generated
  ; from the NEW publish output, so nothing else removes the old file set ???
  ; an upgraded machine would keep a launchable stale exe sitting next to the
  ; new one. Delete the old exe and its paired runtime files explicitly.
  ; NOTE: JJFlexRadio.chm is NOT in this list. The help file keeps its name
  ; (HelpLauncher.cs looks it up literally) and is still shipped.
  Delete "$INSTDIR\JJFlexRadio.exe"
  Delete "$INSTDIR\JJFlexRadio.dll"
  Delete "$INSTDIR\JJFlexRadio.pdb"
  Delete "$INSTDIR\JJFlexRadio.deps.json"
  Delete "$INSTDIR\JJFlexRadio.runtimeconfig.json"
  Delete "$INSTDIR\JJFlexRadio.dll.config"
  Delete "$INSTDIR\JJFlexRadio.xml"

  ; Old shortcuts point at the exe we just deleted. Remove them here rather
  ; than only in the shortcut sections below ??? those are optional components,
  ; and a user who unticks them on upgrade would otherwise be left with Start
  ; Menu and desktop shortcuts that launch nothing. The sections re-create
  ; them immediately after if selected.
  Delete "$SMPROGRAMS\JJFlexRadio.lnk"
  Delete "$DESKTOP\JJFlexRadio.lnk"

  ; Write the installation path into the registry
  WriteRegStr HKLM "SOFTWARE\JJFlexRadio" "Install_Dir" "$INSTDIR"
  
  ; Write the uninstall keys for Windows
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio" "DisplayName" "JJ Flexible Radio Access"
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
  
  CreateShortcut "$SMPROGRAMS\JJFlexRadio.lnk" "$INSTDIR\jjflexible.exe" ""
  
SectionEnd

; Optional section (can be disabled by the user)
Section "Desktop Shortcuts"

  ; install for all users.
  SetShellVarContext all  
  
  ; working dirrectory
  SetOutPath $INSTDIR
  
  CreateShortcut "$DESKTOP\JJFlexRadio.lnk" "$INSTDIR\jjflexible.exe" ""
  
SectionEnd
;--------------------------------

; Uninstaller

Section "Uninstall"

  ; uninstall for all users.
  SetShellVarContext all  
  
  ; Remove registry keys
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\JJFlexRadio"
  DeleteRegKey HKLM "SOFTWARE\JJFlexRadio"

  ; Remove files and subdirectories.
  ; Sprint 29 Track J: deleteList.txt now carries Delete lines for every file
  ; (recursive walk) and RMDir /r lines for every top-level subdir of the
  ; install (runtimes, help, Resources, satellite-resource dirs cs/de/es/...
  ; introduced by the self-contained .NET 10 publish). Generated by
  ; install.bat at build time so any new subdir the publish drops gets cleaned
  ; up automatically.
!include "deleteList.txt"
  Delete "$INSTDIR\uninstall.exe"

  ; Pre-rename leftovers, in case this install dir was upgraded from a 4.x
  ; JJFlexRadio.exe build before the install-time cleanup above existed.
  Delete "$INSTDIR\JJFlexRadio.exe"
  Delete "$INSTDIR\JJFlexRadio.dll"
  Delete "$INSTDIR\JJFlexRadio.pdb"
  Delete "$INSTDIR\JJFlexRadio.deps.json"
  Delete "$INSTDIR\JJFlexRadio.runtimeconfig.json"
  Delete "$INSTDIR\JJFlexRadio.dll.config"
  Delete "$INSTDIR\JJFlexRadio.xml"

  ; Remove shortcuts, if any
  Delete "$SMPROGRAMS\JJFlexRadio.lnk"
  Delete "$DESKTOP\JJFlexRadio.lnk"

  ; Final cleanup of the install root (only succeeds if empty ??? by design,
  ; so any user-added files in the install dir are not silently destroyed).
  RMDir "$INSTDIR"

SectionEnd

