// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis.Symbols;
using Midl.Syntax;

namespace Midl.Analysis;

/// <summary>
/// Namespace-aware name resolution with scoped lookup.
/// </summary>
public class SymbolTable
{
    private readonly NamespaceSymbol _globalNamespace;
    private readonly Stack<ISymbol> _scopeStack = new();
    private readonly Dictionary<string, ISymbol> _allDefinedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typeAliases = new(StringComparer.Ordinal);
    private readonly List<LazyWinMdProvider> _lazyProviders = new();
    private List<string>? _refDirectories;

    // Track which directory+winmd combinations we've already tried loading
    private readonly HashSet<string> _loadedWinMds = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ShorthandMappings= new(StringComparer.Ordinal)
    {
        ["IVector"] = "Windows.Foundation.Collections.IVector",
        ["IVectorView"] = "Windows.Foundation.Collections.IVectorView",
        ["IMap"] = "Windows.Foundation.Collections.IMap",
        ["IMapView"] = "Windows.Foundation.Collections.IMapView",
        ["IIterable"] = "Windows.Foundation.Collections.IIterable",
        ["IKeyValuePair"] = "Windows.Foundation.Collections.IKeyValuePair",
        ["IObservableVector"] = "Windows.Foundation.Collections.IObservableVector",
        ["IObservableMap"] = "Windows.Foundation.Collections.IObservableMap",
        ["IAsyncOperation"] = "Windows.Foundation.IAsyncOperation",
        ["IAsyncAction"] = "Windows.Foundation.IAsyncAction",
        ["IReference"] = "Windows.Foundation.IReference",
        ["EventHandler"] = "Windows.Foundation.EventHandler",
        ["TypedEventHandler"] = "Windows.Foundation.TypedEventHandler",
    };

    public SymbolTable(NamespaceSymbol globalNamespace)
    {
        _globalNamespace = globalNamespace;
        _scopeStack.Push(globalNamespace);
    }

    public ISymbol CurrentScope => _scopeStack.Peek();

    /// <summary>
    /// Returns all candidate simple type names for "did you mean" suggestions.
    /// Includes shorthand mapping keys and simple names of all defined types.
    /// </summary>
    public IEnumerable<string> GetAllCandidateTypeNames()
    {
        foreach (var key in ShorthandMappings.Keys)
            yield return key;

        foreach (var kvp in _allDefinedTypes)
        {
            // Return simple name (last segment after '.') for defined types
            var fullName = kvp.Key;
            var dotIdx = fullName.LastIndexOf('.');
            yield return dotIdx >= 0 ? fullName.Substring(dotIdx + 1) : fullName;
        }

        foreach (var alias in _typeAliases.Keys)
            yield return alias;
    }

    /// <summary>
    /// Returns all fully-qualified type names from lazy WinMD providers
    /// (for "did you mean" suggestions).
    /// </summary>
    public IEnumerable<string> GetAllLazyTypeNames()
    {
        foreach (var provider in _lazyProviders)
            foreach (var name in provider.GetAllTypeNames())
                yield return name;
    }

    /// <summary>
    /// Returns all fully-qualified type names from the defined types table
    /// (for "did you mean" suggestions with qualified names).
    /// </summary>
    public IEnumerable<string> GetAllDefinedTypeFullNames() => _allDefinedTypes.Keys;

    /// <summary>Look up a type by its fully qualified name from the defined types dictionary.</summary>
    public ISymbol? LookupDefinedType(string fullName) =>
        _allDefinedTypes.TryGetValue(fullName, out var symbol) ? symbol : null;

    public void AddLazyProvider(LazyWinMdProvider provider)
    {
        _lazyProviders.Add(provider);
        if (provider is { } p && p.SourcePath != null)
            _loadedWinMds.Add(p.SourcePath);
    }

    public void SetRefDirectories(IReadOnlyList<string>? dirs)
    {
        _refDirectories = dirs?.Count > 0 ? new List<string>(dirs) : null;
    }

    public void EnterScope(ISymbol scope)
    {
        _scopeStack.Push(scope);
    }

    public void ExitScope()
    {
        if (_scopeStack.Count > 1)
            _scopeStack.Pop();
    }

    public void Define(ISymbol symbol)
    {
        // Track fully-qualified types for fast lookup
        if (symbol.Kind is not (SymbolKind.Namespace or SymbolKind.Parameter
            or SymbolKind.Method or SymbolKind.Property or SymbolKind.Event
            or SymbolKind.Constructor or SymbolKind.Field or SymbolKind.EnumMember))
        {
            _allDefinedTypes[symbol.FullName] = symbol;
        }
    }

    /// <summary>
    /// Bulk-import external symbols (e.g., from .winmd references) into the symbol table.
    /// Creates namespace hierarchy and inserts each type into the appropriate namespace.
    /// </summary>
    public void ImportExternalTypes(IReadOnlyList<ISymbol> externalTypes)
    {
        foreach (var type in externalTypes)
        {
            var fullName = type.FullName;
            var lastDot = fullName.LastIndexOf('.');
            var nsName = lastDot >= 0 ? fullName[..lastDot] : "";

            // Create namespace hierarchy from fully-qualified name
            var ns = GetOrCreateNamespaceHierarchy(nsName);

            // Re-parent the symbol into the real namespace
            switch (type)
            {
                case InterfaceSymbol iface:
                    var newIface = new InterfaceSymbol(iface.Name, ns)
                    {
                        Uuid = iface.Uuid,
                        IsExternal = true,
                        SourceWinMdPath = iface.SourceWinMdPath,
                    };
                    foreach (var m in iface.Members)
                        newIface.AddMember(m);
                    foreach (var r in iface.RequiredInterfaces)
                        newIface.RequiredInterfaces.Add(r);
                    ns.AddMember(newIface);
                    _allDefinedTypes[newIface.FullName] = newIface;
                    break;

                case EnumSymbol e:
                    var newEnum = new EnumSymbol(e.Name, ns)
                    {
                        IsFlags = e.IsFlags,
                        UnderlyingType = e.UnderlyingType,
                        IsExternal = true,
                        SourceWinMdPath = e.SourceWinMdPath,
                    };
                    foreach (var m in e.EnumMembers)
                        newEnum.AddMember(m);
                    ns.AddMember(newEnum);
                    _allDefinedTypes[newEnum.FullName] = newEnum;
                    break;

                case StructSymbol s:
                    var newStruct = new StructSymbol(s.Name, ns)
                    {
                        IsExternal = true,
                        SourceWinMdPath = s.SourceWinMdPath,
                    };
                    foreach (var f in s.Fields)
                        newStruct.AddField(f);
                    ns.AddMember(newStruct);
                    _allDefinedTypes[newStruct.FullName] = newStruct;
                    break;

                case DelegateSymbol d:
                    var newDel = new DelegateSymbol(d.Name, ns)
                    {
                        ReturnType = d.ReturnType,
                        IsExternal = true,
                        SourceWinMdPath = d.SourceWinMdPath,
                    };
                    newDel.Parameters.AddRange(d.Parameters);
                    ns.AddMember(newDel);
                    _allDefinedTypes[newDel.FullName] = newDel;
                    break;

                case RuntimeClassSymbol rc:
                    var newRc = new RuntimeClassSymbol(rc.Name, ns)
                    {
                        Uuid = rc.Uuid,
                        BaseClass = rc.BaseClass,
                        IsStatic = rc.IsStatic,
                        IsExternal = true,
                        SourceWinMdPath = rc.SourceWinMdPath,
                    };
                    foreach (var i in rc.Interfaces)
                        newRc.Interfaces.Add(i);
                    foreach (var m in rc.Members)
                        newRc.AddMember(m);
                    ns.AddMember(newRc);
                    _allDefinedTypes[newRc.FullName] = newRc;
                    break;

                case ApiContractSymbol ac:
                    var newAc = new ApiContractSymbol(ac.Name, ns)
                    {
                        Version = ac.Version,
                        IsExternal = true,
                        SourceWinMdPath = ac.SourceWinMdPath,
                    };
                    ns.AddMember(newAc);
                    _allDefinedTypes[newAc.FullName] = newAc;
                    break;
            }
        }
    }

    private NamespaceSymbol GetOrCreateNamespaceHierarchy(string dottedName)
    {
        if (string.IsNullOrEmpty(dottedName)) return _globalNamespace;

        var parts = dottedName.Split('.');
        var current = _globalNamespace;
        foreach (var part in parts)
        {
            current = current.GetOrCreateChildNamespace(part);
        }
        return current;
    }

    /// <summary>
    /// Scoped lookup: search current scope, then parent scopes.
    /// </summary>
    public ISymbol? Lookup(string name)
    {
        foreach (var scope in _scopeStack)
        {
            var result = LookupInScope(scope, name);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>
    /// Qualified lookup: walk namespace hierarchy from global root.
    /// Falls back to lazy WinMD providers if not found locally.
    /// </summary>
    public ISymbol? LookupQualified(string dottedName)
    {
        if (_allDefinedTypes.TryGetValue(dottedName, out var cached))
            return cached is InterfaceSymbol { SourceWinMdPath: BuiltInTypes.BuiltInSource } placeholder
                ? PreferReferencedDefinition(dottedName, placeholder)
                : cached;

        NamespaceSymbol current = _globalNamespace;
        int start = 0;
        while (start <= dottedName.Length)
        {
            int dot = dottedName.IndexOf('.', start);
            bool isLast = dot < 0;
            string part = isLast ? dottedName.Substring(start) : dottedName.Substring(start, dot - start);

            if (current.ChildNamespaces.TryGetValue(part, out var childNs))
            {
                if (isLast) return childNs;
                current = childNs;
            }
            else
            {
                var member = FindMemberByName(current, part);
                if (member != null && isLast) return member;
                return TryResolveLazy(dottedName);
            }

            if (isLast) break;
            start = dot + 1;
        }

        return current;
    }

    /// <summary>
    /// BuiltInTypes registers member-less interface placeholders (IClosable, IStringable, ...) so IDL
    /// compiles without references. When a referenced WinMD defines the interface, use its real
    /// definition: a runtime class implementing it (directly or through a required interface, such as
    /// IRandomAccessStream requiring IClosable) needs its members and MethodImpls, as midl.exe emits them.
    /// </summary>
    private ISymbol PreferReferencedDefinition(string fullName, InterfaceSymbol placeholder)
    {
        if ((_lazyProviders.Count == 0 && _refDirectories == null) || !_placeholderLookups.Add(fullName))
            return placeholder;

        _allDefinedTypes.Remove(fullName);
        if (TryResolveLazy(fullName) is InterfaceSymbol real)
            return real;
        _allDefinedTypes[fullName] = placeholder;
        return placeholder;
    }

    // Built-in placeholders already checked against the referenced WinMDs.
    private readonly HashSet<string> _placeholderLookups = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a type alias (e.g., HSTRING → String, DWORD → UInt32).
    /// When looking up the alias name, the target type name is resolved instead.
    /// </summary>
    public void RegisterAlias(string alias, string targetType)
    {
        _typeAliases[alias] = targetType;
    }

    /// <summary>
    /// Try to resolve a name through registered type aliases.
    /// Returns the target type name if the name is a registered alias, null otherwise.
    /// </summary>
    public string? ResolveAlias(string name)
    {
        return _typeAliases.TryGetValue(name, out var target) ? target : null;
    }

    /// <summary>
    /// Shorthand resolution: try normal lookup, then shorthand mappings,
    /// then fall back to the global defined types table (for built-in types).
    /// </summary>
    public ISymbol? LookupWithShorthand(string name)
    {
        var result = Lookup(name);
        if (result != null) return result;

        if (ShorthandMappings.TryGetValue(name, out var qualified))
            return LookupQualified(qualified);

        // Fallback: check _allDefinedTypes directly (finds built-in types
        // that use detached namespaces and aren't in the real namespace tree)
        if (_allDefinedTypes.TryGetValue(name, out var directMatch))
            return directMatch;

        return null;
    }

    private static ISymbol? LookupInScope(ISymbol scope, string name)
    {
        return scope switch
        {
            NamespaceSymbol ns => ns.ChildNamespaces.TryGetValue(name, out var child)
                ? child
                : FindMemberByName(ns, name),
            RuntimeClassSymbol rc => FindMemberByNameLinear(rc.Members, name),
            InterfaceSymbol iface => FindMemberByNameLinear(iface.Members, name),
            _ => null,
        };
    }

    private static ISymbol? FindMemberByNameLinear(IReadOnlyList<ISymbol> members, string name)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].Name == name) return members[i];
        }
        return null;
    }

    private static ISymbol? FindMemberByName(NamespaceSymbol ns, string name)
    {
        foreach (var m in ns.Members)
        {
            if (m.Name == name && m.Kind != SymbolKind.Namespace)
                return m;
        }
        return null;
    }

    /// <summary>
    /// Try to resolve a fully-qualified type name from lazy WinMD providers.
    /// On success, imports the type into the namespace tree and returns it.
    /// </summary>
    internal ISymbol? TryResolveLazy(string qualifiedName)
    {
        if (_lazyProviders.Count == 0 && _refDirectories == null) return null;

        foreach (var provider in _lazyProviders)
        {
            var symbol = provider.TryResolve(qualifiedName);
            if (symbol != null)
            {
                var imported = ImportSingleType(symbol);
                return imported ?? symbol;
            }
        }

        // Try namespace-based directory resolution: for A.B.C.TypeName,
        // look in ref directories for A.B.C.winmd, A.B.winmd, A.winmd
        if (_refDirectories != null)
        {
            var resolved = TryResolveFromRefDirectories(qualifiedName);
            if (resolved != null) return resolved;
        }

        return null;
    }

    private ISymbol? TryResolveFromRefDirectories(string qualifiedName)
    {
        // Extract namespace from qualified name (everything before the last dot)
        var lastDot = qualifiedName.LastIndexOf('.');
        if (lastDot < 0) return null;
        var ns = qualifiedName[..lastDot];

        // Walk up the namespace chain: A.B.C → A.B → A
        while (!string.IsNullOrEmpty(ns))
        {
            foreach (var dir in _refDirectories!)
            {
                var candidate = Path.Combine(dir, ns + ".winmd");
                if (!_loadedWinMds.Contains(candidate) && File.Exists(candidate))
                {
                    MidlTrace.Binder($"RefDir: loading {candidate} for {qualifiedName}");
                    _loadedWinMds.Add(candidate);
                    try
                    {
                        var provider = new LazyWinMdProvider(candidate);
                        _lazyProviders.Add(provider);

                        var symbol = provider.TryResolve(qualifiedName);
                        if (symbol != null)
                        {
                            var imported = ImportSingleType(symbol);
                            return imported ?? symbol;
                        }
                    }
                    catch
                    {
                        // Skip unreadable WinMD files
                    }
                }
            }

            var dot = ns.LastIndexOf('.');
            ns = dot >= 0 ? ns[..dot] : null;
        }

        return null;
    }

    /// <summary>
    /// Try to resolve a simple (unqualified) name by walking up the namespace chain
    /// and checking lazy providers with each possible qualified name.
    /// </summary>
    internal ISymbol? TryResolveLazyBySimpleName(string simpleName, NamespaceSymbol containingNamespace)
    {
        if (_lazyProviders.Count == 0 && _refDirectories == null) return null;

        NamespaceSymbol? current = containingNamespace;
        while (current != null)
        {
            var fullName = string.IsNullOrEmpty(current.FullName)
                ? simpleName
                : string.Concat(current.FullName, ".", simpleName);

            var result = TryResolveLazy(fullName);
            if (result != null) return result;

            current = current.ContainingSymbol as NamespaceSymbol;
        }
        return null;
    }

    /// <summary>
    /// Import a single external symbol into the namespace tree, re-parenting it
    /// from its temporary namespace chain into the real global hierarchy.
    /// Returns the imported symbol (in the real namespace tree).
    /// </summary>
    internal ISymbol? ImportSingleType(ISymbol type)
    {
        var fullName = type.FullName;
        if (_allDefinedTypes.TryGetValue(fullName, out var existing))
        {
            MidlTrace.Binder($"ImportSingleType: {fullName} already defined");
            return existing;
        }

        MidlTrace.Binder($"ImportSingleType: importing {fullName} ({type.Kind})");
        var lastDot = fullName.LastIndexOf('.');
        var nsName = lastDot >= 0 ? fullName[..lastDot] : "";
        var ns = GetOrCreateNamespaceHierarchy(nsName);

        switch (type)
        {
            case InterfaceSymbol iface:
                var newIface = new InterfaceSymbol(iface.Name, ns)
                {
                    Uuid = iface.Uuid,
                    IsExternal = true,
                    SourceWinMdPath = iface.SourceWinMdPath,
                };
                foreach (var m in iface.Members)
                    newIface.AddMember(m);
                foreach (var r in iface.RequiredInterfaces)
                    newIface.RequiredInterfaces.Add(r);
                ns.AddMember(newIface);
                _allDefinedTypes[newIface.FullName] = newIface;
                return newIface;

            case EnumSymbol e:
                var newEnum = new EnumSymbol(e.Name, ns)
                {
                    IsFlags = e.IsFlags,
                    UnderlyingType = e.UnderlyingType,
                    IsExternal = true,
                    SourceWinMdPath = e.SourceWinMdPath,
                };
                foreach (var m in e.EnumMembers)
                    newEnum.AddMember(m);
                ns.AddMember(newEnum);
                _allDefinedTypes[newEnum.FullName] = newEnum;
                return newEnum;

            case StructSymbol s:
                var newStruct = new StructSymbol(s.Name, ns)
                {
                    IsExternal = true,
                    SourceWinMdPath = s.SourceWinMdPath,
                };
                foreach (var f in s.Fields)
                    newStruct.AddField(f);
                ns.AddMember(newStruct);
                _allDefinedTypes[newStruct.FullName] = newStruct;
                return newStruct;

            case DelegateSymbol d:
                var newDel = new DelegateSymbol(d.Name, ns)
                {
                    ReturnType = d.ReturnType,
                    IsExternal = true,
                    SourceWinMdPath = d.SourceWinMdPath,
                };
                newDel.Parameters.AddRange(d.Parameters);
                ns.AddMember(newDel);
                _allDefinedTypes[newDel.FullName] = newDel;
                return newDel;

            case RuntimeClassSymbol rc:
                var newRc = new RuntimeClassSymbol(rc.Name, ns)
                {
                    Uuid = rc.Uuid,
                    BaseClass = rc.BaseClass,
                    IsStatic = rc.IsStatic,
                    IsUnsealed = rc.IsUnsealed,
                    IsExternal = true,
                    SourceWinMdPath = rc.SourceWinMdPath,
                };
                foreach (var i in rc.Interfaces)
                    newRc.Interfaces.Add(i);
                foreach (var m in rc.Members)
                    newRc.AddMember(m);
                ns.AddMember(newRc);
                _allDefinedTypes[newRc.FullName] = newRc;
                return newRc;

            case ApiContractSymbol ac:
                var newAc = new ApiContractSymbol(ac.Name, ns)
                {
                    Version = ac.Version,
                    IsExternal = true,
                    SourceWinMdPath = ac.SourceWinMdPath,
                };
                ns.AddMember(newAc);
                _allDefinedTypes[newAc.FullName] = newAc;
                return newAc;

            case AttributeTypeSymbol at:
                var newAt = new AttributeTypeSymbol(at.Name, ns)
                {
                    IsExternal = true,
                    SourceWinMdPath = at.SourceWinMdPath,
                };
                foreach (var f in at.Fields)
                    newAt.AddField(f);
                ns.AddMember(newAt);
                _allDefinedTypes[newAt.FullName] = newAt;
                return newAt;
        }
        return null;
    }
}
