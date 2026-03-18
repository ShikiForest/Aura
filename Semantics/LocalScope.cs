namespace AuraLang.Semantics;

public sealed class LocalScope
{
    private readonly Dictionary<string, LocalSymbol> _locals = new(StringComparer.Ordinal);

    public LocalScope? Parent { get; }

    public LocalScope(LocalScope? parent) => Parent = parent;

    public bool TryDeclare(LocalSymbol sym)
    {
        if (_locals.ContainsKey(sym.Name)) return false;
        _locals[sym.Name] = sym;
        return true;
    }

    public bool TryLookup(string name, out LocalSymbol sym)
    {
        if (_locals.TryGetValue(name, out sym!)) return true;
        return Parent != null && Parent.TryLookup(name, out sym);
    }
}
