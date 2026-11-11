#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]] $TracePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($path in $TracePath) {
    $stream = [IO.File]::OpenRead($path)
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($stream, [System.Text.Json.JsonDocumentOptions]::new())
        $pending = @{}
        $durations = [Collections.Generic.List[double]]::new()
        $firstUpdates = 0
        $inertialUpdates = 0
        $lastScrollInputTimestamp = 0L
        $lastScrollPresentationTimestamp = 0L
        foreach ($entry in $document.RootElement.GetProperty('traceEvents').EnumerateArray()) {
            $name = [System.Text.Json.JsonElement]::new()
            if (-not $entry.TryGetProperty('name', [ref]$name) -or $name.GetString() -ne 'EventLatency') { continue }
            $phase = $entry.GetProperty('ph').GetString()
            if ($phase -notin @('b', 'e')) { continue }
            $identity = $entry.GetProperty('id2')
            $id = [System.Text.Json.JsonElement]::new()
            if ($identity.TryGetProperty('local', [ref]$id)) { $scope = $entry.GetProperty('pid').GetRawText() + ':local' }
            elseif ($identity.TryGetProperty('global', [ref]$id)) { $scope = 'global' }
            else { throw "Unrecognized EventLatency identifier in $path." }
            $key = $scope + ':' + $id.GetString()
            $timestamp = $entry.GetProperty('ts').GetInt64()
            if ($phase -eq 'b') {
                # Async IDs are reused; a later begin must replace any earlier event.
                [void]$pending.Remove($key)
                $type = $entry.GetProperty('args').GetProperty('event_latency').GetProperty('event_type').GetString()
                if ($type -in @('FIRST_GESTURE_SCROLL_UPDATE', 'GESTURE_SCROLL_UPDATE', 'INERTIAL_GESTURE_SCROLL_UPDATE')) {
                    $pending[$key] = @{ Timestamp = $timestamp; Type = $type }
                }
            } elseif ($pending.ContainsKey($key)) {
                $event = $pending[$key]
                $duration = ($timestamp - $event.Timestamp) / 1000.0
                if ($duration -lt 0) { throw "Negative scroll-presentation duration in $path." }
                $durations.Add($duration)
                $lastScrollInputTimestamp = [Math]::Max($lastScrollInputTimestamp, $event.Timestamp)
                $lastScrollPresentationTimestamp = [Math]::Max($lastScrollPresentationTimestamp, $timestamp)
                if ($event.Type -eq 'FIRST_GESTURE_SCROLL_UPDATE') { $firstUpdates++ }
                elseif ($event.Type -eq 'INERTIAL_GESTURE_SCROLL_UPDATE') { $inertialUpdates++ }
                [void]$pending.Remove($key)
            }
        }
        if ($durations.Count -eq 0) { throw "No completed scroll-update presentation events in $path." }
        $durations.Sort()
        [pscustomobject]@{
            Trace = [IO.Path]::GetFileName($path)
            ScrollUpdates = $durations.Count
            FirstScrollUpdates = $firstUpdates
            InertialScrollUpdates = $inertialUpdates
            Presentation95Ms = $durations[[int][Math]::Ceiling($durations.Count * 0.95) - 1]
            Presentation99Ms = $durations[[int][Math]::Ceiling($durations.Count * 0.99) - 1]
            PresentationMaximumMs = $durations[$durations.Count - 1]
            UpdatesOver100Ms = @($durations.Where({ $_ -gt 100 })).Count
            LastScrollInputTimestampMs = $lastScrollInputTimestamp / 1000.0
            LastScrollPresentationTimestampMs = $lastScrollPresentationTimestamp / 1000.0
        }
    } finally {
        if ($null -ne $document) { $document.Dispose() }
        $stream.Dispose()
    }
}
