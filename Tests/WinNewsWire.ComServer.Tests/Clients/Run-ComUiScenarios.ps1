param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$FeedServerUrl,
    [ValidateSet('First', 'Restart')][string]$Phase = 'First',
    [string]$ProgID = 'WinNewsWire.Application'
)
$ErrorActionPreference = 'Stop'
function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Send-UiCommand([string]$Action, [hashtable]$Properties = @{}) {
    $id = [Guid]::NewGuid().ToString('N')
    $request = @{ ID = $id; Action = $Action }
    foreach ($key in $Properties.Keys) { $request[$key] = $Properties[$key] }
    $path = Join-Path $OutputDirectory 'com-command.json'
    $request | ConvertTo-Json | Set-Content -LiteralPath ($path + '.tmp')
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $replyPath = Join-Path $OutputDirectory 'com-reply.json'
        if (!(Test-Path -LiteralPath $replyPath)) { continue }
        $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json
        if ($reply.ID -ne $id) { continue }
        if ($reply.Error) { throw $reply.Error }
        return $reply.Details
    }
    throw "UI command timed out: $Action"
}
$objects = [Collections.Generic.List[object]]::new()
foreach ($file in @('com-ready.json', 'com-command.json', 'com-reply.json')) {
    $path = Join-Path $OutputDirectory $file
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
}
$nnw = New-Object -ComObject $ProgID
$objects.Add($nnw)
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0
    while ($true) {
        try { $name = $nnw.Name; break }
        catch {
            $attempts++
            if ($attempts -ge 3) { throw }
            Start-Sleep -Seconds 2
        }
    }
    $watch.Stop()
    Require ($name -eq 'WinNewsWire') 'COM activation returned the wrong application.'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path (Join-Path $OutputDirectory 'com-ready.json'))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The packaged UI did not report readiness.' }
        Start-Sleep -Milliseconds 100
    }
    $ready = Get-Content (Join-Path $OutputDirectory 'com-ready.json') -Raw | ConvertFrom-Json
    Require ($ready.Package -like 'JCQTechnology.WinNewsWire.ComScenarios_*') 'The COM server did not launch the isolated installed package.'
    Require ($ready.Directory -eq (Join-Path $OutputDirectory 'profile')) 'The installed app used the wrong data directory.'
    $account = $nnw.Accounts.Item(1)
    $objects.Add($account)
    if ($Phase -eq 'First') {
        $folder = $account.CreateFolder('Scenario Reading List')
        $objects.Add($folder)
        $feed = $folder.CreateFeed($FeedServerUrl + '/rss')
        $objects.Add($feed)
        Require ($feed.Articles.Count -eq 320) 'The real local provider did not download all 320 sample articles.'
        $selected = Send-UiCommand 'select' @{ FeedUrl = $feed.Url; Ordinal = 1 }
        Require ($nnw.CurrentArticle.ID -eq $selected.ID) 'COM current article does not follow the real reader selection.'
        Require ($nnw.SelectedArticles.Count -eq 1 -and $nnw.SelectedFeeds.Count -eq 1) 'The real UI selection was not exposed.'
        $article = $nnw.CurrentArticle
        $objects.Add($article)
        $article.Starred = $true
        $article.Read = $false
        $state = Send-UiCommand 'state'
        Require ($state.IsStarred -and !$state.IsRead) 'COM flag changes did not reach the live timeline row.'
        $second = Send-UiCommand 'secondWindow' @{ FeedUrl = $feed.Url; Ordinal = 2 }
        Require ($second.Windows -eq 2 -and $nnw.CurrentArticle.ID -eq $second.ID) 'COM did not follow the active second window.'
        $failed = $false
        try { [void]$folder.CreateFeed($FeedServerUrl + '/broken') }
        catch { $failed = $true }
        Require $failed 'A deliberately failing HTTP subscription reported success.'
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'feed-version.txt') -Value '2'
        [void](Send-UiCommand 'refresh' @{ FeedUrl = $feed.Url })
        Require ($feed.Articles.Count -eq 321) 'Real provider refresh did not import the new sample article.'
        Require ($feed.Articles.ByID('sample-0').Html -like '*revision 2*') 'Real provider refresh did not update existing content.'
        Require $article.Starred 'Real provider refresh discarded a reading flag.'
        $publicFeed = $account.CreateFeed('https://feeds.bbci.co.uk/news/rss.xml')
        $objects.Add($publicFeed)
        Require ($publicFeed.Articles.Count -gt 0) 'The real public RSS subscription returned no articles.'
        $typeLibrary = Join-Path $ready.ExecutableDirectory 'Automation\WinNewsWire.Automation.tlb'
        $module = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\Windows\Automation\Samples\ReadingList.bas'))
        & (Join-Path $PSScriptRoot 'Run-OfficeScenarios.ps1') -ProgID $ProgID -FeedUrl $feed.Url `
            -ExpectedArticleID $second.ID -TypeLibrary $typeLibrary -OutputDirectory $OutputDirectory -ReadingListModule $module
        $state = Send-UiCommand 'state'
        Require ($state.ID -eq $second.ID -and $state.IsStarred) 'Actual VBA did not update the live selected article.'
        $result = [pscustomobject]@{
            Phase = $Phase; Package = $ready.Package; ProcessID = $ready.ProcessID
            ActivationMilliseconds = $watch.Elapsed.TotalMilliseconds; StartupRetries = $attempts
            FeedUrl = $feed.Url; FolderID = $folder.ID; PersistentArticleID = $article.ID
            ArticleCount = $feed.Articles.Count; PublicFeedArticles = $publicFeed.Articles.Count
            ActiveWindowSelection = $true; ComFlagsReachedUi = $true; VbaFlagsReachedUi = $true
            ProviderRefresh = $true; ProviderFailure = $true
        }
        $result | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'scenario-first.json')
        $result
    }
    else {
        $previous = Get-Content (Join-Path $OutputDirectory 'scenario-first.json') -Raw | ConvertFrom-Json
        $folder = $account.Folders.ByID($previous.FolderID)
        $objects.Add($folder)
        $feed = $folder.Feeds.Item(1)
        $objects.Add($feed)
        Require ($feed.Url -eq $previous.FeedUrl -and $feed.Articles.Count -eq 321) 'Packaged restart lost the subscription or articles.'
        $article = $feed.Articles.ByID($previous.PersistentArticleID)
        $objects.Add($article)
        Require $article.Starred 'Packaged restart lost the starred status.'
        $result = [pscustomobject]@{
            Phase = $Phase; Package = $ready.Package; ProcessID = $ready.ProcessID
            PersistedArticles = $feed.Articles.Count; PersistedStar = $article.Starred
        }
        $folder.Delete()
        Require (!$folder.Exists) 'Deleting the sample folder did not persist.'
        $result | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'scenario-restart.json')
        $result
    }
    [void](Send-UiCommand 'stop')
}
finally {
    foreach ($value in $objects.ToArray()) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
