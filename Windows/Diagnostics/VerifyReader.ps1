#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('WinNewsWire-reader-' + [Guid]::NewGuid().ToString('N'))),
    [switch] $LiveAtlantic,
    [switch] $HeaderLayout,
    [switch] $ToolbarControls,
    [switch] $SidebarLayout,
    [switch] $Navigation,
    [switch] $Performance,
    [switch] $ArticleScrolling,
    [switch] $LongSession,
    [switch] $RefreshScrolling,
    [switch] $RapidScrolling,
    [switch] $InertialScrolling,
    [switch] $InertiaFocus,
    [switch] $RefreshFocus,
    [switch] $MemoryPressure,
    [switch] $ControlledScrollPages,
    [ValidateSet('HighResolutionWheel', 'NotchedWheel', 'TwoContactTouch')]
    [string] $ScrollInput = 'HighResolutionWheel',
    [switch] $Bugbash,
    [switch] $BrowserTrace,
    [string] $ProfileDirectory,
    [string] $OpmlFixture,
    [string] $ArticleFixture,
    [switch] $CompatibilityRendering,
    [switch] $DirectBrowserScrolling,
    [switch] $UseInstalledRuntime,
    [string] $RuntimeDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not [IO.Path]::IsPathFullyQualified($ResultsDirectory)) {
    throw 'ResultsDirectory must be an absolute path to a new or empty directory.'
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
if ($OpmlFixture -and (-not [IO.Path]::IsPathFullyQualified($OpmlFixture) -or -not (Test-Path -LiteralPath $OpmlFixture -PathType Leaf))) {
    throw 'OpmlFixture must be an existing absolute file path.'
}
if (($RefreshScrolling -or $Bugbash) -and (-not $OpmlFixture -or -not $ProfileDirectory)) {
    throw 'RefreshScrolling and Bugbash require the OPML file and an existing isolated ProfileDirectory.'
}
if ($BrowserTrace -and -not $RefreshScrolling) { throw 'BrowserTrace requires RefreshScrolling.' }
if ($RapidScrolling -and (-not $RefreshScrolling -or $ScrollInput -ne 'HighResolutionWheel')) {
    throw 'RapidScrolling requires RefreshScrolling and runs its own notched and high-resolution workloads.'
}
if ($InertialScrolling -and (-not $RapidScrolling -or $DirectBrowserScrolling)) {
    throw 'InertialScrolling requires RapidScrolling and cannot disable browser scroll animation.'
}
if ($InertiaFocus -and -not $InertialScrolling) { throw 'InertiaFocus requires InertialScrolling.' }
if ($RefreshFocus -and (-not $InertialScrolling -or $InertiaFocus)) {
    throw 'RefreshFocus requires InertialScrolling and cannot be combined with InertiaFocus.'
}
if ($MemoryPressure -and -not $RefreshFocus) { throw 'MemoryPressure requires RefreshFocus.' }
if ($RuntimeDirectory -and (-not [IO.Path]::IsPathFullyQualified($RuntimeDirectory) `
    -or -not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'WinNewsWire.exe') -PathType Leaf))) {
    throw 'RuntimeDirectory must be an existing absolute probe runtime directory.'
}
if ($RuntimeDirectory) {
    $stream = [IO.File]::OpenRead((Join-Path $RuntimeDirectory 'WinNewsWire.dll'))
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $diagnosticBuild = $false
            foreach ($handle in $metadata.TypeDefinitions) {
                $type = $metadata.GetTypeDefinition($handle)
                if ($metadata.GetString($type.Name) -eq 'ParityProbe' `
                    -and $metadata.GetString($type.Namespace) -eq 'WinNewsWire.Diagnostics') {
                    $diagnosticBuild = $true
                    break
                }
            }
            if (-not $diagnosticBuild) { throw 'RuntimeDirectory must contain a diagnostic build, not the normal reader.' }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}
if ($ControlledScrollPages -and -not $RefreshScrolling) { throw 'ControlledScrollPages requires RefreshScrolling.' }
if ($ScrollInput -ne 'HighResolutionWheel' -and -not $RefreshScrolling) { throw 'ScrollInput requires RefreshScrolling.' }
if ($ProfileDirectory -and ((-not $RefreshScrolling -and -not $Bugbash) -or -not [IO.Path]::IsPathFullyQualified($ProfileDirectory) `
    -or -not (Test-Path -LiteralPath (Join-Path $ProfileDirectory 'Accounts') -PathType Container))) {
    throw 'ProfileDirectory must identify an existing isolated profile for RefreshScrolling or Bugbash.'
}
if ($ArticleFixture -and (-not $ArticleScrolling -or -not [IO.Path]::IsPathFullyQualified($ArticleFixture) `
    -or -not (Test-Path -LiteralPath $ArticleFixture -PathType Leaf))) {
    throw 'ArticleFixture requires ArticleScrolling and an existing absolute fixture path.'
}
if ((Test-Path -LiteralPath $ResultsDirectory) -and @(Get-ChildItem -LiteralPath $ResultsDirectory -Force).Count -gt 0) {
    throw 'The results directory is not empty; existing profiles and results will not be overwritten.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
if ($ProfileDirectory) {
    $profileTarget = Join-Path $ResultsDirectory 'ui\profile'
    New-Item -ItemType Directory -Path $profileTarget -Force | Out-Null
    foreach ($name in @('Accounts', 'AppDefaults.json', 'Favicons', 'Caches', 'Themes')) {
        $source = Join-Path $ProfileDirectory $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $profileTarget -Recurse }
    }
}
if ($CompatibilityRendering -or $DirectBrowserScrolling -or $InertialScrolling) {
    $profileTarget = Join-Path $ResultsDirectory 'ui\profile'
    New-Item -ItemType Directory -Path $profileTarget -Force | Out-Null
    $preferencesPath = Join-Path $profileTarget 'AppDefaults.json'
    $preferences = if (Test-Path -LiteralPath $preferencesPath) {
        Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json -AsHashtable
    } else { @{ refreshInterval = 5; refreshIntervalVersion = 1 } }
    if ($CompatibilityRendering) { $preferences['useSoftwareRasterization'] = $true }
    if ($DirectBrowserScrolling) { $preferences['useDirectBrowserScrolling'] = $true }
    if ($InertialScrolling) { $preferences['useDirectBrowserScrolling'] = $false }
    $preferences | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $preferencesPath
}
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$process = $null
Push-Location $repository
try {
    & dotnet test 'Modules\Images\Tests\Images.Tests\Images.Tests.csproj' --no-restore --nologo --verbosity quiet `
        --logger 'trx;LogFileName=images.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw "Images.Tests failed with exit code $LASTEXITCODE." }
    [xml] $trx = Get-Content -LiteralPath (Join-Path $ResultsDirectory 'images.trx') -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters -or [int]$counters.executed -le 0 `
        -or [int]$counters.total -ne [int]$counters.executed -or [int]$counters.passed -ne [int]$counters.executed) {
        throw 'Images.Tests reported no executed tests, skipped tests, or failures.'
    }
    $testResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    foreach ($name in @(
        'RootFaviconIsAvailableEvenWhenHomepageMetadataCannotBeDownloaded',
        'CachedHomepageFailureDoesNotPreventRootFaviconFallback',
        'UnavailableMetadataDoesNotPersistAnEmptyFaviconCandidateList'
    )) {
        if (-not ($testResults | Where-Object { $_.testName.Contains($name) })) {
            throw "The required image regression did not execute: $name"
        }
    }

    $wheelDriver = $null
    if ($RapidScrolling) {
        $driverDirectory = Join-Path $ResultsDirectory 'input-driver'
        & dotnet build 'Tools\WheelInput\WheelInput.csproj' --nologo --verbosity quiet -c Release --output $driverDirectory
        if ($LASTEXITCODE -ne 0) { throw 'The independent wheel input driver did not build.' }
        $wheelDriver = Join-Path $driverDirectory 'WinNewsWire.WheelInput.exe'
    }
    $configuration = if ($RefreshScrolling -or $Bugbash) { 'Debug' } else { 'Probe' }
    $buildArguments = @('build', 'Windows\WinNewsWire.csproj', '--no-restore', '--nologo', '--verbosity', 'quiet',
        '-c', $configuration, '-p:ParityProbe=true', '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64', '-p:PublishReadyToRun=false')
    if ($RefreshScrolling -or $Bugbash) { $buildArguments += ('-p:OutDir=' + (Join-Path $ResultsDirectory 'runtime\')) }
    if ($UseInstalledRuntime) { $buildArguments += '-p:WindowsAppSDKSelfContained=false' }
    if (-not $RuntimeDirectory) {
        & dotnet @buildArguments
        if ($LASTEXITCODE -ne 0) { throw "The reader probe build failed with exit code $LASTEXITCODE." }
    }
    $uiDirectory = Join-Path $ResultsDirectory 'ui'
    $arguments = @('"' + $uiDirectory + '"', '--reader-regressions')
    if ($LiveAtlantic) { $arguments += '--atlantic' }
    if ($HeaderLayout) { $arguments += '--header-layout' }
    if ($ToolbarControls) { $arguments += '--toolbar-controls' }
    if ($SidebarLayout) { $arguments += '--sidebar-layout' }
    if ($Navigation) { $arguments += '--navigation' }
    if ($Performance) { $arguments += '--performance' }
    if ($ArticleScrolling) { $arguments += '--article-scrolling' }
    if ($LongSession) { $arguments += '--long-session' }
    if ($RefreshScrolling) { $arguments += '--refresh-scrolling' }
    if ($RapidScrolling) {
        $arguments += @('--rapid-scrolling', '--wheel-driver-host', ('"' + $wheelDriver + '"'))
    }
    if ($InertialScrolling) {
        $node = Get-Command node.exe -ErrorAction SilentlyContinue
        $nodePath = if ($null -ne $node) { $node.Source } else { Join-Path $env:ProgramFiles 'nodejs\node.exe' }
        if (-not (Test-Path -LiteralPath $nodePath -PathType Leaf)) { throw 'Independent composited-frame capture requires Node.js.' }
        $arguments += @('--inertial-scrolling', '--frame-driver-host', ('"' + $nodePath + '"'))
    }
    if ($InertiaFocus) { $arguments += '--inertia-focus' }
    if ($RefreshFocus) { $arguments += '--refresh-focus' }
    if ($MemoryPressure) { $arguments += '--refresh-memory-pressure' }
    if ($ControlledScrollPages) { $arguments += '--controlled-scroll-pages' }
    if ($ScrollInput -eq 'NotchedWheel') { $arguments += '--notched-wheel' }
    if ($ScrollInput -eq 'TwoContactTouch') { $arguments += '--two-contact-touch' }
    if ($Bugbash) { $arguments += '--bugbash' }
    if ($BrowserTrace) { $arguments += '--browser-trace' }
    if ($OpmlFixture) { $arguments += @('--opml-fixture', ('"' + $OpmlFixture + '"')) }
    if ($ArticleFixture) { $arguments += @('--article-fixture', ('"' + $ArticleFixture + '"')) }
    if ($UseInstalledRuntime) { $arguments += '--installed-runtime' }
    $executable = Join-Path $repository 'Windows\bin\x64\Probe\net8.0-windows10.0.19041.0\win-x64\WinNewsWire.exe'
    if ($RefreshScrolling -or $Bugbash) { $executable = Join-Path $ResultsDirectory 'runtime\WinNewsWire.exe' }
    if ($RuntimeDirectory) { $executable = Join-Path $RuntimeDirectory 'WinNewsWire.exe' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru
    $timeout = if ($Bugbash) { 1800000 } elseif ($OpmlFixture) { 900000 } elseif ($LongSession) { 600000 } else { 180000 }
    if (-not $process.WaitForExit($timeout)) { throw "The isolated reader probe exceeded $($timeout / 60000) minutes." }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "The reader probe failed with exit code $($process.ExitCode). See $uiDirectory." }
    $results = @(Get-Content -LiteralPath (Join-Path $uiDirectory 'results.json') -Raw | ConvertFrom-Json)
    if ($CompatibilityRendering -or $DirectBrowserScrolling) {
        $runtime = Get-Content -LiteralPath (Join-Path $uiDirectory 'runtime.json') -Raw | ConvertFrom-Json
        $preferences = Get-Content -LiteralPath (Join-Path $uiDirectory 'profile\AppDefaults.json') -Raw | ConvertFrom-Json
        if ($CompatibilityRendering -and (-not $runtime.SoftwareRasterizationEnabled -or -not $preferences.useSoftwareRasterization)) {
            throw 'The persisted compatibility-rendering setting was not applied or retained.'
        }
        if ($DirectBrowserScrolling -and (-not $runtime.DirectScrollingEnabled -or -not $preferences.useDirectBrowserScrolling)) {
            throw 'The persisted direct-scrolling setting was not applied or retained.'
        }
    }
    $required = @(
        'Notification registration leaves the UI responsive and drains on close',
        'Compatibility rendering preference, restart boundary and shared browser',
        'Direct scrolling preference and restart boundary',
        'Article renderer, native scripts and five text sizes',
        'Explicit derived-cache cleanup preserves live article identity',
        'Native browser arrow keys and scrollbar dragging',
        'Unrequested data documents cannot replace the article',
        'Trusted page processing with article JavaScript disabled',
        'Decoded feed-image cache, bitmap binding and visible icon pixels',
        'Independent main windows share statuses without sharing selection or undo',
        'Reading unread articles preserves timeline and sidebar rows',
        'Blocked homepage, feed bodies and late favicon without document reload',
        'Window state capture before shutdown',
        'Runtime shutdown drains account work'
    )
    if ($LiveAtlantic) { $required += 'Live Atlantic subscription, every article and actual favicons' }
    if ($Performance) {
        $required += @('Cached icon luminance, contrast and file invalidation', 'Reader responsiveness and bounded header work')
    }
    if ($ArticleScrolling) {
        $required += @('Trusted reader shortcuts, editing and hover with publisher scripts disabled', 'Direct article-body scrolling performance')
    }
    if ($LongSession) { $required += 'Long-session reader retention and callback stability' }
    if ($OpmlFixture) { $required += 'Supplied OPML import and large-profile scrolling' }
    if ($HeaderLayout) {
        $required += @(
            'Pane headers, title-bar input and column presentation',
            'Native column layout, table cells, sorting, reordering and split restoration',
            'Window minimum width follows the article pane across layouts',
            'Responsive menu overflow preserves commands, nested menus and keyboard shortcuts',
            'Shared header customization, overflow and fixed title controls'
            'Theme command insertion and acrylic header overlay'
            'Fixed sidebar commands, contiguous column headers and article read filtering'
            'Native title-bar button motion and compact left inset'
        )
    }
    if ($ToolbarControls) {
        $required += 'Reading-status icons and customization scrollbar gutters'
    }
    if ($SidebarLayout) {
        $required += @(
            'Static sidebar headings, folder icons and fade-down disclosure'
            'Independent sidebar selection, scope filters, rebuild and batch undo'
            'Folder disclosure, static accounts and sidebar filter restoration'
            'Unread count, dot and hidden modes across controls and windows'
            'Native sidebar drop restrictions and mixed-account transfer semantics'
            'Native feed dragging, saved order and folder placement'
            'Delete key is scoped to sidebar rows, not text or article controls'
        )
    }
    if ($Navigation) {
        $required += @(
            'Back across articles, feeds and embedded web pages'
            'New webpage loads never expose the previous document'
            'Original taskbar icon and separate small window icon'
            'Browser preference, background inversion and external article navigation'
        )
    }
    if ($RefreshScrolling) {
        $required = @('Launched Debug app scrolling during full-runtime feed refresh',
            'Window state capture before shutdown', 'Runtime shutdown drains account work')
    }
    if ($Bugbash) {
        $required = @(1..6 | ForEach-Object { "Bugbash reading and browsing round $_" }) + @(
            'Bugbash feed dialogs, rename, delete, undo and redo',
            'Bugbash native OPML pickers and repeated round trips',
            'Bugbash rapid navigation and repeated window lifetime',
            'Window state capture before shutdown', 'Runtime shutdown drains account work')
    }
    foreach ($name in $required) {
        $matching = @($results | Where-Object { $_.Name -eq $name })
        if ($matching.Count -ne 1 -or $matching[0].Passed -ne $true) {
            throw "The required reader regression did not pass: $name. See $uiDirectory."
        }
    }
    if ($results | Where-Object { $_.Passed -ne $true }) { throw "Reader regressions failed. See $uiDirectory." }
    if ($RefreshScrolling -and -not $RapidScrolling) {
        $scrolling = Get-Content -LiteralPath (Join-Path $uiDirectory 'refresh-scrolling.json') -Raw | ConvertFrom-Json
        $expectedInput = switch ($ScrollInput) {
            'HighResolutionWheel' { 'InjectedHighResolutionMouseWheel' }
            'NotchedWheel' { 'InjectedNotchedMouseWheel' }
            'TwoContactTouch' { 'InjectedTwoContactTouchscreen' }
        }
        if ($scrolling.InputKind -ne $expectedInput -or $scrolling.PrecisionTouchpadVerified) {
            throw 'The scroll input was not identified accurately.'
        }
        if ($BrowserTrace) {
            $traces = @('initial-scroll', 'startup-refresh-scroll', 'refresh-scroll', 'video-initial-scroll', 'video-refresh-scroll') |
                ForEach-Object { Join-Path $uiDirectory ($_ + '-browser-trace.json') }
            $presentation = @(& (Join-Path $PSScriptRoot 'MeasureBrowserPresentation.ps1') -TracePath $traces)
            $presentation | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $uiDirectory 'browser-presentation.json')
            $slow = @($presentation | Where-Object {
                $_.ScrollUpdates -lt 100 -or $_.Presentation99Ms -gt 75 -or $_.PresentationMaximumMs -gt 100 -or
                ($ScrollInput -eq 'TwoContactTouch' -and ($_.FirstScrollUpdates -lt 1 -or $_.InertialScrollUpdates -lt 1))
            })
            if ($slow.Count) {
                throw "Actual browser presentation exceeded the interaction budget: $($slow.Trace -join ', '). See browser-presentation.json."
            }
        }
        if ($scrolling.Configuration -ne 'Debug' -or $scrolling.Feeds -ne 50 -or $scrolling.LoadedArticles -lt 12000 `
            -or -not $scrolling.FullRuntimeStarted -or -not $scrolling.IndependentNativeInput) {
            throw 'The full-runtime scrolling workload was not exercised.'
        }
        if ($ScrollInput -eq 'TwoContactTouch') {
            foreach ($sample in $scrolling.Samples) {
                if ($sample.NativeInput.Contacts -ne 2 -or $sample.NativeInput.Gestures -lt 1 `
                    -or $sample.NativeInput.PrecisionTouchpad -or $sample.Browser.maximumTouchContacts -ne 2) {
                    throw "Two trusted native touch contacts were not verified: $($sample.Name)"
                }
            }
        }
        $minimumEvents = if ($ScrollInput -eq 'NotchedWheel') { 100 } else { 1000 }
        foreach ($name in @('normal-runtime-startup-refresh', 'full-refresh-and-reparse', 'video-full-refresh')) {
            $samples = @($scrolling.Samples | Where-Object Name -eq $name)
            if ($samples.Count -ne 1) { throw "Missing concurrent scrolling sample: $name" }
            $sample = $samples[0]
            $refreshEnd = if ($sample.RefreshStillActive) { $sample.InputFinishedMs } else { $sample.RefreshEndMs }
            if ($null -ne $refreshEnd) { $refreshEnd = [Math]::Min($refreshEnd, $sample.InputFinishedMs) }
            if ($null -eq $sample.RefreshStartMs -or $null -eq $refreshEnd `
                -or $sample.NativeInput.EventsSent -lt $minimumEvents -or $sample.Browser.scrollRange -lt 1000 `
                -or $refreshEnd - $sample.RefreshStartMs -lt 1500 -or -not $sample.ViewStillVisible) {
                throw "The native scrolling and active refresh did not overlap: $name"
            }
            if ($sample.UiMaximumGapMs -gt 100 -or $sample.Browser.frame99Ms -gt 34 `
                -or ($ScrollInput -ne 'TwoContactTouch' -and $sample.Browser.wheel99Ms -gt 50) -or $sample.TimelineReplacements -ne 0) {
                throw "Concurrent refresh exceeded the interaction budget: $name. See refresh-scrolling.json."
            }
        }
        foreach ($nativeName in @('sidebar-refresh', 'timeline-refresh')) {
            $native = @($scrolling.NativeUiSamples | Where-Object Name -eq $nativeName)
            if ($native.Count -ne 1 -or $native[0].ScrollRange -le 200 -or $native[0].UiMaximumGapMs -gt 100 `
                -or $native[0].NativeInput.EventsSent -lt $(if ($ScrollInput -eq 'NotchedWheel') { 100 } else { 500 })) {
                throw "Native pane scrolling exceeded the interaction budget: $nativeName."
            }
        }
        if ($DirectBrowserScrolling -and $ScrollInput -ne 'TwoContactTouch') {
            foreach ($sample in $scrolling.Samples) {
                $reversals = @($sample.Browser.reversals)
                if ($reversals.Count -lt 2 -or @($reversals | Where-Object { $null -eq $_.responseMs -or $_.responseMs -gt 75 }).Count) {
                    throw "Wheel-direction response exceeded 75 ms: $($sample.Name). This measures injected wheel input, not a physical touchpad."
                }
            }
        }
    }
    if ($RapidScrolling) {
        $rapid = Get-Content -LiteralPath (Join-Path $uiDirectory 'rapid-scrolling.json') -Raw | ConvertFrom-Json
        $expectedCases = if ($RefreshFocus) { 4 } elseif ($InertiaFocus) { 12 } elseif ($InertialScrolling) { 32 } else { 22 }
        if (-not $rapid.FullRuntimeStarted -or $rapid.PrecisionTouchpadVerified -or $rapid.Samples.Count -ne $expectedCases `
            -or $rapid.InertialScrolling -ne [bool]$InertialScrolling -or $rapid.FocusedInertia -ne [bool]$InertiaFocus `
            -or $rapid.FocusedRefresh -ne [bool]$RefreshFocus) {
            throw 'The rapid-scroll workloads did not all execute in the real application runtime.'
        }
        if ($MemoryPressure -and (-not $rapid.MemoryPressureInjected `
            -or ($rapid.Samples | Where-Object { $_.Workload.Name -eq 'rapid-startup-refresh' }).MemoryCacheCleanups -lt 1)) {
            throw 'The memory-pressure path did not execute during startup refresh.'
        }
        foreach ($pane in @('article-list', 'article-reader', 'embedded-webpage')) {
            $workloads = @('rapid-notched', 'very-rapid-notched', 'rapid-high-resolution', 'rapid-unfocused', 'slow-notched', 'rapid-refresh')
            if ($InertialScrolling) { $workloads += @('coast-single-notch', 'coast-reverse-notched', 'coast-reverse-high-resolution') }
            if ($pane -eq 'article-list') { $workloads += @('rapid-after-touch', 'rapid-column') }
            if ($pane -eq 'article-list' -and $InertialScrolling) { $workloads += 'coast-scrollbar' }
            if ($pane -eq 'article-reader') { $workloads += @('rapid-startup-refresh', 'rapid-column') }
            if ($InertiaFocus) {
                $workloads = @('coast-single-notch', 'coast-reverse-notched', 'coast-reverse-high-resolution')
                if ($pane -eq 'article-list') { $workloads += @('coast-scrollbar', 'coast-column') }
                if ($pane -eq 'article-reader') { $workloads += 'coast-column' }
            }
            if ($RefreshFocus) {
                $workloads = @('rapid-refresh')
                if ($pane -eq 'article-reader') { $workloads += 'rapid-startup-refresh' }
            }
            foreach ($workload in $workloads) {
                $matching = @($rapid.Samples | Where-Object { $_.Pane -eq $pane -and $_.Workload.Name -eq $workload })
                if ($matching.Count -ne 1 -or $matching[0].ColumnLayout -ne ($workload -like '*-column')) {
                    throw "Missing or incorrectly configured rapid-scroll workload: $pane/$workload."
                }
            }
        }
        if (-not $InertiaFocus -and -not $RefreshFocus -and ($null -eq $rapid.TouchTransition -or -not $rapid.TouchTransition.InertiaObserved `
            -or -not $rapid.TouchTransition.EnabledAfterTouch -or -not $rapid.TouchTransition.EnabledAfterWheel `
            -or $rapid.TouchTransition.ScrollRange -lt 200 -or $rapid.TouchTransition.NativeInput.Contacts -ne 2)) {
            throw 'Native touch or wheel momentum regressed.'
        }
        if ($InertialScrolling -and ($null -eq $rapid.BrowserRouting -or -not $rapid.BrowserRouting.NestedTarget `
            -or -not $rapid.BrowserRouting.KeyboardInterruption -or -not $rapid.BrowserRouting.PageOwnedWheel `
            -or -not $rapid.BrowserRouting.ReducedMotion -or -not $rapid.BrowserRouting.RtlHorizontal)) {
            throw 'The browser wheel controller regressed existing input behavior.'
        }
        $rapid.Samples | Select-Object Pane, @{n='Input';e={$_.Workload.Name}}, ScrollRange, ScrollTailMs,
            NativeDispatchMaximumMs, UiMaximumGapMs | Format-Table -AutoSize
        if ($BrowserTrace -and -not $InertialScrolling) {
            $traces = @($rapid.Samples | Where-Object Pane -ne 'article-list' | ForEach-Object {
                $surface = if ($_.Pane -eq 'article-reader') { 'reader' } else { 'webpage' }
                Join-Path $uiDirectory ("rapid-$surface-" + $_.Workload.Name + '-browser-trace.json')
            })
            $presentation = @(& (Join-Path $PSScriptRoot 'MeasureBrowserPresentation.ps1') -TracePath $traces)
            foreach ($sample in @($rapid.Samples | Where-Object Pane -ne 'article-list')) {
                $surface = if ($sample.Pane -eq 'article-reader') { 'reader' } else { 'webpage' }
                $trace = "rapid-$surface-" + $sample.Workload.Name + '-browser-trace.json'
                $match = @($presentation | Where-Object Trace -eq $trace)
                if ($match.Count -ne 1 -or $null -eq $sample.InputStoppedTraceMs -or $sample.TraceClockErrorMs -ge 10) {
                    throw "Missing or unaligned rapid-scroll presentation trace: $trace"
                }
                $tail = [Math]::Max(0.0, $match[0].LastScrollPresentationTimestampMs - $sample.InputStoppedTraceMs)
                $match[0] | Add-Member -NotePropertyName ScrollPresentationTailMs -NotePropertyValue $tail
            }
            $presentation | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $uiDirectory 'browser-presentation.json')
            $slow = @($presentation | Where-Object {
                $_.ScrollUpdates -lt 10 -or $_.Presentation99Ms -gt 75 -or $_.PresentationMaximumMs -gt 100 `
                    -or $_.ScrollPresentationTailMs -gt $(if ($InertialScrolling) { 750 } else { 100 })
            })
            if ($slow.Count) {
                throw "Rapid browser presentation exceeded the input budget: $($slow.Trace -join ', '). See browser-presentation.json."
            }
        }
        foreach ($sample in $rapid.Samples) {
            if ($sample.DriverProcessId -eq $rapid.ProcessId -or $sample.DriverProcessId -le 0 `
                -or @($sample.DriverGcCollections | Where-Object { $_ -ne 0 }).Count) {
                throw "Wheel injection was not independent of managed pauses: $($sample.Pane)/$($sample.Workload.Name)."
            }
            $coastingAllowed = $sample.Pane -eq 'article-list' -or $InertialScrolling
            $minimumEvents = $sample.Workload.DurationMs / ($sample.Workload.IntervalMs * 1.5)
            if ($sample.Workload.BurstMs -gt 0) { $minimumEvents *= $sample.Workload.BurstMs / $sample.Workload.ReverseMs }
            $compositedBrowser = $InertialScrolling -and $sample.Pane -ne 'article-list'
            $interruptingCoast = $sample.Workload.Name -like 'coast-*'
            $minimumRange = if ($sample.Workload.Name -eq 'coast-single-notch') { 30 } else { 200 }
            # Keep the original 75-ms document response and 100-ms output budgets distinct.
            $responseBudget = if ($compositedBrowser -and -not $interruptingCoast) { 100 } else { 75 }
            if ($sample.ScrollRange -lt $minimumRange -or $sample.ScrollTailMs -gt $(if ($coastingAllowed) { $sample.QuietEndedMs - $sample.InputStoppedMs - 100 } else { 100 }) `
                -or (-not $coastingAllowed -and $sample.MotionAfter100Ms -ne 0) -or $sample.Events.Count -lt $minimumEvents `
                -or $sample.Reversals.Count -lt [Math]::Floor($sample.Workload.DurationMs / $sample.Workload.ReverseMs) - 1 `
                -or $sample.QuietEndedMs - $sample.InputStoppedMs -lt 1500 `
                -or @($sample.Reversals | Where-Object { $null -eq $_.ResponseMs -or $_.ResponseMs -gt $responseBudget `
                    -or ($compositedBrowser -and ($null -eq $_.DocumentResponseMs -or $_.DocumentResponseMs -gt 75)) }).Count) {
                throw "Rapid direction changes or stopping exceeded the input budget: $($sample.Pane)/$($sample.Workload.Name). See rapid-scrolling.json."
            }
            if ($sample.Pane -eq 'article-list' -and (-not $sample.NativeInertiaEnabled -or $sample.VisibleRows.Count -lt 10)) {
                throw "Native wheel inertia and visible list motion were not verified: $($sample.Workload.Name)."
            }
            if ($InertialScrolling -and $sample.Pane -ne 'article-list' -and ($sample.CompositedFrames.Count -lt 20 `
                -or $null -eq $sample.FrameCaptureProcessId -or $sample.FrameCaptureProcessId -eq $rapid.ProcessId `
                -or $null -eq $sample.WheelBehavior -or $sample.WheelBehavior.interruptions -lt $sample.Reversals.Count - 1)) {
                throw "Interruptible browser inertia and composited frames were not verified: $($sample.Pane)/$($sample.Workload.Name)."
            }
            if ($InertialScrolling -and $sample.Pane -eq 'article-reader' -and $sample.ReaderScrollMessages -gt 5) {
                throw "Wheel animation flooded the host with intermediate scroll positions: $($sample.Workload.Name)."
            }
            if ($interruptingCoast -and (@($sample.Reversals | Where-Object CoastActiveBefore).Count `
                -lt [Math]::Ceiling($sample.Reversals.Count * 0.75) -or $sample.ScrollTailMs -lt 50)) {
                throw "The reversal did not interrupt active coasting: $($sample.Pane)/$($sample.Workload.Name)."
            }
            if ($sample.Workload.Name -like '*refresh') {
                if ($null -eq $sample.RefreshWorkerProcessID -or $sample.RefreshWorkerProcessID -eq $rapid.ProcessId `
                    -or $null -eq $sample.RefreshWorkerStatistics `
                    -or $sample.RefreshWorkerStatistics.ProcessID -ne $sample.RefreshWorkerProcessID `
                    -or $sample.RefreshWorkerStatistics.ParsedFeeds -lt 25 `
                    -or $sample.RefreshWorkerStatistics.AllocatedBytes -lt 64MB) {
                    throw "Heavy feed processing was not verified in the independent refresh worker: $($sample.Pane)/$($sample.Workload.Name)."
                }
                if ($sample.RefreshWorkerStatistics.ClockFrequency -ne $sample.ClockFrequency `
                    -or @($sample.RefreshWorkerStatistics.FeedCompletedTimestamps | Where-Object {
                        $at = ($_ - $sample.OriginTicks) * 1000.0 / $sample.ClockFrequency
                        $at -ge $sample.RefreshStartMs -and $at -le $sample.InputStoppedMs
                    }).Count -lt 25) {
                    throw "At least 25 feeds were not parsed and committed during active input: $($sample.Pane)/$($sample.Workload.Name)."
                }
                if ($sample.TimelineChanges.Replaced -ne 0 -or $sample.TimelineChanges.Resets -ne 0 `
                    -or $sample.TimelineChanges.CollectionReplaced) {
                    throw "Background refresh replaced existing timeline rows: $($sample.Pane)/$($sample.Workload.Name)."
                }
                if ($null -ne $sample.TimelineChanges.FirstAtMs -and $sample.TimelineChanges.FirstAtMs -lt $sample.InputStoppedMs) {
                    throw "Background refresh changed timeline structure while wheel input was active: $($sample.Pane)/$($sample.Workload.Name)."
                }
                $end = if ($sample.RefreshStillActive) { $sample.InputStoppedMs } else { $sample.RefreshEndMs }
                if ($null -eq $sample.RefreshStartMs -or $null -eq $end `
                    -or [Math]::Min($end, $sample.InputStoppedMs) - $sample.RefreshStartMs -lt 1500) {
                    throw "Rapid scrolling did not overlap enough real refresh work: $($sample.Pane)/$($sample.Workload.Name)."
                }
            }
        }
    }
    $expected = @(Get-Content -LiteralPath (Join-Path $uiDirectory 'expected-windows.json') -Raw | ConvertFrom-Json)
    $defaults = Get-Content -LiteralPath (Join-Path $uiDirectory 'profile\AppDefaults.json') -Raw | ConvertFrom-Json
    $saved = @($defaults.mainWindowStates)
    if ((ConvertTo-Json -InputObject $expected -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $saved -Depth 30 -Compress)) {
        throw 'The reader closed without persisting its expected window state.'
    }
    if ($Bugbash) {
        $journeys = Get-Content -LiteralPath (Join-Path $uiDirectory 'bugbash.json') -Raw | ConvertFrom-Json
        if (-not $journeys.FullRuntimeStarted -or $journeys.FeedVisits -ne 50 -or $journeys.ArticleVisits -lt 200 -or $journeys.BrowserVisits -lt 8) {
            throw 'The full repeated-user workload did not execute.'
        }
        $process.Dispose()
        $process = Start-Process -FilePath $executable -ArgumentList @('"' + $uiDirectory + '"', '--bugbash-restart') -PassThru
        if (-not $process.WaitForExit(180000)) { throw 'The private profile restart exceeded three minutes.' }
        $process.Refresh()
        $restart = @(Get-Content -LiteralPath (Join-Path $uiDirectory 'restart-results.json') -Raw | ConvertFrom-Json)
        if ($process.ExitCode -ne 0 -or $restart.Count -ne 3 -or @($restart | Where-Object { -not $_.Passed }).Count) {
            throw 'The private profile did not preserve its subscriptions, reading state, and shutdown behavior across restart.'
        }
    }
    "Reader gate passed: $($counters.executed) image tests and $($results.Count) UI groups. Results: $ResultsDirectory"
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
    Pop-Location
}
