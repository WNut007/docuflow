# Generates samples/thai-invoice.png — a simple SYNTHETIC Thai-language invoice fixture used by the
# accuracy tests. NOTE: the offline test does NOT read this image; it feeds a synthetic
# OcrExtraction modelling the same content. This PNG is a human-facing fixture that shows the kind
# of document (Thai digits ๐-๙, Buddhist-era พ.ศ. date) the Thai assertions cover.
#
# Requires Windows (System.Drawing + a Thai-capable font such as Tahoma).
# Run:  pwsh -File scripts/generate-thai-sample.ps1
Add-Type -AssemblyName System.Drawing

$W = 750; $H = 1061
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$g.TextRenderingHint = 'AntiAliasGridFit'
$black = [System.Drawing.Brushes]::Black
$gray = [System.Drawing.Pens]::Gray

$title = New-Object System.Drawing.Font('Tahoma', 20, [System.Drawing.FontStyle]::Bold)
$font  = New-Object System.Drawing.Font('Tahoma', 12)
$bold  = New-Object System.Drawing.Font('Tahoma', 12, [System.Drawing.FontStyle]::Bold)
function T($s, $f, $x, $y) { $g.DrawString($s, $f, $black, [single]$x, [single]$y) }

T 'บริษัท ตัวอย่าง จำกัด' $title 55 55
T 'ใบแจ้งหนี้' $title 560 55
T '123 ถนนสุขุมวิท กรุงเทพฯ 10110' $font 55 100

T 'เลขที่ใบแจ้งหนี้' $bold 420 250; T 'INV-๒๕๖๒-๐๐๑' $font 600 250
T 'วันที่' $bold 420 282;          T '๑๑/๐๒/๒๕๖๒' $font 600 282
T 'ครบกำหนด' $bold 420 314;        T '๒๖/๐๒/๒๕๖๒' $font 600 314

$g.DrawRectangle($gray, 55, 380, 640, 130)
T 'รายการ' $bold 70 388; T 'จำนวน' $bold 360 388; T 'ราคา' $bold 470 388; T 'จำนวนเงิน' $bold 575 388
T 'ค่าบริการซ่อม' $font 70 423; T '๑' $font 380 423; T '๑,๐๐๐.๐๐' $font 460 423; T '๑,๐๐๐.๐๐' $font 580 423
T 'อะไหล่' $font 70 458;       T '๒' $font 380 458; T '๑๑๗.๒๘' $font 470 458;   T '๒๓๔.๕๖' $font 590 458

T 'ยอดรวม' $bold 470 540;     T '๑,๒๓๔.๕๖' $font 585 540
T 'ภาษี ๗%' $bold 470 572;    T '๘๖.๔๒' $font 600 572
T 'รวมทั้งสิ้น' $bold 470 608; T '๑,๓๒๐.๙๘' $bold 585 608

$out = Join-Path $PSScriptRoot '..\samples\thai-invoice.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "wrote $out"
