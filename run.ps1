#!/usr/bin/env -S pwsh -NoProfile
$ErrorActionPreference = 'Stop'

$originalPwd = $env:PWD
Push-Location $PSScriptRoot
try {
    if (-not $IsWindows) {
        # .NET honors PWD, which may still name an aliased checkout path.
        $physicalPath = & /bin/pwd -P
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not resolve the physical working directory.'
        }
        Set-Location -LiteralPath $physicalPath
        $env:PWD = $physicalPath
    }

    dotnet run --project src/PrInbox.Launcher @args
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
    $env:PWD = $originalPwd
}

exit $exitCode
