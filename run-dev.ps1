# One-command dev flow: bring up the Dockerized PP-Structure service, wait for it to be healthy, then
# run the .NET app on the host. Requires Docker Desktop running.
#   pwsh ./run-dev.ps1            # service + app
#   pwsh ./run-dev.ps1 -Capture   # service + STEP-① Michelin capture (no app)
param([switch]$Capture)
$ErrorActionPreference = "Stop"

Write-Host "Building + starting paddle-structure (first build is multi-GB / several minutes)..."
docker compose up -d --build paddle-structure

Write-Host "Waiting for /health..."
$ok = $false
foreach ($i in 1..40) {
    try { if ((Invoke-RestMethod "http://localhost:8080/health" -TimeoutSec 5).status -eq "ok") { $ok = $true; break } } catch {}
    Start-Sleep 5
}
if (-not $ok) { Write-Error "paddle-structure did not become healthy — check: docker compose logs paddle-structure"; exit 1 }
Write-Host "paddle-structure healthy at http://localhost:8080"

if ($Capture) { pwsh ./ocr-service/capture-michelin.ps1; exit $LASTEXITCODE }

Push-Location src/OcrPipeline.Web
try { dotnet run } finally { Pop-Location }
