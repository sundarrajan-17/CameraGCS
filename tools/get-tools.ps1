# Downloads the two external tools used by the RTSP restreamer into this folder:
#   ffmpeg.exe   - remuxes the gimbal stream to RTSP without re-encoding (gyan.dev "essentials" build)
#   mediamtx.exe - lightweight RTSP server used in "Serve" mode
# Run once:  powershell -ExecutionPolicy Bypass -File tools\get-tools.ps1
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dest = $PSScriptRoot
$tmp  = Join-Path $env:TEMP ("epsilon-tools-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null

$mediamtxVersion = 'v1.9.3'
$mediamtxUrl = "https://github.com/bluenviron/mediamtx/releases/download/$mediamtxVersion/mediamtx_${mediamtxVersion}_windows_amd64.zip"
$ffmpegUrl   = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'

function Get-Zip($url, $name) {
    $zip = Join-Path $tmp "$name.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $out = Join-Path $tmp $name
    Expand-Archive -Path $zip -DestinationPath $out -Force
    return $out
}

try {
    if (-not (Test-Path (Join-Path $dest 'mediamtx.exe'))) {
        $d = Get-Zip $mediamtxUrl 'mediamtx'
        Copy-Item (Get-ChildItem $d -Recurse -Filter 'mediamtx.exe' | Select-Object -First 1).FullName $dest -Force
        Write-Host "  mediamtx.exe OK"
    } else { Write-Host "mediamtx.exe already present" }

    if (-not (Test-Path (Join-Path $dest 'ffmpeg.exe'))) {
        $d = Get-Zip $ffmpegUrl 'ffmpeg'
        Copy-Item (Get-ChildItem $d -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1).FullName $dest -Force
        Write-Host "  ffmpeg.exe OK"
    } else { Write-Host "ffmpeg.exe already present" }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Tools ready in $dest"
