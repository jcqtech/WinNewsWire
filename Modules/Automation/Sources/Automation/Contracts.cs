using System.Runtime.InteropServices;

[assembly: ComVisible(false)]
[assembly: Guid("163CB58F-AB4C-43E3-9458-89744B418B14")]
[assembly: TypeLibVersion(1, 0)]

namespace WinNewsWire.Automation;

public static class AutomationIdentity
{
    public const string ClassID = "4B97D46F-41B4-4689-9466-04B87EC7A2B0";
    public const string ProgID = "WinNewsWire.Application";
    public const string TypeLibraryID = "163CB58F-AB4C-43E3-9458-89744B418B14";
}

[ComVisible(true), Guid("89804C8C-193C-4A22-9595-B23C0719F241"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IApplication
{
    [DispId(1)] string Name { get; }
    [DispId(2)] string Version { get; }
    [DispId(3)] object? CurrentArticle { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(4)] object SelectedArticles { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(5)] object SelectedFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(6)] object Accounts { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(7)] object Articles { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(8)] object Feeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(9)] object WebFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(10)] void OpenLocation(string url);
    [DispId(11)]
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Make(string newKind, [Optional] object at, [Optional] object withData, [Optional] object withProperties);
    // VARIANT inputs preserve VBScript's by-reference object arguments.
    [DispId(12)] void Delete(object target);
    [DispId(13)] bool Exists(object? target);
    [DispId(14)] int Count(object collection);
    [DispId(15)]
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object CreateFeed(string url, string name = "");
    [DispId(16)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object CreateFolder(string name);
}

[ComVisible(true), Guid("EC7DCDA1-4D0A-4378-A50C-699FFB452382"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IAccount
{
    [DispId(1)] string Name { get; }
    [DispId(2)] string ID { get; }
    [DispId(3)] string AccountType { get; }
    [DispId(4)] bool Active { get; set; }
    [DispId(5)] object AllFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(6)] string OpmlRepresentation { get; }
    [DispId(7)] object Feeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(8)] object Folders { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(9)] object AllWebFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(10)] object WebFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(11)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object CreateFeed(string url, string name = "");
    [DispId(12)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object CreateFolder(string name);
    [DispId(13)] bool Exists { get; }
}

[ComVisible(true), Guid("F5D8412C-6F1B-44E4-BE19-751B303A0C32"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFolder
{
    [DispId(1)] string Name { get; }
    [DispId(2)] int ID { get; }
    [DispId(3)] string OpmlRepresentation { get; }
    [DispId(4)] object Feeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(5)] object Articles { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(6)] object WebFeeds { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(7)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object CreateFeed(string url, string name = "");
    [DispId(8)] void Delete();
    [DispId(9)] bool Exists { get; }
}

[ComVisible(true), Guid("86D94E30-36A6-4666-85D8-B912CC59FE75"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFeed
{
    [DispId(1)] string Name { get; }
    [DispId(2)] string ID { get; }
    [DispId(3)] string Url { get; }
    [DispId(4)] string HomePageUrl { get; }
    [DispId(5)] string IconUrl { get; }
    [DispId(6)] string FaviconUrl { get; }
    [DispId(7)] string OpmlRepresentation { get; }
    [DispId(8)] object Authors { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(9)] object Articles { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(10)] void Delete();
    [DispId(11)] bool Exists { get; }
}

[ComVisible(true), Guid("8A33D395-5F62-4439-9869-A52FB74A2426"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IArticle
{
    [DispId(1)] string ID { get; }
    [DispId(2)] string Title { get; }
    [DispId(3)] string? Url { get; }
    [DispId(4)] string? ExternalUrl { get; }
    [DispId(5)] string? Permalink { get; }
    [DispId(6)] string Contents { get; }
    [DispId(7)] string Html { get; }
    [DispId(8)] string Summary { get; }
    [DispId(9)] object? PublishedDate { get; }
    [DispId(10)] object ArrivedDate { get; }
    [DispId(11)] object? ModifiedDate { get; }
    [DispId(12)] bool Read { get; set; }
    [DispId(13)] bool Starred { get; set; }
    [DispId(14)] bool Deleted { get; }
    [DispId(15)] string ImageUrl { get; }
    [DispId(16)] object? Feed { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(17)] object Authors { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(18)] bool Exists { get; }
    [DispId(19)] object? DatePublished { get; }
    [DispId(20)] object DateArrived { get; }
    [DispId(21)] object? DateModified { get; }
}

[ComVisible(true), Guid("E85FF4FD-CA3E-48E3-84D0-BF5B7DF58D08"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IAuthor
{
    [DispId(1)] string Name { get; }
    [DispId(2)] string ID { get; }
    [DispId(3)] string Url { get; }
    [DispId(4)] string AvatarUrl { get; }
    [DispId(5)] string EmailAddress { get; }
    [DispId(6)] string Email { get; }
    [DispId(7)] bool Exists { get; }
}

[ComVisible(true), Guid("BB8A1DF7-A44C-43E3-8437-4B722A704902"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IObjectCollection
{
    [DispId(0)] object this[object indexOrName] { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(1)] int Count { get; }
    [DispId(2)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object ByID(object id);
    [DispId(3)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object ByName(string name);
    [DispId(4)] bool Exists(object indexOrName);
    [DispId(5)] object?[] Get(string property);
    [DispId(6)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object Where(string property, object value);
    [DispId(7)] void Delete();
    [DispId(8)]
    [return: MarshalAs(UnmanagedType.IDispatch)] object GetPage(int start, int count);
    [DispId(-4)]
    object _NewEnum { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
}
