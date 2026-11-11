namespace WinNewsWire.Tree;

/// <summary>Port of <c>Node</c>.</summary>
public sealed class Node
{
    public object RepresentedObject { get; }
    public Node? Parent { get; internal set; }
    public bool IsRoot => Parent is null;
    public bool CanHaveChildNodes { get; set; } = true;
    public bool IsGroupItem { get; set; }
    public List<Node> ChildNodes { get; internal set; } = new();

    public Node(object representedObject, Node? parent = null)
    {
        RepresentedObject = representedObject;
        Parent = parent;
    }

    public static Node GenericRoot() => new(new object()) { CanHaveChildNodes = true, IsGroupItem = true };

    public int Level
    {
        get
        {
            int level = 0;
            var n = Parent;
            while (n is not null) { level++; n = n.Parent; }
            return level;
        }
    }

    public bool HasAncestorIn(IEnumerable<Node> nodes)
    {
        var set = nodes as ICollection<Node> ?? nodes.ToList();
        var n = Parent;
        while (n is not null) { if (set.Contains(n)) return true; n = n.Parent; }
        return false;
    }

    public Node CreateChild(object representedObject)
    {
        var child = new Node(representedObject, this);
        ChildNodes.Add(child);
        return child;
    }
}

public interface ITreeControllerDelegate
{
    IReadOnlyList<Node>? ChildNodesFor(Node node);
}

/// <summary>Port of <c>TreeController</c>.</summary>
public sealed class TreeController
{
    private readonly ITreeControllerDelegate _delegate;
    public Node RootNode { get; }

    public TreeController(ITreeControllerDelegate @delegate) : this(@delegate, Node.GenericRoot()) { }

    public TreeController(ITreeControllerDelegate @delegate, Node rootNode)
    {
        _delegate = @delegate;
        RootNode = rootNode;
        Rebuild();
    }

    public bool Rebuild() => RebuildChildren(RootNode);

    private bool RebuildChildren(Node node)
    {
        bool changed = false;
        if (!node.CanHaveChildNodes) return false;
        var newChildren = _delegate.ChildNodesFor(node) ?? Array.Empty<Node>();
        if (!node.ChildNodes.SequenceEqual(newChildren))
        {
            foreach (var c in newChildren) c.Parent = node;
            node.ChildNodes = newChildren.ToList();
            changed = true;
        }
        foreach (var c in node.ChildNodes) changed |= RebuildChildren(c);
        return changed;
    }

    public void VisitNodes(Action<Node> visit) => Visit(RootNode, visit);
    private static void Visit(Node n, Action<Node> visit) { visit(n); foreach (var c in n.ChildNodes) Visit(c, visit); }

    public Node? NodeInTreeRepresenting(object representedObject)
        => FindRec(new[] { RootNode }, representedObject, recurse: true);

    private static Node? FindRec(IEnumerable<Node> nodes, object repr, bool recurse)
    {
        foreach (var n in nodes)
        {
            if (ReferenceEquals(n.RepresentedObject, repr)) return n;
            if (recurse && n.CanHaveChildNodes)
            {
                var f = FindRec(n.ChildNodes, repr, true);
                if (f is not null) return f;
            }
        }
        return null;
    }

    public IEnumerable<Node> NormalizedSelected(IEnumerable<Node> nodes)
    {
        var list = nodes.ToList();
        return list.Where(n => !n.HasAncestorIn(list));
    }
}
