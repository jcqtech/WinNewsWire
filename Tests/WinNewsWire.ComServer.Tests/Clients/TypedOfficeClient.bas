Attribute VB_Name = "TypedOfficeClient"
Option Explicit

Public Function VerifyTypedAutomation(ByVal progID As String, ByVal expectedID As String) As String
    Dim nnw As WinNewsWire_Automation.IApplication
    Dim articles As WinNewsWire_Automation.IObjectCollection
    Dim article As WinNewsWire_Automation.IArticle
    Dim selected As WinNewsWire_Automation.IArticle
    Set nnw = CreateObject(progID)
    Set selected = nnw.CurrentArticle
    If selected Is Nothing Then Err.Raise vbObjectError + 2, , "No current article."
    If selected.ID <> expectedID Then Err.Raise vbObjectError + 3, , "Wrong current article."
    If VarType(selected.PublishedDate) <> vbDate Then Err.Raise vbObjectError + 4, , "Published date is not a native Date."
    Set articles = nnw.SelectedArticles
    If articles.Count <> 1 Then Err.Raise vbObjectError + 5, , "Wrong selection count."
    For Each article In articles
        article.Starred = True
        If Not article.Starred Then Err.Raise vbObjectError + 6, , "Starred flag did not persist."
    Next article
    VerifyTypedAutomation = "ACTUAL OFFICE TYPED VBA OK"
End Function
