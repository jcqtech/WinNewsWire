param(
    [string]$ProgID = 'WinNewsWire.Application',
    [Parameter(Mandatory)][string]$FeedUrl,
    [Parameter(Mandatory)][string]$ExpectedArticleID,
    [Parameter(Mandatory)][string]$TypeLibrary,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ReadingListModule
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$access = $null
$excel = $null
$database = Join-Path $OutputDirectory ('ReadingList-' + [Guid]::NewGuid().ToString('N') + '.accdb')
$workbook = Join-Path $OutputDirectory ('ReadingList-' + [Guid]::NewGuid().ToString('N') + '.xlsx')
try {
    $access = New-Object -ComObject Access.Application
    $access.Visible = $false
    $access.NewCurrentDatabase($database)
    if ($null -eq $access.VBE) { throw 'The installed Access host does not expose VBA.' }
    $project = $access.VBE.ActiveVBProject
    if ($null -eq $project) { throw 'No accessible VBA project; Office macro policy was not changed.' }
    [void]$project.References.AddFromFile($TypeLibrary)
    foreach ($path in @($ReadingListModule, (Join-Path $PSScriptRoot 'TypedOfficeClient.bas'))) {
        $component = $project.VBComponents.Import($path)
        $access.DoCmd.Save(5, $component.Name)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($component)
    }
    $typed = $access.Run('VerifyTypedAutomation', $ProgID, $ExpectedArticleID)
    if ($typed -ne 'ACTUAL OFFICE TYPED VBA OK') { throw "Typed VBA verification failed: $typed" }
    $count = $access.Run('ExportReadingList', $ProgID, $FeedUrl, $workbook)
    if ($count -lt 1 -or !(Test-Path -LiteralPath $workbook)) { throw 'VBA did not export a reading-list workbook.' }
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Open($workbook, 0, $true)
    try {
        $sheet = $book.Worksheets.Item(1)
        $rows = $sheet.UsedRange.Rows.Count - 1
        if ($rows -ne $count) { throw "Saved workbook lost rows: expected $count, got $rows" }
        if ($sheet.Cells.Item(1, 1).Value2 -ne 'Title' -or !$sheet.Cells.Item(2, 1).Value2) {
            throw 'The saved workbook does not contain exported titles.'
        }
        $result = [pscustomobject]@{
            Host = 'Microsoft Access VBA'
            Version = $access.Version
            TypedResult = $typed
            ExportedRows = $count
            WorkbookRows = $rows
            Workbook = $workbook
            NativeDate = $true
            ExcelVersion = $excel.Version
            ExcelVBEAvailable = ($null -ne $excel.VBE)
            MacroSecurityChanged = $false
        }
        $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'office-results.json')
        $result
    }
    finally { $book.Close($false) }
}
finally {
    if ($excel) {
        $excel.Quit()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel)
    }
    if ($access) {
        $access.CloseCurrentDatabase()
        $access.Quit(2)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($access)
    }
}
