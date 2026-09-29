# Noctis Local API from PowerShell (Windows PowerShell 5.1 or PowerShell 7).
# Turn the API on first: Settings > Account & Devices > Local API. Reference: docs/LOCAL-API.md
#
#   .\local-api.ps1                  # what's playing
#   .\local-api.ps1 toggle           # play/pause
#   .\local-api.ps1 next | previous | play | pause
#   .\local-api.ps1 volume 40
#   .\local-api.ps1 search "daft punk"
#   .\local-api.ps1 events           # stream events until Ctrl+C
param(
    [string]$Command = "now",
    [string]$Arg
)
$ErrorActionPreference = "Stop"

# Discovery: port and token live in local-api.json in the Noctis data folder.
# NOCTIS_DATA_DIR overrides the folder the same way it does for the app.
$dataDir = if ($env:NOCTIS_DATA_DIR) { $env:NOCTIS_DATA_DIR } else { Join-Path $env:APPDATA "Noctis" }
$info = Get-Content (Join-Path $dataDir "local-api.json") -Raw | ConvertFrom-Json
if (-not $info.running) { throw "The Local API is off. Turn it on in Settings > Account & Devices." }

$base = $info.baseUrl                                   # http://127.0.0.1:<port>/api/v1
$headers = @{ Authorization = "Bearer $($info.token)" }

function Get-Api($path) { Invoke-RestMethod -Uri "$base$path" -Headers $headers }
function Post-Api($path, $body) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Compress } else { "" }
    Invoke-RestMethod -Method Post -Uri "$base$path" -Headers $headers -ContentType "application/json" -Body $json
}

switch ($Command) {
    "now" {
        $np = Get-Api "/now-playing"
        if (-not $np.track) { "Nothing playing"; break }
        "{0} - {1}  [{2}]  {3:mm\:ss} / {4:mm\:ss}" -f ($np.artists -join ", "), $np.title, $np.state,
            [TimeSpan]::FromMilliseconds($np.positionMs), [TimeSpan]::FromMilliseconds($np.durationMs)
    }
    { $_ -in "play", "pause", "toggle", "next", "previous" } { (Post-Api "/playback/$Command").playback }
    "volume" { (Post-Api "/playback/volume" @{ volume = [int]$Arg }).playback }
    "seek" { (Post-Api "/playback/seek" @{ positionMs = [int]$Arg * 1000 }).playback }
    "search" {
        $r = Get-Api ("/library/search?limit=10&q=" + [uri]::EscapeDataString($Arg))
        $r.tracks | Format-Table title, artist, album, id -AutoSize
    }
    "queue-next" { Post-Api "/queue/add" @{ trackIds = @($Arg); mode = "next" } }
    "lyrics" {
        $l = Get-Api "/lyrics/current"
        if ($l.available) { $l.text } else { "No lyrics loaded" }
    }
    "events" {
        # Invoke-RestMethod buffers the whole response, so read the stream by hand.
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
        $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $info.token)
        $stream = $client.GetStreamAsync("$base/events?lyrics=1").GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)
        while ($null -ne ($line = $reader.ReadLine())) { if ($line) { $line } }
    }
    default { throw "Unknown command '$Command'." }
}
