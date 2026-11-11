using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlNamespaceContextTests
{
    [Fact] public void XmlPrefixAlwaysResolvesToSpecUri()
        => Assert.Equal("http://www.w3.org/XML/1998/namespace", new XmlNamespaceContext().Resolve("xml"));
    [Fact] public void XmlnsPrefixAlwaysResolvesToSpecUri()
        => Assert.Equal("http://www.w3.org/2000/xmlns/", new XmlNamespaceContext().Resolve("xmlns"));
    [Fact] public void BuiltInsArePresentEvenAtDepth()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("dc", XmlNamespace.Uris.DublinCore)]);
        context.PushScope([(null, XmlNamespace.Uris.Atom)]);
        Assert.Equal("http://www.w3.org/XML/1998/namespace", context.Resolve("xml"));
        Assert.Equal("http://www.w3.org/2000/xmlns/", context.Resolve("xmlns"));
    }
    [Fact] public void BuiltInsCannotBeShadowedByBindings()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("xml", "http://evil.example.com/")]);
        Assert.Equal("http://www.w3.org/XML/1998/namespace", context.Resolve("xml"));
    }
    [Fact] public void DefaultNamespaceResolvesWhenBound()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([(null, XmlNamespace.Uris.Atom)]);
        Assert.Equal(XmlNamespace.Uris.Atom, context.Resolve(null));
    }
    [Fact] public void DefaultNamespaceReturnsNilWhenUnbound() => Assert.Null(new XmlNamespaceContext().Resolve(null));
    [Fact] public void PrefixResolvesAfterPush()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("dc", XmlNamespace.Uris.DublinCore)]);
        Assert.Equal(XmlNamespace.Uris.DublinCore, context.Resolve("dc"));
    }
    [Fact] public void UnboundPrefixReturnsNil() => Assert.Null(new XmlNamespaceContext().Resolve("nosuchprefix"));
    [Fact] public void InnerScopeShadowsOuter()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:outer")]);
        Assert.Equal("urn:outer", context.Resolve("x"));
        context.PushScope([("x", "urn:inner")]);
        Assert.Equal("urn:inner", context.Resolve("x"));
    }
    [Fact] public void OuterBindingVisibleFromInnerScopeIfNotShadowed()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:outer")]);
        context.PushScope([("y", "urn:inner")]);
        Assert.Equal("urn:outer", context.Resolve("x"));
        Assert.Equal("urn:inner", context.Resolve("y"));
    }
    [Fact] public void PopRevealsShadowedBinding()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:outer")]);
        context.PushScope([("x", "urn:inner")]);
        context.PopScope();
        Assert.Equal("urn:outer", context.Resolve("x"));
    }
    [Fact] public void PopRemovesInnerBindingEntirely()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:only")]);
        Assert.Equal("urn:only", context.Resolve("x"));
        context.PopScope();
        Assert.Null(context.Resolve("x"));
    }
    [Fact] public void PopOfEmptyScopeIsBalanced()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:outer")]);
        context.PushScope([]);
        Assert.Equal("urn:outer", context.Resolve("x"));
        context.PopScope();
        Assert.Equal("urn:outer", context.Resolve("x"));
        context.PopScope();
        Assert.Null(context.Resolve("x"));
    }
    [Fact] public void PopWithoutPushIsNoop()
    {
        var context = new XmlNamespaceContext();
        context.PopScope();
        Assert.Null(context.Resolve("x"));
        context.PushScope([("x", "urn:x")]);
        Assert.Equal("urn:x", context.Resolve("x"));
    }
    [Fact] public void MultipleBindingsInSameScope()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([(null, XmlNamespace.Uris.Atom), ("dc", XmlNamespace.Uris.DublinCore), ("content", XmlNamespace.Uris.Content)]);
        Assert.Equal(XmlNamespace.Uris.Atom, context.Resolve(null));
        Assert.Equal(XmlNamespace.Uris.DublinCore, context.Resolve("dc"));
        Assert.Equal(XmlNamespace.Uris.Content, context.Resolve("content"));
    }
    [Fact] public void PopSinglePopsAllBindingsFromThatScope()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("a", "urn:a"), ("b", "urn:b"), ("c", "urn:c")]);
        context.PopScope();
        Assert.Null(context.Resolve("a"));
        Assert.Null(context.Resolve("b"));
        Assert.Null(context.Resolve("c"));
    }
    [Fact] public void CanPushAgainAfterPop()
    {
        var context = new XmlNamespaceContext();
        context.PushScope([("x", "urn:first")]);
        context.PopScope();
        context.PushScope([("x", "urn:second")]);
        Assert.Equal("urn:second", context.Resolve("x"));
    }
}
