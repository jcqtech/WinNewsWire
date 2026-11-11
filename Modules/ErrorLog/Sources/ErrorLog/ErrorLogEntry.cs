namespace WinNewsWire.ErrorLog;

/// <summary>Port of <c>ErrorLogEntry</c>.</summary>
public sealed record ErrorLogEntry(
    long Id,
    DateTime Date,
    string SourceName,
    int SourceID,
    string Operation,
    string FileName,
    string FunctionName,
    int LineNumber,
    string ErrorMessage);
