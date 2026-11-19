Attribute VB_Name = "ReadingList"
Option Explicit

Public Function ExportReadingList(ByVal progID As String, ByVal feedUrl As String, ByVal outputPath As String) As Long
    Dim nnw As Object, account As Object, candidate As Object, feed As Object
    Dim page As Object, titles As Variant, urls As Variant, dates As Variant
    Dim excel As Object, book As Object, sheet As Object
    Dim start As Long, count As Long, row As Long, index As Long
    Dim values() As Variant
    Dim failureNumber As Long, failureMessage As String
    On Error GoTo Failed

    Set nnw = CreateObject(progID)
    For Each account In nnw.Accounts
        For Each candidate In account.AllFeeds
            If candidate.Url = feedUrl Then Set feed = candidate
        Next candidate
    Next account
    If feed Is Nothing Then Err.Raise vbObjectError + 1, , "The requested feed is not subscribed."

    Set excel = CreateObject("Excel.Application")
    excel.Visible = False
    excel.DisplayAlerts = False
    Set book = excel.Workbooks.Add
    Set sheet = book.Worksheets(1)
    sheet.Name = "Reading List"
    sheet.Cells(1, 1).Value = "Title"
    sheet.Cells(1, 2).Value = "URL"
    sheet.Cells(1, 3).Value = "Published"
    row = 2
    count = feed.Articles.Count
    For start = 1 To count Step 128
        Set page = feed.Articles.GetPage(start, 128)
        If page.Count > 0 Then
            titles = page.Get("Title")
            urls = page.Get("Url")
            dates = page.Get("PublishedDate")
            ReDim values(1 To page.Count, 1 To 3)
            For index = 0 To page.Count - 1
                values(index + 1, 1) = titles(index)
                values(index + 1, 2) = urls(index)
                values(index + 1, 3) = dates(index)
            Next index
            sheet.Cells(row, 1).Resize(page.Count, 3).Value = values
            row = row + page.Count
        End If
    Next start
    sheet.Columns("C").NumberFormat = "yyyy-mm-dd hh:mm:ss"
    sheet.Rows(1).Font.Bold = True
    book.SaveAs outputPath, 51
    book.Close False
    excel.Quit
    ExportReadingList = row - 2
    Exit Function

Failed:
    failureNumber = Err.Number
    failureMessage = Err.Description
    On Error Resume Next
    If Not book Is Nothing Then book.Close False
    If Not excel Is Nothing Then excel.Quit
    On Error GoTo 0
    Err.Raise failureNumber, "ExportReadingList", failureMessage
End Function
