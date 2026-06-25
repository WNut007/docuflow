# STEP ② (Option A vs B): test PP-Structure on a TIGHT line-item crop instead of the full page. The
# full-page captures showed PP-Structure swallowing the whole page into one shredded table; this feeds
# it only the table band a user would draw, to see if its native rows are clean (Option B) or still
# need a row-classifier (Option A).
#
# For each page it derives the line-item band from that page's ocr-page-N.json (column-header anchors,
# else full content extent for continuation pages), crops the backdrop PNG, and — if the sidecar is up —
# POSTs the crop to /structure and /ocr. Crops are always saved so they're ready for a later live run.
#   pwsh ./ocr-service/capture-crop.ps1
param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$UploadDir = "src/OcrPipeline.Web/App_Data/uploads",
    [string]$Hash = "322bf826f971449e878262c6f1d3b0d7",   # doc 74's Michelin upload (4 pages)
    [string]$OutDir = "ocr-service/captures",
    [int]$Pad = 18
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing

# Header tokens that mark the top of the line-item table on FIRST pages.
$headerRx = '^(Quantity|Description|Unit Price|Total Amount|Article Code)$'

# Is the sidecar reachable? If not we still produce crops, just skip the POSTs.
$serviceUp = $false
try { if ((Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5).status -eq "ok") { $serviceUp = $true } } catch {}
if (-not $serviceUp) { Write-Warning "Sidecar not reachable at $BaseUrl - cropping only, skipping POSTs. Bring it up then re-run to capture." }

$summary = @()
foreach ($p in 1..4) {
    $png = Join-Path $UploadDir "$Hash.page-$p.png"
    $ocrJson = Join-Path $OutDir "ocr-page-$p.json"
    if (-not (Test-Path $png)) { Write-Warning "missing page image $png"; continue }
    if (-not (Test-Path $ocrJson)) { Write-Warning "missing $ocrJson (run capture-michelin.ps1 first)"; continue }

    $o = Get-Content $ocrJson -Raw | ConvertFrom-Json
    $words = $o.words
    if (-not $words) { Write-Warning "page $p has no words"; continue }

    # content extent
    $minX = ($words | ForEach-Object { $_.bbox[0] } | Measure-Object -Min).Minimum
    $maxX = ($words | ForEach-Object { $_.bbox[2] } | Measure-Object -Max).Maximum
    $minY = ($words | ForEach-Object { $_.bbox[1] } | Measure-Object -Min).Minimum
    $maxY = ($words | ForEach-Object { $_.bbox[3] } | Measure-Object -Max).Maximum

    # top = column header (FIRST page) else first content line (continuation page)
    $header = $words | Where-Object { $_.text -match $headerRx }
    $top = if ($header) { ($header | ForEach-Object { $_.bbox[1] } | Measure-Object -Min).Minimum } else { $minY }

    $left   = [Math]::Max(0, [int]$minX - $Pad)
    $right  = [Math]::Min([int]$o.page_width,  [int]$maxX + $Pad)
    $topY   = [Math]::Max(0, [int]$top - $Pad)
    $botY   = [Math]::Min([int]$o.page_height, [int]$maxY + $Pad)
    $w = $right - $left; $h = $botY - $topY

    # crop with System.Drawing
    $crop = Join-Path $OutDir "crop-page-$p.png"
    $src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $png))
    try {
        $rect = New-Object System.Drawing.Rectangle $left, $topY, $w, $h
        $dst = $src.Clone($rect, $src.PixelFormat)
        try { $dst.Save($crop, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $dst.Dispose() }
    } finally { $src.Dispose() }

    $row = [pscustomobject]@{ page=$p; box="$left,$topY+${w}x${h}"; header=[bool]$header; tables=$null; rows=$null; words=$null; ms=$null }

    if ($serviceUp) {
        $s = Invoke-RestMethod "$BaseUrl/structure" -Method Post -Form @{ file = Get-Item $crop }
        $s | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $OutDir "structure-crop-page-$p.json")
        $row.tables = $s.table_count
        $row.rows = if ($s.tables) { ([regex]::Matches(($s.tables[0].html ?? ''), '<tr>')).Count } else { 0 }
        $row.ms = $s.elapsed_ms

        $oc = Invoke-RestMethod "$BaseUrl/ocr" -Method Post -Form @{ file = Get-Item $crop }
        $oc | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutDir "ocr-crop-page-$p.json")
        $row.words = $oc.word_count
    }
    $summary += $row
}

Write-Host "`n=== crop capture ($([int]$serviceUp ? 'POSTed' : 'crops only')) ==="
$summary | Format-Table -Auto
Write-Host "Crops + JSON under $OutDir/  (crop-page-N.png, structure-crop-page-N.json, ocr-crop-page-N.json)"
