# check-jjflex-processes.ps1 — the #21 orphan-process check, without Task
# Manager spelunking. Run it from anywhere:
#
#   & "C:\dev\JJFlex-NG\check-jjflex-processes.ps1"
#
# Output is one plain sentence a screen reader can settle on. Zero processes
# after you have exited the app is the pass; anything else lists each stray
# with its start time so you can tell a fresh launch from a ghost.
#
# Exit code = number of jjflexible processes found (0 = clean), so scripts
# can chain on it too.

$procs = @(Get-Process jjflexible -ErrorAction SilentlyContinue)

if ($procs.Count -eq 0) {
    Write-Output "Clean: no JJ Flex processes are running."
}
elseif ($procs.Count -eq 1) {
    $p = $procs[0]
    Write-Output ("One JJ Flex process is running, process id " + $p.Id + ", started " + $p.StartTime.ToString("h:mm:ss tt") + ". If you just exited the app, this is a stray.")
}
else {
    Write-Output ("" + $procs.Count + " JJ Flex processes are running. If the app is open that should be exactly one - the rest are strays:")
    foreach ($p in $procs) {
        Write-Output ("  Process id " + $p.Id + ", started " + $p.StartTime.ToString("h:mm:ss tt"))
    }
}

exit $procs.Count
