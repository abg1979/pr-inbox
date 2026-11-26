$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    dotnet run --project src/PrInbox.Launcher @args
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $exitCode
