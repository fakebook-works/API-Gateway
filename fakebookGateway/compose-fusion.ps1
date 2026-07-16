param(
    [ValidateSet("Development", "Production")]
    [string]$Environment = "Production",
    [string]$Archive = ""
)

$ErrorActionPreference = "Stop"

$archiveName = if ([string]::IsNullOrWhiteSpace($Archive)) {
    if ($Environment -eq "Development") { "gateway.local.far" } else { "gateway.far" }
}
else {
    $Archive
}

$archivePath = if ([IO.Path]::IsPathRooted($archiveName)) {
    $archiveName
}
else {
    Join-Path $PSScriptRoot $archiveName
}

$nitroCommand = Get-Command nitro -ErrorAction SilentlyContinue
$localNitroPath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..\..\.tools\nitro.exe"))
$nitroExecutable = if ($nitroCommand) {
    $nitroCommand.Source
}
elseif (Test-Path -LiteralPath $localNitroPath -PathType Leaf) {
    $localNitroPath
}
else {
    throw "Nitro CLI 16.1.3 is required. Install it globally or place nitro.exe at '$localNitroPath'."
}

$sourceSchemas = @(
    ".\Gateway\schema\Authentication",
    ".\Gateway\schema\SocialGraph",
    ".\Gateway\schema\Recommendation",
    ".\Gateway\schema\Search",
    ".\Gateway\schema\Messaging",
    ".\Gateway\schema\Notification",
    ".\Gateway\schema\Payment"
)

Push-Location $PSScriptRoot
try {
    $arguments = @("fusion", "compose")
    foreach ($sourceSchema in $sourceSchemas) {
        $arguments += @("--source-schema-file", $sourceSchema)
    }

    $arguments += @(
        "--archive", $archivePath,
        "--env", $Environment,
        "--include-satisfiability-paths",
        "--output", "json"
    )

    & $nitroExecutable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Fusion composition failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
