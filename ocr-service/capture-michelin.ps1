# STEP ① validation: POST the 4 Michelin backdrop PNGs to the running PP-Structure service, save the
# real JSON, and report per-page latency. Run AFTER `docker compose up -d --build paddle-structure`.
#   pwsh ./ocr-service/capture-michelin.ps1
param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$UploadDir = "src/OcrPipeline.Web/App_Data/uploads",
    [string]$Hash = "322bf826f971449e878262c6f1d3b0d7",   # doc 74's Michelin upload (4 pages)
    [string]$OutDir = "ocr-service/captures"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null

# health gate
try { Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 10 | Out-Null }
catch { Write-Error "Service not reachable at $BaseUrl — is `docker compose up` running and healthy?"; exit 1 }

$sumStruct = @(); $sumOcr = @()
foreach ($p in 1..4) {
    $png = Join-Path $UploadDir "$Hash.page-$p.png"
    if (-not (Test-Path $png)) { Write-Warning "missing $png"; continue }

    $s = Invoke-RestMethod "$BaseUrl/structure" -Method Post -Form @{ file = Get-Item $png }
    $s | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $OutDir "structure-page-$p.json")
    $sumStruct += [pscustomobject]@{ page=$p; tables=$s.table_count; ms=$s.elapsed_ms; w=$s.page_width; h=$s.page_height }

    $o = Invoke-RestMethod "$BaseUrl/ocr" -Method Post -Form @{ file = Get-Item $png }
    $o | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutDir "ocr-page-$p.json")
    $sumOcr += [pscustomobject]@{ page=$p; words=$o.word_count; ms=$o.elapsed_ms }
}

Write-Host "`n=== /structure (tables + latency) ===" ; $sumStruct | Format-Table -Auto
Write-Host "=== /ocr (words + latency) ===" ; $sumOcr | Format-Table -Auto
Write-Host "JSON saved under $OutDir/  (structure-page-N.json holds raw_debug = the real PP-Structure shape)"
