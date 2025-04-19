@echo off
setlocal enabledelayedexpansion

echo Building CS2 Slots Tracker Plugin...
dotnet publish -c Release

echo Cleaning up unnecessary files...
cd build\counterstrikesharp\plugins\cs2-slots-tracker
del /f /q *.pdb
del /f /q *.deps.json

echo Creating zip file...
set "zipfile=cs2-slots-tracker.zip"
if exist %zipfile% del /f /q %zipfile%

echo Adding files to zip...
powershell Compress-Archive -Path * -DestinationPath %zipfile%

echo Removing unnecessary DLLs from zip...
powershell -command "& {
  $zip = [System.IO.Compression.ZipFile]::Open('%zipfile%', 'Update')
  foreach ($entry in $zip.Entries) {
    if ($entry.Name -ne 'cs2-slots-tracker.dll' -and 
        $entry.Name -ne 'config.json' -and 
        (-not $entry.Name.EndsWith('.xml'))) {
      Write-Host ('Removing {0}' -f $entry.FullName)
      $entry.Delete()
    }
  }
  $zip.Dispose()
}"

echo Build complete! Output: build\counterstrikesharp\plugins\cs2-slots-tracker\%zipfile%
cd ..\..\..\..

endlocal
