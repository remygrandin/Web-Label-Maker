# Minimal Chrome DevTools Protocol driver for headless Edge: evaluate JS and save element screenshots.
param([string]$OutDir, [string]$Steps)

$ErrorActionPreference = 'Stop'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Edge not found' }
$port = 9333
$profile = Join-Path $OutDir '_profile'
if (Test-Path $profile) { Remove-Item -Recurse -Force $profile }
$proc = Start-Process -FilePath $edge -PassThru -ArgumentList @('--headless=new', "--remote-debugging-port=$port", "--user-data-dir=$profile", '--no-first-run', '--disable-gpu', '--hide-scrollbars', '--window-size=1280,760', 'about:blank')

try {
    $target = $null
    for ($i = 0; $i -lt 40 -and -not $target; $i++) {
        Start-Sleep -Milliseconds 300
        try { $target = (Invoke-RestMethod "http://127.0.0.1:$port/json") | Where-Object { $_.type -eq 'page' } | Select-Object -First 1 } catch { }
    }
    if (-not $target) { throw 'no page target' }

    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:nextId = 0

    function Send-Cdp([string]$method, $params = @{}) {
        $script:nextId++
        $id = $script:nextId
        $json = (@{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress)
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ws.SendAsync((New-Object ArraySegment[byte] (, $bytes)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $buffer = New-Object byte[] 1048576
        while ($true) {
            $ms = New-Object IO.MemoryStream
            do {
                $r = $ws.ReceiveAsync((New-Object ArraySegment[byte] (, $buffer)), [Threading.CancellationToken]::None).Result
                $ms.Write($buffer, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
            if ($text -match ('^\{"id":' + $id + '[,}]')) {
                $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
                $ser.MaxJsonLength = [int]::MaxValue
                return $ser.DeserializeObject($text)
            }
        }
    }
    Add-Type -AssemblyName System.Web.Extensions

    function Eval([string]$js) {
        $r = Send-Cdp 'Runtime.evaluate' @{ expression = "(async () => { $js })()"; awaitPromise = $true; returnByValue = $true }
        if ($r.result.exceptionDetails) { throw ("JS error: " + ($r.result.exceptionDetails.exception.description)) }
        return $r.result.result.value
    }

    function Wait-For([string]$cond, [int]$seconds = 30) {
        for ($i = 0; $i -lt $seconds * 4; $i++) {
            if ((Eval "return !!($cond);") -eq $true) { return }
            Start-Sleep -Milliseconds 250
        }
        throw "timed out waiting for: $cond"
    }

    # Screenshot of the union of the elements matching the selectors (or the whole viewport), with a margin.
    function Shot([string]$name, [string[]]$selectors = @(), [int]$pad = 8) {
        $params = @{ format = 'png' }
        if ($selectors.Count -gt 0) {
            $list = ($selectors | ForEach-Object { "'" + ($_ -replace "'", "\'") + "'" }) -join ','
            $rect = Eval "const els = [$list].flatMap(s => [...document.querySelectorAll(s)]); if (!els.length) return null; const r = els.map(e => e.getBoundingClientRect()); const x0 = Math.min(...r.map(b => b.left)), y0 = Math.min(...r.map(b => b.top)), x1 = Math.max(...r.map(b => b.right)), y1 = Math.max(...r.map(b => b.bottom)); return [x0, y0, x1, y1].join(',');"
            if (-not $rect) { throw "nothing matches $selectors" }
            $p = $rect.Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
            $x = [Math]::Max(0, $p[0] - $pad); $y = [Math]::Max(0, $p[1] - $pad)
            $w = [Math]::Min(1280, $p[2] + $pad) - $x; $h = [Math]::Min(760, $p[3] + $pad) - $y
            $params.clip = @{ x = $x; y = $y; width = $w; height = $h; scale = 1 }
        }
        $r = Send-Cdp 'Page.captureScreenshot' $params
        $file = Join-Path $OutDir "$name.png"
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($r.result.data))
        Write-Output "saved $name.png"
    }

    Send-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = 1280; height = 760; deviceScaleFactor = 1.5; mobile = $false } | Out-Null
    Send-Cdp 'Page.enable' | Out-Null

    . $Steps
}
finally {
    try { if ($ws) { $ws.Dispose() } } catch { }
    try { Stop-Process -Id $proc.Id -Force } catch { }
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
}
