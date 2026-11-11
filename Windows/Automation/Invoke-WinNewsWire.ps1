#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Request,
    [string]$DataDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WinNewsWire'),
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 300,
    [switch]$RawResponse
)

$ErrorActionPreference = 'Stop'
$null = $Request | ConvertFrom-Json -Depth 24
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid = $identity.User.Value } finally { $identity.Dispose() }
if ([string]::IsNullOrEmpty($sid)) { throw 'The current Windows user could not be identified.' }
$path = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($sid + "`n" + $path)))
$pipeName = 'WinNewsWire.Automation.' + $hash.Substring(0, 24)
$options = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, $options)

function Read-Exact([byte[]]$Buffer) {
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $reading = $pipe.ReadAsync($Buffer, $offset, $Buffer.Length - $offset)
        if (-not $reading.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) { throw 'The automation request timed out.' }
        $count = $reading.GetAwaiter().GetResult()
        if ($count -eq 0) { throw 'The application closed the automation connection.' }
        $offset += $count
    }
}

try {
    try { $pipe.Connect(5000) }
    catch { throw 'Start WinNewsWire and enable Allow local automation in Preferences before using this command.' }
    $payload = $utf8.GetBytes($Request)
    if ($payload.Length -eq 0 -or $payload.Length -gt 1MB) { throw 'Automation requests are limited to 1 MiB.' }
    $length = [BitConverter]::GetBytes([int]$payload.Length)
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($length) }
    $pipe.Write($length, 0, 4)
    $pipe.Write($payload, 0, $payload.Length)
    $pipe.Flush()
    $header = [byte[]]::new(4)
    Read-Exact $header
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($header) }
    $responseLength = [BitConverter]::ToInt32($header, 0)
    if ($responseLength -le 0 -or $responseLength -gt 64MB) { throw 'The automation response length is invalid.' }
    $responseBytes = [byte[]]::new($responseLength)
    Read-Exact $responseBytes
    $json = $utf8.GetString($responseBytes)
    $response = $json | ConvertFrom-Json -Depth 24
    if (-not $response.success) { throw ($response.error.code + ': ' + $response.error.message) }
    if ($RawResponse) { $json }
    elseif ($response.hasValue) { Write-Output -NoEnumerate $response.value }
} finally { $pipe.Dispose() }
