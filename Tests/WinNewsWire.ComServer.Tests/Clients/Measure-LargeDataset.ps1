param(
    [Parameter(Mandatory)][string]$ProgID,
    [Parameter(Mandatory)][int]$ExpectedCount,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$application = New-Object -ComObject $ProgID
$feed = $application.Accounts.Item(1).Feeds.ByName('Fixture Feed')
$measurements = @()
function Measure-Scenario([string]$Name, [scriptblock]$Action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = & $Action
    $watch.Stop()
    [pscustomobject]@{ Scenario = $Name; Milliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2); Result = $result }
}
$measurements += Measure-Scenario 'Count' {
    $count = $feed.Articles.Count
    if ($count -ne $ExpectedCount) { throw "Expected $ExpectedCount articles, got $count" }
    $count
}
$measurements += Measure-Scenario 'Read 200 live titles' {
    $article = $feed.Articles.ByID('publisher-id')
    foreach ($index in 1..200) {
        if ($article.Title -ne 'Fixture Article') { throw 'Incorrect live title' }
    }
    200
}
$measurements += Measure-Scenario 'Project all titles' {
    $titles = @($feed.Articles.Get('Title'))
    if ($titles.Count -ne $ExpectedCount) { throw 'Projection lost articles' }
    $titles.Count
}
$measurements += Measure-Scenario 'Filter all unread' {
    $count = $feed.Articles.Where('Read', $false).Count
    if ($count -ne (1 + [Math]::Floor($ExpectedCount / 2))) { throw "Incorrect unread count: $count" }
    $count
}
$measurements | ConvertTo-Json | Set-Content -LiteralPath $OutputPath
$measurements | Format-Table -AutoSize
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($feed)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($application)
