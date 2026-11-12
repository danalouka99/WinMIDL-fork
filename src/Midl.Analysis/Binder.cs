// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;
using Midl.Syntax;

namespace Midl.Analysis;

/// <summary>
/// Walks a green syntax tree and produces a bound compilation with symbols.
/// </summary>
public partial class Binder
{
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly NamespaceSymbol _globalNamespace = new("");
    private readonly List<(TypeRef TypeRef, NamespaceSymbol ContainingNamespace, string? SourceFile)> _unresolvedRefs = [];
    private readonly HashSet<string> _definedTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forwardDeclaredTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _definedTypesCaseInsensitive = new(StringComparer.OrdinalIgnoreCase);
#if FEATURE_STAGING
    private readonly Dictionary<string, FeatureNameInfo> _featureNames = new(StringComparer.OrdinalIgnoreCase);
#endif
    private readonly CompilationMode _mode;
    private readonly bool _emitQualityHints;
    private bool _bindingImportedFile;
    private string? _currentImportedFilePath;
    /// <summary>
    /// Source maps for every preprocessed file in this compilation. A position produced
    /// while binding an imported tree is an offset into that file's expanded text; this is
    /// what turns it back into an original file and offset.
    /// </summary>
    private CompilationSourceMap? _unitSourceMaps;
    private TokenPositionMap _posMap = null!;
    private GreenNode _root = null!;
    private Dictionary<GreenNode, int>? _nodeOffsetMap;
    private bool _arraysEnabled;

    private SymbolTable _symbolTable = null!;

    public Binder(CompilationMode mode = CompilationMode.Strict, bool emitQualityHints = false, bool arraysEnabled = false)
    {
        _mode = mode;
        _emitQualityHints = emitQualityHints;
        _arraysEnabled = true; // Arrays always enabled — standard WinRT syntax
    }

    /// <summary>
    /// Attributes diagnostics produced from <paramref name="startIndex"/> onward to
    /// <paramref name="filePath"/>.
    ///
    /// Setting <c>OriginalPosition</c> marks the position as already resolved, so the
    /// preprocessor SourceMap pass leaves it alone. That matters because the source map
    /// only models <c>#include</c>: it would otherwise reinterpret an imported file's
    /// offset as an offset into the main document and attribute it there.
    ///
    /// Diagnostics already carrying a SourceFile (e.g. from a nested import) keep it.
    /// </summary>
    private void TagDiagnosticsWithSourceFile(int startIndex, string filePath)
        => DiagnosticSourceTagger.Tag(_diagnostics, startIndex, filePath, _unitSourceMaps);

    public Compilation Bind(GreenNode compilationUnit, IReadOnlyList<SyntaxToken> tokens,
        IReadOnlyList<ISymbol>? externalTypes = null,
        IReadOnlyList<(GreenNode Tree, IReadOnlyList<SyntaxToken> Tokens, string? FilePath, SourceMap? Map)>? importedTrees = null,
        IReadOnlyList<LazyWinMdProvider>? lazyProviders = null,
        IReadOnlyList<string>? refDirectories = null,
        CompilationSourceMap? unitSourceMaps = null)
    {
        MidlTrace.Binder("Binding compilation unit");
        _symbolTable = new SymbolTable(_globalNamespace);
        _posMap = new TokenPositionMap(tokens);
        _root = compilationUnit;
        _unitSourceMaps = unitSourceMaps;

        // Register built-in WinRT types before any user code
        BuiltInTypes.Register(_symbolTable, _mode);

        // Register lazy providers for on-demand WinMD type resolution
        if (lazyProviders != null)
        {
            foreach (var provider in lazyProviders)
                _symbolTable.AddLazyProvider(provider);
        }

        // Set reference directories for namespace-based WinMD discovery
        if (refDirectories is { Count: > 0 })
            _symbolTable.SetRefDirectories(refDirectories);

        // Import external types before binding user code
        if (externalTypes is { Count: > 0 })
            _symbolTable.ImportExternalTypes(externalTypes);

        // Bind imported trees first (they provide types for the main file)
        if (importedTrees is { Count: > 0 })
        {
            _bindingImportedFile = true;
            foreach (var (importedTree, importedTokens, importedFilePath, _) in importedTrees)
            {
                var savedPosMap = _posMap;
                var savedRoot = _root;
                var savedOffsetMap = _nodeOffsetMap;
                // _strictTokenIndex is a cursor into _posMap, so it has to travel with it.
                // Leaving it stale made the main file's own strict-mode tokens unfindable
                // (FindToken starts past the end of the shorter main token stream), silently
                // dropping every classic-construct diagnostic in the importing document.
                var savedStrictTokenIndex = _strictTokenIndex;
                _posMap = new TokenPositionMap(importedTokens);
                _root = importedTree;
                _nodeOffsetMap = null;
                _strictTokenIndex = 0;
                _currentImportedFilePath = importedFilePath;

                // Diagnostics raised while binding this tree carry positions in the
                // imported file's coordinates, not the main document's. Tag the range
                // produced by this pass so they are attributed to the right file and
                // skipped by the preprocessor SourceMap (which only knows #include).
                int diagStart = _diagnostics.Count;
                BindCompilationUnit(importedTree, _globalNamespace);
                if (importedFilePath != null)
                    TagDiagnosticsWithSourceFile(diagStart, importedFilePath);

                _posMap = savedPosMap;
                _root = savedRoot;
                _nodeOffsetMap = savedOffsetMap;
                _strictTokenIndex = savedStrictTokenIndex;
            }
            _currentImportedFilePath = null;
            _bindingImportedFile = false;
        }

        // Pass 1: create all symbols
        BindCompilationUnit(compilationUnit, _globalNamespace);

        // Pass 2: resolve type references
        var resolver = new TypeResolver(_symbolTable);
        resolver.ResolveAll(_unresolvedRefs, _diagnostics, _unitSourceMaps);

        // Pass 2.4: resolve required-interface TypeRefs on external interfaces so
        // that flattening external classes (Pass 3/emit) can traverse the transitive
        // closure. These TypeRefs were created by WinMdReader and are not in
        // _unresolvedRefs because they originate from imported metadata, not source.
        ResolveExternalRequiredInterfaces(resolver);

        // Pass 2.5: reclassify base lists — a type reference in the first slot of a
        // runtimeclass base list is a base class only if it resolves to another runtimeclass.
        // If it resolves to an interface, it's an implemented interface (class derives from Object).
        ReclassifyBaseLists(_globalNamespace);

        // Pass 3: semantic validation (inheritance, access modifiers, requires)
        ValidateSemantics(_globalNamespace);

        if (MidlTrace.IsEnabled)
        {
            int typeCount = 0;
            foreach (var _ in new Compilation(_globalNamespace, _diagnostics).GetAllTypes()) typeCount++;
            MidlTrace.Binder($"Binding complete: {typeCount} types, {_diagnostics.Count} diagnostics");
        }

        var compilation = new Compilation(_globalNamespace, _diagnostics, lazyProviders);
        compilation.SymbolTable = _symbolTable;
#if FEATURE_STAGING
        foreach (var kvp in _featureNames) compilation.FeatureNames[kvp.Key] = kvp.Value;
#endif
        return compilation;
    }

    private void BindCompilationUnit(GreenNode node, NamespaceSymbol ns)
    {
        // Strict mode: scan all tokens for classic MIDL types
        if (_mode == CompilationMode.Strict)
        {
            CheckStrictModeTokens(node);
        }

        for (int i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;
            BindDeclaration(child, ns);
        }
    }

    private void BindDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        // Strict mode: warn on classic MIDL constructs
#if WITH_COMPAT
        if (_mode == CompilationMode.Strict)
        {
            CheckStrictModeViolation(node);
        }
#endif

        // WinRT namespace requirements (varies by type and mode)
        if (ns.Name == "" && IsTypeDeclaration(node.Kind))
        {
            var typeName = ExtractTypeName(node);
            if (typeName != null)
            {
                // In compat mode: interfaces, coclasses, libraries, modules, dispinterfaces
                // are allowed at global scope (matches original MIDL behavior).
                // Only runtimeclass, delegate, attribute, apicontract, declare require namespaces.
                bool requiresNamespace = node.Kind is SyntaxKind.RuntimeClassDeclaration
                    or SyntaxKind.DelegateDeclaration
                    or SyntaxKind.AttributeDeclaration
                    or SyntaxKind.ApiContractDeclaration;

                // In strict mode, everything requires a namespace
#if WITH_COMPAT
                if (_mode == CompilationMode.Strict)
                    requiresNamespace = true;
#else
                requiresNamespace = true;
#endif

                if (requiresNamespace)
                {
                    int pos = _posMap.GetPositionOfFirst(typeName);
                    _diagnostics.Add(DiagnosticDescriptors.NamespaceRequired(typeName, pos));
                }
            }
        }

        switch (node.Kind)
        {
            case SyntaxKind.NamespaceDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindNamespaceDeclaration(node, ns);
                break;
            case SyntaxKind.RuntimeClassDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindRuntimeClassDeclaration(node, ns);
                break;
            case SyntaxKind.InterfaceDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindInterfaceDeclaration(node, ns);
                break;
            case SyntaxKind.DelegateDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindDelegateDeclaration(node, ns);
                break;
            case SyntaxKind.StructDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindStructDeclaration(node, ns);
                break;
            case SyntaxKind.EnumDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindEnumDeclaration(node, ns);
                break;
            case SyntaxKind.ApiContractDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindApiContractDeclaration(node, ns);
                break;
            case SyntaxKind.AttributeDeclaration:
                MidlTrace.Binder($"Binding {node.Kind} at declaration level");
                BindAttributeDeclaration(node, ns);
                break;
            case SyntaxKind.DeclareBlock:
                BindDeclareBlock(node, ns);
                break;
#if WITH_COMPAT
            case SyntaxKind.TypedefDeclaration:
                BindTypedefDeclaration(node, ns);
                break;
#endif
        }
    }

    #region Namespace

    private void BindNamespaceDeclaration(GreenNode node, NamespaceSymbol parentNs)
    {
        // Find name: first DottedName or IdentifierToken after NamespaceKeyword
        int keywordIdx = FindSlotIndex(node, SyntaxKind.NamespaceKeyword);
        if (keywordIdx < 0) return;

        var nameNode = node.GetSlot(keywordIdx + 1);
        var nameText = ExtractDottedName(nameNode);
        if (string.IsNullOrEmpty(nameText)) return;

        MidlTrace.Binder($"Entering namespace '{nameText}'");

        // Create nested namespace hierarchy for dotted names
        var parts = nameText.Split('.');
        var current = parentNs;
        foreach (var part in parts)
        {
            current = current.GetOrCreateChildNamespace(part);
        }

        // Bind declarations inside namespace body
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child != null)
                BindDeclaration(child, current);
        }
    }

    #endregion

    #region RuntimeClass

    private void BindRuntimeClassDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.RuntimeclassKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        // Check if this is a partial class
        bool isPartial = false;
        bool isStatic = false;
        bool isUnsealed = false;
        var modList = FindChild(node, SyntaxKind.ModifierList);
        if (modList != null)
        {
            for (int i = 0; i < modList.SlotCount; i++)
            {
                if (modList.GetSlot(i) is SyntaxToken mod)
                {
                    switch (mod.Kind)
                    {
                        case SyntaxKind.StaticKeyword: isStatic = true; break;
                        case SyntaxKind.PartialKeyword: isPartial = true; break;
                        case SyntaxKind.UnsealedKeyword: isUnsealed = true; break;
                    }
                }
            }
        }

        // Partial merging: if partial and an existing symbol with the same name exists, merge into it
        if (isPartial)
        {
            RuntimeClassSymbol? existing = null;
            foreach (var m in ns.Members)
            {
                if (m.Name == name && m is RuntimeClassSymbol rcs) { existing = rcs; break; }
            }
            if (existing != null)
            {
                // Validate matching modifiers
                if (existing.IsUnsealed != isUnsealed)
                {
                    int pos = nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name);
                    _diagnostics.Add(new Diagnostics.Diagnostic(
                        Diagnostics.DiagnosticSeverity.Error, "MIDL1031",
                        $"Partial runtimeclass '{name}' has conflicting modifiers (sealed vs unsealed)",
                        pos, name.Length));
                }

                // Validate no conflicting base classes
                var baseList = FindChild(node, SyntaxKind.BaseList);
                if (baseList != null)
                {
                    bool first = true;
                    for (int i = 1; i < baseList.SlotCount; i++)
                    {
                        var slot = baseList.GetSlot(i);
                        if (slot == null || slot.Kind == SyntaxKind.CommaToken || slot.Kind == SyntaxKind.ColonToken)
                            continue;

                        var typeRef = ExtractBaseTypeRef(slot, out bool isDefault);
                        TrackTypeRef(typeRef, ns);
                        if (isDefault)
                            typeRef.IsDefaultInterface = true;

                        if (first)
                        {
                            if (existing.BaseClass != null && existing.BaseClass.Name != typeRef.Name)
                            {
                                int pos = nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name);
                                _diagnostics.Add(new Diagnostics.Diagnostic(
                                    Diagnostics.DiagnosticSeverity.Error, "MIDL1032",
                                    $"Partial runtimeclass '{name}' has conflicting base class ('{existing.BaseClass.Name}' vs '{typeRef.Name}')",
                                    pos, name.Length));
                            }
                            else if (existing.BaseClass == null)
                            {
                                existing.BaseClass = typeRef;
                            }
                            first = false;
                        }
                        else
                        {
                            existing.Interfaces.Add(typeRef);
                        }
                    }
                }

                // Merge attributes
                var rcAttrList = FindChild(node, SyntaxKind.AttributeList);
                if (rcAttrList != null)
                {
                    if (TryExtractUuidAttribute(rcAttrList, out var rcUuid))
                        existing.Uuid ??= rcUuid;

                    var attrs = ExtractAttributes(rcAttrList);
                    existing.Attributes.AddRange(attrs);
                    ApplyWellKnownAttributes(existing, attrs);
                }

                // Merge members
                _symbolTable.EnterScope(existing);
                BindClassMembers(node, existing, ns);
                _symbolTable.ExitScope();
                return;
            }
        }

        bool isFullDefinition = FindSlotIndex(node, SyntaxKind.OpenBraceToken) >= 0;
        bool isForwardDecl = !isFullDefinition;

        // Check for existing forward-declared symbol to merge into
        if (isFullDefinition && !isPartial)
        {
            RuntimeClassSymbol? fwdExisting = null;
            foreach (var m in ns.Members)
            {
                if (m.Name == name && m is RuntimeClassSymbol rcs) { fwdExisting = rcs; break; }
            }
            if (fwdExisting != null)
            {
                var fullName = ns.FullName.Length > 0 ? string.Concat(ns.FullName, ".", name) : name;
                if (_forwardDeclaredTypes.Contains(fullName))
                {
                    _forwardDeclaredTypes.Remove(fullName);

                    var fwdAttrList = FindChild(node, SyntaxKind.AttributeList);
                    if (fwdAttrList != null)
                    {
                        if (TryExtractUuidAttribute(fwdAttrList, out var fwdUuid))
                            fwdExisting.Uuid ??= fwdUuid;
                        var attrs = ExtractAttributes(fwdAttrList);
                        fwdExisting.Attributes.AddRange(attrs);
                        ApplyWellKnownAttributes(fwdExisting, attrs);
                    }

                    fwdExisting.IsStatic = isStatic;
                    fwdExisting.IsPartial = isPartial;
                    fwdExisting.IsUnsealed = isUnsealed;

                    var fwdBaseList = FindChild(node, SyntaxKind.BaseList);
                    if (fwdBaseList != null)
                    {
                        bool first = true;
                        for (int i = 1; i < fwdBaseList.SlotCount; i++)
                        {
                            var slot = fwdBaseList.GetSlot(i);
                            if (slot == null || slot.Kind == SyntaxKind.CommaToken || slot.Kind == SyntaxKind.ColonToken)
                                continue;

                            var typeRef = ExtractBaseTypeRef(slot, out bool isDefault);
                            TrackTypeRef(typeRef, ns);
                            if (isDefault)
                                typeRef.IsDefaultInterface = true;

                            if (first)
                            {
                                fwdExisting.BaseClass = typeRef;
                                first = false;
                            }
                            else
                            {
                                fwdExisting.Interfaces.Add(typeRef);
                            }
                        }
                    }

                    _symbolTable.EnterScope(fwdExisting);
                    BindClassMembers(node, fwdExisting, ns);
                    _symbolTable.ExitScope();
                    return;
                }
            }
        }

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: isForwardDecl)) return;

        var symbol = new RuntimeClassSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachDocComment(symbol, node);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Extract [uuid("...")] if present
        var attrList = FindChild(node, SyntaxKind.AttributeList);
        if (attrList != null && TryExtractUuidAttribute(attrList, out var uuid))
        {
            symbol.Uuid = uuid;
        }

        // Extract all attributes
        if (attrList != null)
        {
            var attrs = ExtractAttributes(attrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        symbol.IsStatic = isStatic;
        symbol.IsPartial = isPartial;
        symbol.IsUnsealed = isUnsealed;

        // Base list
        var mainBaseList = FindChild(node, SyntaxKind.BaseList);
        if (mainBaseList != null)
        {
            bool first = true;
            for (int i = 1; i < mainBaseList.SlotCount; i++)
            {
                var slot = mainBaseList.GetSlot(i);
                if (slot == null || slot.Kind == SyntaxKind.CommaToken || slot.Kind == SyntaxKind.ColonToken)
                    continue;

                var typeRef = ExtractBaseTypeRef(slot, out bool isDefault);
                TrackTypeRef(typeRef, ns);
                if (isDefault)
                    typeRef.IsDefaultInterface = true;

                if (first)
                {
                    symbol.BaseClass = typeRef;
                    first = false;
                }
                else
                {
                    symbol.Interfaces.Add(typeRef);
                }
            }
        }

        // Bind members
        _symbolTable.EnterScope(symbol);
        BindClassMembers(node, symbol, ns);
        _symbolTable.ExitScope();
    }

    private void BindClassMembers(GreenNode node, RuntimeClassSymbol classSymbol, NamespaceSymbol ns)
    {
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;

            switch (child.Kind)
            {
                case SyntaxKind.ConstructorDeclaration:
                    classSymbol.AddMember(BindConstructor(child, classSymbol, ns));
                    break;
                case SyntaxKind.MethodDeclaration:
                    var method = BindMethod(child, classSymbol, ns);
                    CheckSameArityOverload(classSymbol.Members, method);
                    classSymbol.AddMember(method);
                    break;
                case SyntaxKind.PropertyDeclaration:
                    var prop = BindProperty(child, classSymbol, ns);
                    CheckWriteOnlyProperty(prop);
                    classSymbol.AddMember(prop);
                    break;
                case SyntaxKind.EventDeclaration:
                    classSymbol.AddMember(BindEvent(child, classSymbol, ns));
                    break;
                case SyntaxKind.VersionedBlock:
                    BindVersionedBlockMembers(child, classSymbol, ns);
                    break;
                case SyntaxKind.FieldDeclaration:
                {
                    var fieldName = FindNameIdentifier(child);
                    if (!string.IsNullOrEmpty(fieldName))
                        _diagnostics.Add(DiagnosticDescriptors.FieldInRuntimeClass(fieldName, _posMap.GetPositionOfFirst(fieldName)));
                    break;
                }
            }
        }
    }

    private void BindVersionedBlockMembers(GreenNode node, RuntimeClassSymbol classSymbol, NamespaceSymbol ns)
    {
        // Extract block-level attributes (e.g. [contract(C, N)] { ... })
        List<AttributeData>? blockAttrs = null;
        var blockAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (blockAttrList != null)
        {
            blockAttrs = ExtractAttributes(blockAttrList);
        }

        // Validate versioned block version vs class contract version
        if (blockAttrs != null && classSymbol.ContractVersion is int outerVer)
        {
            foreach (var ba in blockAttrs)
            {
                if (ba.Name.Equals("contract", StringComparison.OrdinalIgnoreCase))
                {
                    var innerVer = (int?)ba.GetVersion(1);
                    if (innerVer is int iv && iv < outerVer)
                    {
                        _diagnostics.Add(DiagnosticDescriptors.VersionedBlockConflict(iv, outerVer, ba.Position));
                    }
                }
            }
        }

        for (int i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;

            ISymbol? member = null;
            switch (child.Kind)
            {
                case SyntaxKind.ConstructorDeclaration:
                    member = BindConstructor(child, classSymbol, ns);
                    classSymbol.AddMember(member);
                    break;
                case SyntaxKind.MethodDeclaration:
                    member = BindMethod(child, classSymbol, ns);
                    classSymbol.AddMember(member);
                    break;
                case SyntaxKind.PropertyDeclaration:
                    member = BindProperty(child, classSymbol, ns);
                    classSymbol.AddMember(member);
                    break;
                case SyntaxKind.EventDeclaration:
                    member = BindEvent(child, classSymbol, ns);
                    classSymbol.AddMember(member);
                    break;
            }

            // Apply block-level attributes to the member
            if (member != null && blockAttrs != null)
            {
                ApplyWellKnownAttributes(member, blockAttrs);
                // Also add block attrs to member's attribute list for validation
                if (member is MethodSymbol m2) m2.Attributes.AddRange(blockAttrs);
                else if (member is PropertySymbol p2) p2.Attributes.AddRange(blockAttrs);
                else if (member is EventSymbol e2) e2.Attributes.AddRange(blockAttrs);
                else if (member is ConstructorSymbol c2) c2.Attributes.AddRange(blockAttrs);
            }
        }
    }

    #endregion

    #region Interface

    private void BindInterfaceDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.InterfaceKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0)) return;

        var symbol = new InterfaceSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachDocComment(symbol, node);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Extract [uuid("...")] if present
        var attrList = FindChild(node, SyntaxKind.AttributeList);
        if (attrList != null && TryExtractUuidAttribute(attrList, out var uuid))
        {
            symbol.Uuid = uuid;
        }

        // Extract all attributes
        if (attrList != null)
        {
            var attrs = ExtractAttributes(attrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Requires clause
        var requiresClause = FindChild(node, SyntaxKind.RequiresClause);
        if (requiresClause != null)
        {
            for (int i = 1; i < requiresClause.SlotCount; i++)
            {
                var slot = requiresClause.GetSlot(i);
                if (slot == null || slot.Kind == SyntaxKind.CommaToken) continue;
                var typeRef = ExtractTypeRef(slot);
                TrackTypeRef(typeRef, ns);
                symbol.RequiredInterfaces.Add(typeRef);
            }
        }

        // Base list
        var baseList = FindChild(node, SyntaxKind.BaseList);
        if (baseList != null)
        {
            for (int i = 1; i < baseList.SlotCount; i++)
            {
                var slot = baseList.GetSlot(i);
                if (slot == null || slot.Kind == SyntaxKind.CommaToken || slot.Kind == SyntaxKind.ColonToken)
                    continue;
                var typeRef = ExtractTypeRef(slot);
                TrackTypeRef(typeRef, ns);
                symbol.RequiredInterfaces.Add(typeRef);
            }
        }

        // Bind members
        _symbolTable.EnterScope(symbol);
        BindInterfaceMembers(node, symbol, ns);
        _symbolTable.ExitScope();
    }

    private void BindInterfaceMembers(GreenNode node, InterfaceSymbol ifaceSymbol, NamespaceSymbol ns)
    {
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;

#if WITH_COMPAT
            // Interface members do not go through BindDeclaration, so strict-mode checking
            // has to be driven from here or classic constructs in member position are
            // silently accepted. Doing it for every member — rather than case by case —
            // means a construct added to the switch below cannot be forgotten here.
            if (_mode == CompilationMode.Strict)
                CheckStrictModeViolation(child);
#endif

            switch (child.Kind)
            {
#if WITH_COMPAT
                case SyntaxKind.TypedefDeclaration:
                case SyntaxKind.CppQuoteDirective:
                    // Strict-mode rejection is handled by the blanket check above.
                    // The type belongs to the enclosing namespace, not to the interface — a
                    // typedef is not a member — so bind it there, or every use is undefined.
                    if (child.Kind == SyntaxKind.TypedefDeclaration)
                        BindTypedefDeclaration(child, ns);
                    break;
#endif
                case SyntaxKind.ConstructorDeclaration:
                {
                    // MIDL1039: Constructors are not allowed in interfaces
                    var ctorName = FindNameIdentifier(child);
                    if (!string.IsNullOrEmpty(ctorName))
                        _diagnostics.Add(DiagnosticDescriptors.ConstructorInInterface(ctorName, _posMap.GetPositionOfFirst(ctorName)));
                    break;
                }
                case SyntaxKind.MethodDeclaration:
                {
                    var method = BindMethod(child, ifaceSymbol, ns);
                    // MIDL1040: Static members are not allowed in interfaces
                    if (method.IsStatic && !string.IsNullOrEmpty(method.Name))
                        _diagnostics.Add(DiagnosticDescriptors.StaticMemberInInterface(method.Name, GetSymbolPosition(method)));
                    ifaceSymbol.AddMember(method);
                    break;
                }
                case SyntaxKind.PropertyDeclaration:
                {
                    var prop = BindProperty(child, ifaceSymbol, ns);
                    if (prop.IsStatic && !string.IsNullOrEmpty(prop.Name))
                        _diagnostics.Add(DiagnosticDescriptors.StaticMemberInInterface(prop.Name, GetSymbolPosition(prop)));
                    ifaceSymbol.AddMember(prop);
                    break;
                }
                case SyntaxKind.EventDeclaration:
                {
                    var evt = BindEvent(child, ifaceSymbol, ns);
                    if (evt.IsStatic && !string.IsNullOrEmpty(evt.Name))
                        _diagnostics.Add(DiagnosticDescriptors.StaticMemberInInterface(evt.Name, GetSymbolPosition(evt)));
                    ifaceSymbol.AddMember(evt);
                    break;
                }
                case SyntaxKind.VersionedBlock:
                    BindInterfaceVersionedBlockMembers(child, ifaceSymbol, ns);
                    break;
            }
        }
    }

    private void BindInterfaceVersionedBlockMembers(GreenNode node, InterfaceSymbol ifaceSymbol, NamespaceSymbol ns)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;

            switch (child.Kind)
            {
                case SyntaxKind.MethodDeclaration:
                    ifaceSymbol.AddMember(BindMethod(child, ifaceSymbol, ns));
                    break;
                case SyntaxKind.PropertyDeclaration:
                    ifaceSymbol.AddMember(BindProperty(child, ifaceSymbol, ns));
                    break;
                case SyntaxKind.EventDeclaration:
                    ifaceSymbol.AddMember(BindEvent(child, ifaceSymbol, ns));
                    break;
            }
        }
    }

    #endregion

    #region Delegate

    private void BindDelegateDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.DelegateKeyword);
        if (keywordIdx < 0) return;

        // After delegate keyword: TypeReference, IdentifierToken, ParameterList
        var returnTypeNode = node.GetSlot(keywordIdx + 1);
        var nameToken = node.GetSlot(keywordIdx + 2) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0)) return;

        var symbol = new DelegateSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachDocComment(symbol, node);
        symbol.ReturnType = ExtractTypeRef(returnTypeNode);
        TrackTypeRef(symbol.ReturnType, ns);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Extract [uuid(...)] if present
        var delAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (delAttrList != null && TryExtractUuidAttribute(delAttrList, out var delUuid))
        {
            symbol.Uuid = delUuid;
        }

        // Extract all attributes
        if (delAttrList != null)
        {
            var attrs = ExtractAttributes(delAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Parameters
        var paramList = FindChild(node, SyntaxKind.ParameterList);
        if (paramList != null)
        {
            symbol.Parameters.AddRange(BindParameters(paramList, symbol, ns));
        }
    }

    #endregion

    #region Struct

    private void BindStructDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.StructKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0)) return;

        var symbol = new StructSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachDocComment(symbol, node);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Extract all attributes
        var structAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (structAttrList != null)
        {
            var attrs = ExtractAttributes(structAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Fields between braces
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child?.Kind == SyntaxKind.FieldDeclaration)
            {
                symbol.AddField(BindField(child, symbol, ns));
            }
            // MIDL1041: Properties and events are not allowed in structs
            else if (child?.Kind == SyntaxKind.PropertyDeclaration)
            {
                var propName = FindNameIdentifier(child);
                if (!string.IsNullOrEmpty(propName))
                    _diagnostics.Add(DiagnosticDescriptors.PropertyInStruct(propName, _posMap.GetPositionOfFirst(propName)));
            }
            else if (child?.Kind == SyntaxKind.EventDeclaration)
            {
                var evtName = FindNameIdentifier(child);
                if (!string.IsNullOrEmpty(evtName))
                    _diagnostics.Add(DiagnosticDescriptors.EventInStruct(evtName, _posMap.GetPositionOfFirst(evtName)));
            }
        }

        // MIDL1009: Empty struct
        if (symbol.Fields.Count == 0)
        {
            _diagnostics.Add(DiagnosticDescriptors.EmptyStruct(name, GetSymbolPosition(symbol)));
        }
    }

    private FieldSymbol BindField(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        // Slots: [AttributeList], TypeReference, IdentifierToken, SemicolonToken
        var typeNode = FindChild(node, SyntaxKind.TypeReference)
            ?? FindChild(node, SyntaxKind.GenericName)
            ?? FindChild(node, SyntaxKind.QualifiedName)
            ?? FindChild(node, SyntaxKind.ArrayType);

        // Find name: last identifier token before semicolon
        var fieldNameToken = FindNameIdentifierToken(node);
        var name = fieldNameToken?.Text ?? "";

        var field = new FieldSymbol(name, containingSymbol);
        if (fieldNameToken != null) field.SourcePosition = GetNodePosition(fieldNameToken);
        if (typeNode != null)
        {
            field.Type = ExtractTypeRef(typeNode);
            TrackTypeRef(field.Type, ns);
        }

        // Extract attributes on field
        var attrList = FindChild(node, SyntaxKind.AttributeList);
        if (attrList != null)
        {
            var attrs = ExtractAttributes(attrList);
            field.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(field, attrs);
        }

        return field;
    }

    #endregion

    #region Attribute (user-defined)

    private void BindAttributeDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.AttributeKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0)) return;

        var symbol = new AttributeTypeSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Extract attributes on the attribute declaration itself
        var attrList = FindChild(node, SyntaxKind.AttributeList);
        if (attrList != null)
        {
            var attrs = ExtractAttributes(attrList);
            symbol.Attributes.AddRange(attrs);

            foreach (var attr in attrs)
            {
                switch (attr.Name.ToLowerInvariant())
                {
                    case "attributeusage":
                        foreach (var arg in attr.Arguments)
                        {
                            if (arg.Kind == ArgKind.Identifier && arg.StringValue != null)
                                symbol.ValidTargets.Add(arg.StringValue);
                        }
                        break;

                    case "attributename":
                        symbol.AttributeNameOverride = attr.GetString(0);
                        break;

                    case "allowmultiple":
                        symbol.AllowMultiple = true;
                        break;
                }
            }
        }

        // Fields between braces
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child?.Kind == SyntaxKind.FieldDeclaration)
            {
                symbol.AddField(BindField(child, symbol, ns));
            }
        }
    }

    #endregion

    #region Enum

    private void BindEnumDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.EnumKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDeclaration: FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0)) return;

        var symbol = new EnumSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachDocComment(symbol, node);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        // Check for [flags] attribute
        var enumAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (enumAttrList != null && HasAttribute(enumAttrList, "flags"))
        {
            symbol.IsFlags = true;
            // Per WinRT spec: [flags] enums have UInt32 underlying type
            symbol.UnderlyingType = "UInt32";
        }

        // Extract all attributes on the enum
        if (enumAttrList != null)
        {
            var attrs = ExtractAttributes(enumAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Enum members between braces
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        var enumMemberNames = new HashSet<string>(StringComparer.Ordinal);

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child?.Kind == SyntaxKind.EnumMemberDeclaration)
            {
                var memberName = FindFirstIdentifierText(child);
                if (!string.IsNullOrEmpty(memberName))
                {
                    // MIDL1046: Duplicate enum member
                    if (!enumMemberNames.Add(memberName))
                    {
                        _diagnostics.Add(DiagnosticDescriptors.DuplicateEnumMember(memberName, _posMap.GetPositionOfFirst(memberName)));
                    }

                    var member = new EnumMemberSymbol(memberName, symbol);
                    var memberNameToken = FindFirstIdentifierToken(child);
                    if (memberNameToken != null) member.SourcePosition = GetNodePosition(memberNameToken);

                    // Extract explicit value (Name = expr)
                    ExtractEnumMemberValue(child, member, symbol);

                    // Extract attributes on enum member
                    var memberAttrList = FindChild(child, SyntaxKind.AttributeList);
                    if (memberAttrList != null)
                    {
                        var memberAttrs = ExtractAttributes(memberAttrList);
                        member.Attributes.AddRange(memberAttrs);
                        ApplyWellKnownAttributes(member, memberAttrs);
                    }

                    symbol.AddMember(member);
                }
            }
        }
    }

    private void ExtractEnumMemberValue(GreenNode memberNode, EnumMemberSymbol member, EnumSymbol parentEnum)
    {
        int eqIdx = FindSlotIndex(memberNode, SyntaxKind.EqualsToken);
        if (eqIdx < 0) return;

        for (int j = eqIdx + 1; j < memberNode.SlotCount; j++)
        {
            var valSlot = memberNode.GetSlot(j);
            if (valSlot == null) continue;
            if (valSlot is SyntaxToken { Kind: SyntaxKind.CommaToken }) break;

            member.ExplicitValueText = CollectNodeText(valSlot);

            // Check for float/string literals which are invalid for enum values
            var firstToken = FindFirstToken(valSlot);
            if (firstToken?.Kind is SyntaxKind.FloatLiteralToken or SyntaxKind.StringLiteralToken)
            {
                _diagnostics.Add(DiagnosticDescriptors.EnumValueNotInteger(
                    member.Name, parentEnum.Name, firstToken.Text, _posMap.GetPositionOfFirst(firstToken.Text)));
                break;
            }

            var result = EvaluateEnumExpression(valSlot, parentEnum);
            if (result.HasValue)
            {
                member.ExplicitValue = result.Value;
                ValidateEnumValueRange(result.Value, member.ExplicitValueText ?? "", member.Name, parentEnum);
            }
            break;
        }
    }

    /// <summary>Recursively evaluate an expression green node to a constant long value.</summary>
    private long? EvaluateEnumExpression(GreenNode node, EnumSymbol parentEnum)
    {
        switch (node.Kind)
        {
            case SyntaxKind.NumericLiteralExpression:
            {
                var token = FindFirstToken(node);
                if (token == null) return null;
                return ParseNumericLiteral(token.Text);
            }

            case SyntaxKind.NumericLiteralToken:
                return ParseNumericLiteral(((SyntaxToken)node).Text);

            case SyntaxKind.UnaryExpression:
            {
                // children: op, operand
                var op = node.GetSlot(0) as SyntaxToken;
                var operand = node.GetSlot(1);
                if (op == null || operand == null) return null;
                var val = EvaluateEnumExpression(operand, parentEnum);
                if (!val.HasValue) return null;
                return op.Kind switch
                {
                    SyntaxKind.MinusToken => -val.Value,
                    SyntaxKind.PlusToken => val.Value,
                    SyntaxKind.TildeToken => ~val.Value,
                    SyntaxKind.ExclamationToken => val.Value == 0 ? 1L : 0L,
                    _ => null
                };
            }

            case SyntaxKind.BinaryExpression:
            {
                // children: left, op, right
                var left = node.GetSlot(0);
                var op = node.GetSlot(1) as SyntaxToken;
                var right = node.GetSlot(2);
                if (left == null || op == null || right == null) return null;
                var lv = EvaluateEnumExpression(left, parentEnum);
                var rv = EvaluateEnumExpression(right, parentEnum);
                if (!lv.HasValue || !rv.HasValue) return null;
                return op.Kind switch
                {
                    SyntaxKind.PlusToken => lv.Value + rv.Value,
                    SyntaxKind.MinusToken => lv.Value - rv.Value,
                    SyntaxKind.AsteriskToken => lv.Value * rv.Value,
                    SyntaxKind.SlashToken => rv.Value != 0 ? lv.Value / rv.Value : null,
                    SyntaxKind.PercentToken => rv.Value != 0 ? lv.Value % rv.Value : null,
                    SyntaxKind.PipeToken => lv.Value | rv.Value,
                    SyntaxKind.AmpersandToken => lv.Value & rv.Value,
                    SyntaxKind.CaretToken => lv.Value ^ rv.Value,
                    SyntaxKind.LessThanLessThanToken => lv.Value << (int)rv.Value,
                    SyntaxKind.GreaterThanGreaterThanToken => lv.Value >> (int)rv.Value,
                    _ => null
                };
            }

            case SyntaxKind.ParenthesizedExpression:
            {
                // children: open, expr, close
                var inner = node.GetSlot(1);
                if (inner == null) return null;
                return EvaluateEnumExpression(inner, parentEnum);
            }

            case SyntaxKind.IdentifierExpression:
            {
                var text = CollectNodeText(node);
                // Look up in already-bound enum members
                foreach (var m in parentEnum.EnumMembers)
                {
                    if (string.Equals(m.Name, text, StringComparison.Ordinal) && m.ExplicitValue.HasValue)
                        return m.ExplicitValue.Value;
                }
                // Try parsing as a numeric literal (preprocessor define already substituted)
                return ParseNumericLiteral(text);
            }

            default:
            {
                // If it's a raw token, try to parse it
                if (node is SyntaxToken rawToken && rawToken.Kind == SyntaxKind.NumericLiteralToken)
                    return ParseNumericLiteral(rawToken.Text);
                // Try recursing into single-child wrapper nodes
                if (node.SlotCount == 1)
                {
                    var child = node.GetSlot(0);
                    if (child != null) return EvaluateEnumExpression(child, parentEnum);
                }
                return null;
            }
        }
    }

    private static long? ParseNumericLiteral(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return null;
        bool neg = txt.StartsWith('-');
        var s = neg ? txt.AsSpan(1) : txt.AsSpan();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out long v))
        {
            return neg ? -v : v;
        }
        if (long.TryParse(txt, out v))
            return v;
        return null;
    }

    private static SyntaxToken? FindFirstToken(GreenNode node)
    {
        if (node is SyntaxToken t) return t;
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;
            var result = FindFirstToken(slot);
            if (result != null) return result;
        }
        return null;
    }

    private void ValidateEnumValueRange(long value, string valueText, string memberName, EnumSymbol parentEnum)
    {
        // Original MIDL does not reject enum values that exceed Int32/UInt32 range;
        // it silently truncates them.  Match that behaviour by skipping the check.
#if WITH_COMPAT
        if (_mode == CompilationMode.Compat) return;
#endif
    }

    #endregion

    #region ApiContract

    private void BindApiContractDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        int keywordIdx = FindSlotIndex(node, SyntaxKind.ApicontractKeyword);
        if (keywordIdx < 0) return;

        var nameToken = node.GetSlot(keywordIdx + 1) as SyntaxToken;
        var name = nameToken?.Text ?? "";
        if (string.IsNullOrEmpty(name)) return;

        bool isForwardDecl = FindSlotIndex(node, SyntaxKind.OpenBraceToken) < 0;
        if (!TryDefineType(ns, name, nameToken != null ? GetNodePosition(nameToken) : _posMap.GetPositionOfFirst(name), isForwardDecl)) return;

        var symbol = new ApiContractSymbol(name, ns);
        symbol.IsExternal = _bindingImportedFile || symbol.IsExternal;
        if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        ns.AddMember(symbol);
        _symbolTable.Define(symbol);

        var acAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (acAttrList != null)
        {
            var attrs = ExtractAttributes(acAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }
    }

    #endregion

#if WITH_COMPAT
    #region Typedef

    /// <summary>
    /// Bind a typedef declaration. For typedef enum/struct/union, bind the inner type.
    /// Also register the alias name.
    /// </summary>
    private void BindTypedefDeclaration(GreenNode node, NamespaceSymbol ns)
    {
        // Walk children: find enum/struct keyword → bind the type
        // typedef [attrs] enum Name { members } AliasName;
        // typedef [attrs] struct Name { fields } AliasName;
        // typedef enum Name AliasName;  (forward decl)
        // typedef struct { fields } AliasName;  (anonymous — the alias is the only name)
        // typedef long AliasName, *PAliasName;  (simple alias, several declarators)

        string? typeName = null;
        bool hasBody = false;
        bool seenCloseBrace = false;
        bool seenTypeReference = false;
        GreenNode? typeRefNode = null;
        SyntaxKind typeKind = SyntaxKind.None;

        // Every name declared after the type part. Classic MIDL declares several at once and
        // decorates each independently, so "typedef long A, *PA;" introduces both.
        var declarators = new List<string>();

        // A classic struct/union body is consumed as raw tokens, so the field names land in
        // this same child list. Only identifiers outside the braces are declarators —
        // otherwise "typedef union { long a; short b; } U;" names the type after its last
        // field instead of after the alias.
        int braceDepth = 0;

        for (int i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child == null) continue;

            if (child is SyntaxToken tok)
            {
                if (tok.Kind is SyntaxKind.EnumKeyword or SyntaxKind.StructKeyword or SyntaxKind.UnionKeyword)
                    typeKind = tok.Kind;
                else if (tok.Kind == SyntaxKind.OpenBraceToken)
                {
                    hasBody = true;
                    braceDepth++;
                }
                else if (tok.Kind == SyntaxKind.CloseBraceToken)
                {
                    braceDepth--;
                    if (braceDepth <= 0) seenCloseBrace = true;
                }
                else if (tok.Kind == SyntaxKind.IdentifierToken && braceDepth == 0)
                {
                    bool afterTypePart = seenCloseBrace || seenTypeReference || hasBody;
                    if (typeKind != SyntaxKind.None && !afterTypePart && typeName == null)
                        typeName = tok.Text;   // the tag, as in "struct tagPoint"
                    else
                        declarators.Add(tok.Text);
                }
            }
            // Also check for inner EnumDeclaration/StructDeclaration nodes (from old parser path)
            else if (child.Kind == SyntaxKind.EnumDeclaration)
            {
                BindEnumDeclaration(child, ns);
                return;
            }
            else if (child.Kind == SyntaxKind.StructDeclaration)
            {
                BindStructDeclaration(child, ns);
                return;
            }
            else if (child.Kind is SyntaxKind.TypeReference or SyntaxKind.PointerType or SyntaxKind.ArrayType)
            {
                typeRefNode = child;
                seenTypeReference = true;
            }
        }

        if (typeKind == SyntaxKind.None)
        {
            // Simple alias: "typedef long MyLong;" — every declarator names the same type.
            if (typeRefNode == null) return;
            var underlying = ExtractTypeRef(typeRefNode).Name;
            if (string.IsNullOrEmpty(underlying)) return;

            // The underlying spelling may itself be an alias — "typedef unsigned long DWORD;"
            // where "unsigned long" already maps to UInt32. Point at the end of the chain, so
            // DWORD resolves in one hop rather than to a name that is not a symbol.
            var target = _symbolTable.ResolveAlias(underlying) ?? underlying;

            foreach (var name in declarators)
            {
                if (name != target) _symbolTable.RegisterAlias(name, target);
            }
            return;
        }

        // An anonymous struct/enum/union has no tag, so the first declarator is its name —
        // otherwise the type has no name at all and every reference to it is undefined.
        typeName ??= declarators.FirstOrDefault();
        if (string.IsNullOrEmpty(typeName)) return;

        // Register the type
        bool isForwardDecl = !hasBody;
        if (!TryDefineType(ns, typeName, isForwardDeclaration: isForwardDecl)) return;

        if (typeKind == SyntaxKind.EnumKeyword)
        {
            var symbol = new EnumSymbol(typeName, ns);
            if (_bindingImportedFile) symbol.IsExternal = true;
            if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
            ns.AddMember(symbol);
            _symbolTable.Define(symbol);

            // If it has a body, extract enum members
            if (hasBody)
            {
                for (int i = 0; i < node.SlotCount; i++)
                {
                    var child = node.GetSlot(i);
                    if (child?.Kind == SyntaxKind.EnumMemberDeclaration)
                    {
                        var memberName = FindFirstIdentifierText(child);
                        if (!string.IsNullOrEmpty(memberName))
                        {
                            var member = new EnumMemberSymbol(memberName, symbol);
                            symbol.AddMember(member);
                        }
                    }
                }
            }
        }
        else if (typeKind is SyntaxKind.StructKeyword or SyntaxKind.UnionKeyword)
        {
            // A union is modelled as a struct: MIDL3 has no union, and compat mode only needs
            // the name to resolve so the rest of the file makes sense. Strict mode rejects the
            // typedef regardless.
            var symbol = new StructSymbol(typeName, ns);
            if (_bindingImportedFile) symbol.IsExternal = true;
            if (_currentImportedFilePath != null) symbol.SourceFile = _currentImportedFilePath;
            ns.AddMember(symbol);
            _symbolTable.Define(symbol);
        }

        // Register the alias names (e.g. `Xxx` in `typedef struct tagXxx { ... } Xxx;`)
        // as simple aliases so every spelling resolves to the same type.
        foreach (var alias in declarators)
        {
            if (!string.IsNullOrEmpty(alias) && alias != typeName)
                _symbolTable.RegisterAlias(alias, typeName);
        }
    }

    #endregion
#endif

    #region DeclareBlock / feature_name

#if FEATURE_STAGING
    private static readonly Dictionary<string, FeatureStage> ValidFeatureStages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AlwaysDisabled"] = FeatureStage.AlwaysDisabled,
        ["DisabledByDefault"] = FeatureStage.DisabledByDefault,
        ["EnabledByDefault"] = FeatureStage.EnabledByDefault,
        ["AlwaysEnabled"] = FeatureStage.AlwaysEnabled,
    };
#endif

    private void BindDeclareBlock(GreenNode node, NamespaceSymbol ns)
    {
#if FEATURE_STAGING
        // Check for feature_name declarations:
        //   feature_name MyFeature = {AlwaysEnabled, TRUE};
        if (node.SlotCount >= 2)
        {
            var firstToken = node.GetSlot(0) as SyntaxToken;
            if (firstToken?.Text == "feature_name")
            {
                BindFeatureNameDeclaration(node);
                return;
            }
        }
#endif

        // General declare block: declare { ... }
        int openBraceIdx = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        int closeBraceIdx = FindLastSlotIndex(node, SyntaxKind.CloseBraceToken);
        if (openBraceIdx < 0 || closeBraceIdx < 0) return;

        for (int i = openBraceIdx + 1; i < closeBraceIdx; i++)
        {
            var child = node.GetSlot(i);
            if (child != null)
                BindDeclaration(child, ns);
        }
    }

#if FEATURE_STAGING
    /// <summary>Bind a feature_name declaration, extracting stage and valid-in-all-branches flag.</summary>
    private void BindFeatureNameDeclaration(GreenNode node)
    {
        // Slots: 0=feature_name, 1=Name, 2==, 3={, 4=Stage, 5=comma, 6=Bool, 7=}, 8=;
        var nameToken = node.GetSlot(1) as SyntaxToken;
        var name = nameToken?.Text;
        if (string.IsNullOrEmpty(name)) return;

        var stage = FeatureStage.AlwaysDisabled;
        var validInAllBranches = false;

        // Find open brace to locate stage and bool
        int openBrace = FindSlotIndex(node, SyntaxKind.OpenBraceToken);
        if (openBrace >= 0)
        {
            // Stage identifier is the first identifier after '{'
            var stageToken = FindNextIdentifier(node, openBrace + 1);
            if (stageToken != null)
            {
                if (ValidFeatureStages.TryGetValue(stageToken.Text, out var parsedStage))
                {
                    stage = parsedStage;
                }
                else
                {
                    int pos = _posMap.GetPositionOfFirst(stageToken.Text);
                    _diagnostics.Add(DiagnosticDescriptors.InvalidFeatureStage(
                        name, stageToken.Text, pos));
                }
            }

            // Bool identifier is the next identifier after comma
            int commaIdx = FindSlotIndex(node, SyntaxKind.CommaToken, openBrace + 1);
            if (commaIdx >= 0)
            {
                var boolToken = FindNextIdentifier(node, commaIdx + 1);
                if (boolToken != null)
                    validInAllBranches = boolToken.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            }
        }

        var info = new FeatureNameInfo(name, stage, validInAllBranches);
        _featureNames[name] = info;
        MidlTrace.Binder($"Registered feature_name '{name}' stage={stage} validInAllBranches={validInAllBranches}");
    }

    /// <summary>Find the next identifier or TRUE/FALSE keyword starting at the given slot index.</summary>
    private static SyntaxToken? FindNextIdentifier(GreenNode node, int startSlot)
    {
        for (int i = startSlot; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is SyntaxToken tok &&
                tok.Kind is SyntaxKind.IdentifierToken or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword)
                return tok;
        }
        return null;
    }
#endif

    #endregion

    #region Members

    private ConstructorSymbol BindConstructor(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        var ctorNameToken = FindNameIdentifierToken(node);
        var name = ctorNameToken?.Text ?? "";
        var symbol = new ConstructorSymbol(name, containingSymbol);
        if (ctorNameToken != null) symbol.SourcePosition = GetNodePosition(ctorNameToken);
        AttachMemberDocComment(symbol, node);

        // Extract all attributes
        var ctorAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (ctorAttrList != null)
        {
            var attrs = ExtractAttributes(ctorAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        var paramList = FindChild(node, SyntaxKind.ParameterList);
        if (paramList != null)
        {
            symbol.Parameters.AddRange(BindParameters(paramList, symbol, ns));
        }

        return symbol;
    }

    private MethodSymbol BindMethod(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        // Slots: [AttributeList], [ModifierList], TypeReference, IdentifierToken, ParameterList, SemicolonToken
        // Find the TypeReference, then the next identifier after it
        var (returnTypeNode, nameText, methodNameToken) = ExtractReturnTypeAndName(node);

        var symbol = new MethodSymbol(nameText, containingSymbol);
        if (methodNameToken != null) symbol.SourcePosition = GetNodePosition(methodNameToken);
        AttachMemberDocComment(symbol, node);
        symbol.ReturnType = ExtractTypeRef(returnTypeNode);
        TrackTypeRef(symbol.ReturnType, ns);

        // MIDL3 has no explicit calling convention; the parser accepts one so classic
        // files don't cascade into parse errors, and strict mode reports it here.
        if (_mode == CompilationMode.Strict)
        {
            for (int i = 0; i < node.SlotCount; i++)
            {
                if (node.GetSlot(i) is SyntaxToken { Kind: SyntaxKind.CallingConventionToken } cc)
                {
                    _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct(cc.Text, GetNodePosition(cc)));
                    break;
                }
            }
        }

        // Extract all attributes
        var methodAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (methodAttrList != null)
        {
            var attrs = ExtractAttributes(methodAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Check modifiers
        var modList = FindChild(node, SyntaxKind.ModifierList);
        if (modList != null && HasModifier(modList, SyntaxKind.StaticKeyword))
        {
            symbol.IsStatic = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.ProtectedKeyword))
        {
            symbol.IsProtected = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.OverridableKeyword))
        {
            symbol.IsOverridable = true;
        }

        var paramList = FindChild(node, SyntaxKind.ParameterList);
        if (paramList != null)
        {
            symbol.Parameters.AddRange(BindParameters(paramList, symbol, ns));
        }

        return symbol;
    }

    private PropertySymbol BindProperty(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        var (typeNode, nameText, propNameToken) = ExtractReturnTypeAndName(node);

        var symbol = new PropertySymbol(nameText, containingSymbol);
        if (propNameToken != null) symbol.SourcePosition = GetNodePosition(propNameToken);
        AttachMemberDocComment(symbol, node);
        symbol.Type = ExtractTypeRef(typeNode);
        TrackTypeRef(symbol.Type, ns);

        // Extract all attributes
        var propAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (propAttrList != null)
        {
            var attrs = ExtractAttributes(propAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Check modifiers
        var modList = FindChild(node, SyntaxKind.ModifierList);
        if (modList != null && HasModifier(modList, SyntaxKind.StaticKeyword))
        {
            symbol.IsStatic = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.ProtectedKeyword))
        {
            symbol.IsProtected = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.OverridableKeyword))
        {
            symbol.IsOverridable = true;
        }

        // Check accessors
        var accessors = FindChild(node, SyntaxKind.PropertyAccessors);
        if (accessors != null)
        {
            symbol.HasGetter = HasChildToken(accessors, SyntaxKind.GetKeyword);
            symbol.HasSetter = HasChildToken(accessors, SyntaxKind.SetKeyword);

            // Check for protected get/set: ProtectedKeyword immediately before GetKeyword/SetKeyword
            for (int a = 0; a < accessors.SlotCount - 1; a++)
            {
                if (accessors.GetSlot(a) is SyntaxToken { Kind: SyntaxKind.ProtectedKeyword } protectedToken)
                {
                    var next = accessors.GetSlot(a + 1);
                    if (next is SyntaxToken { Kind: SyntaxKind.GetKeyword })
                    {
                        symbol.IsGetterProtected = true;
                        _diagnostics.Add(DiagnosticDescriptors.ProtectedAccessorNotValid(
                            symbol.Name, "get", GetNodePosition(protectedToken)));
                    }
                    else if (next is SyntaxToken { Kind: SyntaxKind.SetKeyword })
                    {
                        symbol.IsSetterProtected = true;
                        _diagnostics.Add(DiagnosticDescriptors.ProtectedAccessorNotValid(
                            symbol.Name, "set", GetNodePosition(protectedToken)));
                    }
                }
            }
        }
        else
        {
            // No explicit accessors means public get+set (shorthand)
            symbol.HasGetter = true;
            symbol.HasSetter = true;
            symbol.IsShorthand = true;
        }

        return symbol;
    }

    private EventSymbol BindEvent(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        // Slots: [AttributeList], EventKeyword, TypeReference, IdentifierToken, SemicolonToken
        int eventIdx = FindSlotIndex(node, SyntaxKind.EventKeyword);
        if (eventIdx < 0) return new EventSymbol("", containingSymbol);

        var typeNode = node.GetSlot(eventIdx + 1);
        var nameToken = node.GetSlot(eventIdx + 2) as SyntaxToken;
        var name = nameToken?.Text ?? "";

        var symbol = new EventSymbol(name, containingSymbol);
        if (nameToken != null) symbol.SourcePosition = GetNodePosition(nameToken);
        AttachMemberDocComment(symbol, node);
        symbol.DelegateType = ExtractTypeRef(typeNode);
        TrackTypeRef(symbol.DelegateType, ns);

        // Extract all attributes
        var eventAttrList = FindChild(node, SyntaxKind.AttributeList);
        if (eventAttrList != null)
        {
            var attrs = ExtractAttributes(eventAttrList);
            symbol.Attributes.AddRange(attrs);
            ApplyWellKnownAttributes(symbol, attrs);
        }

        // Check modifiers
        var modList = FindChild(node, SyntaxKind.ModifierList);
        if (modList != null && HasModifier(modList, SyntaxKind.StaticKeyword))
        {
            symbol.IsStatic = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.ProtectedKeyword))
        {
            symbol.IsProtected = true;
        }
        if (modList != null && HasModifier(modList, SyntaxKind.OverridableKeyword))
        {
            symbol.IsOverridable = true;
        }

        return symbol;
    }

    private List<ParameterSymbol> BindParameters(GreenNode paramList, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        var result = new List<ParameterSymbol>();

        for (int i = 0; i < paramList.SlotCount; i++)
        {
            var child = paramList.GetSlot(i);
            if (child?.Kind != SyntaxKind.Parameter) continue;

            var param = BindParameter(child, containingSymbol, ns);
            result.Add(param);
        }

        return result;
    }

    private ParameterSymbol BindParameter(GreenNode node, ISymbol containingSymbol, NamespaceSymbol ns)
    {
        // Slots: [AttributeList...], [OutKeyword|RefKeyword [ConstKeyword]], TypeReference, IdentifierToken
        var direction = ParameterDirection.In;
        GreenNode? typeNode = null;
        GreenNode? paramAttrList = null;
        string name = "";
        SyntaxToken? paramNameToken = null;

        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;

            if (slot is SyntaxToken token)
            {
                switch (token.Kind)
                {
                    case SyntaxKind.OutKeyword:
                        direction = ParameterDirection.Out;
                        break;
                    case SyntaxKind.RefKeyword:
                        direction = ParameterDirection.Ref;
                        break;
                    case SyntaxKind.ConstKeyword:
                        // ref const — upgrade Ref to RefConst
                        if (direction == ParameterDirection.Ref)
                        {
                            direction = ParameterDirection.RefConst;
                        }
                        else if (_mode == CompilationMode.Strict)
                        {
                            // Bare 'const T p' is the classic MIDL2 type qualifier. MIDL3's
                            // only use of const is the 'ref const' direction modifier above.
                            _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct(
                                "const", GetNodePosition(token)));
                        }
                        break;
                    case SyntaxKind.IdentifierToken:
                    case var k when IsTypeKeywordOrNameable(k):
                        // Last identifier-like token is the name
                        name = token.Text;
                        paramNameToken = token;
                        break;
                }
            }
            else if (IsTypeNode(slot.Kind))
            {
                typeNode = slot;
            }
            else if (slot.Kind == SyntaxKind.AttributeList)
            {
                // Parameter attributes like [optional], [in], [out], [retval]
                paramAttrList = slot;
            }
        }

        var param = new ParameterSymbol(name, containingSymbol) { Direction = direction };
        if (paramNameToken != null) param.SourcePosition = GetNodePosition(paramNameToken);

        // Extract parameter attributes
        if (paramAttrList != null)
        {
            var attrs = ExtractAttributes(paramAttrList);
            param.Attributes.AddRange(attrs);
        }

        if (typeNode != null)
        {
            param.Type = ExtractTypeRef(typeNode);
            TrackTypeRef(param.Type, ns);
            param.IsArray = param.Type.IsArray;
        }

        // MIDL1043: 'void' is not valid as a parameter type
        // In compat mode: standalone void = "no params", named void = void pointer (classic MIDL)
        // In strict mode: void as param type is always an error
        if (param.Type.Name is "void" or "Void" && _mode == CompilationMode.Strict)
        {
            _diagnostics.Add(DiagnosticDescriptors.VoidParameterType(
                typeNode != null ? GetNodePosition(typeNode) : _posMap.GetPositionOfFirst("void")));
        }

        // MIDL1013: Reserved parameter names
        // 'result' and 'operation' are reserved in methods; 'value' is reserved in constructors
        bool isConstructor = containingSymbol is ConstructorSymbol;
        if (!isConstructor && name is "result" or "operation")
        {
            _diagnostics.Add(DiagnosticDescriptors.ReservedMethodParameterName(name, _posMap.GetPositionOfFirst(name)));
        }
        else if (isConstructor && name is "value")
        {
            _diagnostics.Add(DiagnosticDescriptors.ReservedConstructorParameterName(name, _posMap.GetPositionOfFirst(name)));
        }

        return param;
    }

    #endregion

    #region Type Reference Extraction

    /// <summary>
    /// Extract a type reference from a base list entry, handling optional [default] attribute.
    /// BaseType nodes contain an attribute list followed by a type reference.
    /// </summary>
    private TypeRef ExtractBaseTypeRef(GreenNode slot, out bool isDefault)
    {
        isDefault = false;
        if (slot.Kind == SyntaxKind.BaseType)
        {
            // BaseType node: slot[0] = AttributeList, slot[1] = TypeReference
            for (int j = 0; j < slot.SlotCount; j++)
            {
                var child = slot.GetSlot(j);
                if (child == null) continue;
                if (child.Kind == SyntaxKind.AttributeList)
                {
                    isDefault = HasAttribute(child, "default");
                }
            }
            // Extract the type reference from the last non-attribute child
            for (int j = slot.SlotCount - 1; j >= 0; j--)
            {
                var child = slot.GetSlot(j);
                if (child != null && child.Kind != SyntaxKind.AttributeList)
                    return ExtractTypeRef(child);
            }
            return TypeRef.Unknown;
        }
        return ExtractTypeRef(slot);
    }

    /// <summary>Check if an attribute list node contains a specific attribute name.</summary>
    // Note: Uses existing HasAttribute method defined elsewhere in this class

    /// <summary>
    /// Spells a folded multi-word C type the way it was written: "unsigned long", not
    /// "unsignedlong". Used for alias lookup and for the strict-mode diagnostic, so both see
    /// the same name the author typed.
    /// </summary>
    internal static string JoinTypeWords(GreenNode node)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is not SyntaxToken t) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    private TypeRef ExtractTypeRef(GreenNode? node)
    {
        if (node == null) return TypeRef.Unknown;

        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
            {
                var inner = node.GetSlot(0);
                if (inner is SyntaxToken tok)
                {
                    // A multi-word C integer type — "unsigned long", "long long" — is folded
                    // into several token slots by the parser. Join them so the type is named
                    // the way it was written, which is what the alias table and diagnostics use.
                    var name = node.SlotCount > 1 ? JoinTypeWords(node) : tok.Text;
                    return new TypeRef(name) { Position = GetNodePosition(node) };
                }
                if (inner != null)
                    return ExtractTypeRef(inner);
                return TypeRef.Unknown;
            }

            case SyntaxKind.GenericName:
            {
                var nameToken = node.GetSlot(0) as SyntaxToken;
                var typeArgList = node.GetSlot(1);
                var typeArgs = ExtractTypeArguments(typeArgList);
                return new TypeRef(nameToken?.Text ?? "", typeArgs) { Position = GetNodePosition(node) };
            }

            case SyntaxKind.QualifiedName:
                return ExtractQualifiedTypeRef(node);

            case SyntaxKind.ArrayType:
            {
                var elemRef = ExtractTypeRef(node.GetSlot(0));
                return elemRef.WithArray();
            }

            case SyntaxKind.PointerType:
            {
                return ExtractTypeRef(node.GetSlot(0));
            }

            default:
                if (node is SyntaxToken t)
                    return new TypeRef(t.Text) { Position = GetNodePosition(node) };
                return TypeRef.Unknown;
        }
    }

    private TypeRef ExtractQualifiedTypeRef(GreenNode node)
    {
        System.Text.StringBuilder? sb = null;
        List<TypeRef>? typeArgs = null;

        for (int i = 0; i < node.SlotCount; i += 2)
        {
            var slot = node.GetSlot(i);
            if (slot is SyntaxToken tok)
            {
                if (sb == null)
                    sb = new System.Text.StringBuilder(tok.Text);
                else
                    sb.Append('.').Append(tok.Text);
            }
            else if (slot?.Kind == SyntaxKind.GenericName)
            {
                var gName = slot.GetSlot(0) as SyntaxToken;
                var text = gName?.Text ?? "";
                if (sb == null)
                    sb = new System.Text.StringBuilder(text);
                else
                    sb.Append('.').Append(text);
                typeArgs = ExtractTypeArguments(slot.GetSlot(1));
            }
        }

        return new TypeRef(sb?.ToString() ?? "", typeArgs ?? []) { Position = GetNodePosition(node) };
    }

    /// <summary>Get accurate source position for a green node using tree offset.</summary>
    private int GetNodePosition(GreenNode node)
    {
        // Build offset map lazily in a single O(n) pass instead of per-call O(n) tree search
        if (_nodeOffsetMap == null)
        {
            _nodeOffsetMap = new Dictionary<GreenNode, int>(ReferenceEqualityComparer.Instance);
            BuildNodeOffsetMap(_root, 0);
        }

        if (_nodeOffsetMap.TryGetValue(node, out var offset))
        {
            var firstToken = FindFirstToken(node);
            return firstToken != null
                ? offset + SumTriviaWidth(firstToken.LeadingTrivia)
                : offset;
        }
        // Fallback to text-based lookup
        var token = FindFirstToken(node);
        return token != null ? _posMap.GetPositionOfFirst(token.Text) : 0;
    }

    /// <summary>Get the source position for a symbol, using its stored SourcePosition if available.</summary>
    private int GetSymbolPosition(ISymbol symbol) =>
        symbol.SourcePosition >= 0 ? symbol.SourcePosition : _posMap.GetPositionOfFirst(symbol.Name);

    /// <summary>Precompute offsets for all nodes in the tree in a single O(n) pass.</summary>
    private void BuildNodeOffsetMap(GreenNode node, int currentOffset)
    {
        _nodeOffsetMap![node] = currentOffset;
        int childOffset = currentOffset;
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;
            BuildNodeOffsetMap(slot, childOffset);
            childOffset += slot.FullWidth;
        }
    }

    private List<TypeRef> ExtractTypeArguments(GreenNode? typeArgList)
    {
        if (typeArgList == null || typeArgList.Kind != SyntaxKind.TypeArgumentList)
            return [];

        var args = new List<TypeRef>();
        for (int i = 1; i < typeArgList.SlotCount - 1; i++)
        {
            var arg = typeArgList.GetSlot(i);
            if (arg == null || arg.Kind == SyntaxKind.CommaToken) continue;
            args.Add(ExtractTypeRef(arg));
        }
        return args;
    }

    #endregion

    #region Name Extraction

    private static string ExtractDottedName(GreenNode? node)
    {
        if (node is SyntaxToken token) return token.Text;
        if (node == null) return "";

        if (node.Kind == SyntaxKind.DottedName)
        {
            var parts = new List<string>();
            for (int i = 0; i < node.SlotCount; i += 2)
            {
                if (node.GetSlot(i) is SyntaxToken t)
                    parts.Add(t.Text);
            }
            return string.Join(".", parts);
        }

        return "";
    }

    /// <summary>
    /// Extract return type node and name from a method/property declaration.
    /// Pattern: [attrs], [mods], TypeReference, IdentifierToken, ...
    /// </summary>
    private static (GreenNode? TypeNode, string Name, SyntaxToken? NameToken) ExtractReturnTypeAndName(GreenNode node)
    {
        GreenNode? typeNode = null;
        string name = "";
        SyntaxToken? nameToken = null;

        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;

            if (IsTypeNode(slot.Kind))
            {
                typeNode = slot;
                // Next slot is the name, unless a classic calling convention sits between.
                int nameIndex = i + 1;
                if (node.GetSlot(nameIndex) is SyntaxToken { Kind: SyntaxKind.CallingConventionToken })
                    nameIndex++;

                if (node.GetSlot(nameIndex) is SyntaxToken nt)
                {
                    name = nt.Text;
                    nameToken = nt;
                }
                break;
            }
        }

        return (typeNode, name, nameToken);
    }

    private static string FindNameIdentifier(GreenNode node)
    {
        return FindNameIdentifierToken(node)?.Text ?? "";
    }

    private static SyntaxToken? FindNameIdentifierToken(GreenNode node)
    {
        // Find the last identifier-like token that isn't followed by another identifier
        // For constructors: [attrs], [mods], IdentifierToken, ParameterList, Semicolon
        // For fields: [attrs], TypeReference, IdentifierToken, Semicolon
        SyntaxToken? lastIdent = null;
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot is SyntaxToken t && IsIdentifierLike(t.Kind))
            {
                lastIdent = t;
            }
            else if (slot?.Kind == SyntaxKind.ParameterList
                     || slot?.Kind == SyntaxKind.SemicolonToken)
            {
                break;
            }
        }
        return lastIdent;
    }

    private static string FindFirstIdentifierText(GreenNode node)
    {
        return FindFirstIdentifierToken(node)?.Text ?? "";
    }

    private static SyntaxToken? FindFirstIdentifierToken(GreenNode node)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot is SyntaxToken t && IsIdentifierLike(t.Kind))
                return t;
        }
        return null;
    }

    #endregion

    #region Strict Mode Checks

#if WITH_COMPAT
    private void CheckStrictModeViolation(GreenNode node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.CoclassDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("coclass", FindKeywordPosition(node, SyntaxKind.CoclassKeyword)));
                break;
            case SyntaxKind.LibraryDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("library", FindKeywordPosition(node, SyntaxKind.LibraryKeyword)));
                break;
            case SyntaxKind.ModuleDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("module", FindKeywordPosition(node, SyntaxKind.ModuleKeyword)));
                break;
            case SyntaxKind.TypedefDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("typedef", FindKeywordPosition(node, SyntaxKind.TypedefKeyword)));
                break;
            case SyntaxKind.DispinterfaceDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("dispinterface", FindKeywordPosition(node, SyntaxKind.DispinterfaceKeyword)));
                break;
            case SyntaxKind.CppQuoteDirective:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("cpp_quote", FindKeywordPosition(node, SyntaxKind.CppQuoteKeyword)));
                break;
            case SyntaxKind.ImportlibDirective:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("importlib", FindKeywordPosition(node, SyntaxKind.ImportlibKeyword)));
                break;
            case SyntaxKind.UnionDeclaration:
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("union", FindKeywordPosition(node, SyntaxKind.UnionKeyword)));
                break;
            case SyntaxKind.FunctionDeclaration:
                // A free function at file scope — classic RPC. There is no leading keyword
                // to point at, so the diagnostic goes on the function's own name.
                _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("function", FindFunctionNamePosition(node)));
                break;
            case SyntaxKind.MethodDeclaration:
                // A trailing `const` qualifier is C++ syntax the parser accepts in compat
                // mode. `ref const` on a parameter is valid MIDL3 and lives on the
                // parameter node, so it is not reached here.
                if (HasDirectToken(node, SyntaxKind.ConstKeyword))
                    _diagnostics.Add(DiagnosticDescriptors.ClassicConstruct("const", FindKeywordPosition(node, SyntaxKind.ConstKeyword)));
                break;
        }
    }

    /// <summary>
    /// Position of a file-scope function's name — the last identifier before its parameter
    /// list, so the squiggle lands on the function rather than on its return type.
    /// </summary>
    private int FindFunctionNamePosition(GreenNode node)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i)?.Kind == SyntaxKind.ParameterList && i > 0 &&
                node.GetSlot(i - 1) is SyntaxToken name)
                return FindKeywordPosition(node, name.Kind);
        }
        return FindKeywordPosition(node, SyntaxKind.IdentifierToken);
    }

    /// <summary>True if <paramref name="node"/> has <paramref name="kind"/> as a direct token slot.</summary>
    private static bool HasDirectToken(GreenNode node, SyntaxKind kind)
    {
        for (int i = 0; i < node.SlotCount; i++)
            if (node.GetSlot(i) is SyntaxToken t && t.Kind == kind)
                return true;
        return false;
    }
#endif

    /// <summary>
    /// Absolute position of <paramref name="keywordKind"/> within <paramref name="node"/>.
    ///
    /// Uses the node offset map so each occurrence reports at its own location. A text
    /// lookup would return the first token with that text anywhere in the file, which
    /// collapsed every <c>cpp_quote</c>/<c>typedef</c> diagnostic onto the first one.
    /// </summary>
    private int FindKeywordPosition(GreenNode node, SyntaxKind keywordKind)
    {
        if (_nodeOffsetMap == null && _root != null)
        {
            _nodeOffsetMap = new Dictionary<GreenNode, int>(ReferenceEqualityComparer.Instance);
            BuildNodeOffsetMap(_root, 0);
        }

        if (_nodeOffsetMap != null && _nodeOffsetMap.TryGetValue(node, out var nodeOffset))
        {
            int offset = nodeOffset;
            for (int i = 0; i < node.SlotCount; i++)
            {
                var slot = node.GetSlot(i);
                if (slot == null) continue;
                if (slot is SyntaxToken t && t.Kind == keywordKind)
                    return offset + SumTriviaWidth(t.LeadingTrivia);
                offset += slot.FullWidth;
            }
        }

        // Node isn't in the map (e.g. synthesized): fall back to a token-map lookup.
        for (int i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is SyntaxToken token && token.Kind == keywordKind)
                return _posMap.GetPositionOfFirst(token.Text);
        }
        return _posMap.GetPositionOfFirstKind(keywordKind);
    }

    private void CheckStrictModeTokens(GreenNode node)
    {
        // Walk the tree, but use _posMap to get positions for tokens
        CheckStrictModeTokensRecursive(node, 0);
    }

    private int _strictTokenIndex = 0;

    private void CheckStrictModeTokensRecursive(GreenNode node, int depth)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;

            if (slot is SyntaxToken token)
            {
                // Skip error recovery tokens — they have empty text and would cause
                // a full scan of the token list with no match
                if (token.Kind == SyntaxKind.MissingToken) continue;

                // Find this token's position in the token stream and advance the index
                int idx = _posMap.FindToken(token.Text, _strictTokenIndex);
                if (idx < 0)
                {
                    // The token isn't in this file's token stream — it came from an
                    // imported tree. Reporting it here would paint a squiggle at
                    // offset 0 of the wrong document; the imported file's own pass
                    // reports it with a position that is actually meaningful.
                    continue;
                }
                int pos = _posMap.GetPosition(idx);
                _strictTokenIndex = idx + 1;
                CheckStrictModeToken(token, pos);
            }
            else
            {
                // A folded multi-word C type ("unsigned long") is one construct, so report it
                // once, naming the whole spelling, rather than once per word. Same shape as the
                // pointer case below.
                if (slot.Kind == SyntaxKind.TypeReference && slot.SlotCount > 1 &&
                    IsFoldedMultiWordType(slot))
                {
                    var spelling = JoinTypeWords(slot);
                    int typePos = AttributeExtractor.FindNodeOffset(_root, slot) ?? 0;
                    if (slot.GetSlot(0) is SyntaxToken firstWord)
                        typePos += SumTriviaWidth(firstWord.LeadingTrivia);

                    _diagnostics.Add(DiagnosticDescriptors.LowercaseType(
                        spelling, SuggestWinRtTypeFor(spelling), typePos));

                    // Advance past this construct's tokens so later lookups stay in step.
                    for (int t = 0; t < slot.SlotCount; t++)
                    {
                        if (slot.GetSlot(t) is not SyntaxToken word) continue;
                        int wi = _posMap.FindToken(word.Text, _strictTokenIndex);
                        if (wi >= 0) _strictTokenIndex = wi + 1;
                    }
                    continue;
                }

                // Check pointer types — report once for the whole pointer chain, don't recurse
                if (slot.Kind == SyntaxKind.PointerType)
                {
                    // Walk through nested PointerType nodes to get base type and count stars
                    var current = slot;
                    int starCount = 0;
                    while (current != null && current.Kind == SyntaxKind.PointerType)
                    {
                        starCount++;
                        current = current.GetSlot(0);
                    }
                    var typeName = CollectNodeText(current);
                    // Use green tree offset for accurate position (not text-based lookup)
                    int ptrPos = AttributeExtractor.FindNodeOffset(_root, slot) ?? 0;
                    var firstToken = FindFirstToken(slot);
                    if (firstToken != null)
                        ptrPos += SumTriviaWidth(firstToken.LeadingTrivia);
                    _diagnostics.Add(DiagnosticDescriptors.PointerType(typeName, ptrPos));
                    // Don't recurse into pointer children — already reported
                    continue;
                }

                // Check for classic [in]/[out] attributes on parameters
                if (slot.Kind == SyntaxKind.AttributeList)
                {
                    CheckStrictModeAttributes(slot);
                }

                CheckStrictModeTokensRecursive(slot, depth + 1);
            }
        }
    }

    /// <summary>
    /// True when a TypeReference holds a folded multi-word C type rather than, say, a
    /// qualified name — recognised by the spelling the parser produced.
    /// </summary>
    private static bool IsFoldedMultiWordType(GreenNode node) =>
        BuiltInTypes.MultiWordClassicTypes.ContainsKey(JoinTypeWords(node));

    /// <summary>The WinRT type to suggest instead, from the same table compat mode resolves with.</summary>
    private static string SuggestWinRtTypeFor(string spelling) =>
        BuiltInTypes.MultiWordClassicTypes.TryGetValue(spelling, out var winrt) ? winrt : "Int32";

    private void CheckStrictModeToken(SyntaxToken token, int position)
    {
        switch (token.Kind)
        {
            case SyntaxKind.HResultKeyword:
                _diagnostics.Add(DiagnosticDescriptors.HResultReturnType(position));
                break;
            case SyntaxKind.ByteKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("byte", "UInt8", position));
                break;
            case SyntaxKind.ShortKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("short", "Int16", position));
                break;
            case SyntaxKind.LongKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("long", "Int32", position));
                break;
            case SyntaxKind.IntKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("int", "Int32", position));
                break;
            case SyntaxKind.FloatKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("float", "Single", position));
                break;
            case SyntaxKind.HyperKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("hyper", "Int64", position));
                break;
            case SyntaxKind.SmallKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("small", "UInt8", position));
                break;
            case SyntaxKind.WcharTKeyword:
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("wchar_t", "Char16", position));
                break;
            case SyntaxKind.BooleanKeyword when token.Text == "boolean":
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("boolean", "Boolean", position));
                break;
            case SyntaxKind.DoubleKeyword when token.Text == "double":
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("double", "Double", position));
                break;
            case SyntaxKind.CharKeyword when token.Text == "char":
                _diagnostics.Add(DiagnosticDescriptors.LowercaseType("char", "Char16", position));
                break;
            case SyntaxKind.UnsignedKeyword:
                _diagnostics.Add(DiagnosticDescriptors.StrictModeTypeWarning("unsigned",
                    "use UInt8/UInt16/UInt32/UInt64 instead", position));
                break;
            case SyntaxKind.SignedKeyword:
                _diagnostics.Add(DiagnosticDescriptors.StrictModeTypeWarning("signed",
                    "use Int16/Int32/Int64 instead", position));
                break;
            case SyntaxKind.HandleTKeyword:
                _diagnostics.Add(DiagnosticDescriptors.StrictModeTypeWarning("handle_t",
                    "handle_t is not used in MIDL3", position));
                break;
            case SyntaxKind.ErrorStatusTKeyword:
                _diagnostics.Add(DiagnosticDescriptors.StrictModeTypeWarning("error_status_t",
                    "error_status_t is not used in MIDL3", position));
                break;
            case SyntaxKind.IdentifierToken:
                CheckStrictModeIdentifier(token, position);
                break;
        }
    }

    private void CheckStrictModeIdentifier(SyntaxToken token, int position)
    {
        switch (token.Text)
        {
            case "BSTR":
                _diagnostics.Add(DiagnosticDescriptors.ClassicMidlType("BSTR", "String", position));
                break;
            case "LPSTR":
                _diagnostics.Add(DiagnosticDescriptors.ClassicMidlType("LPSTR", "String", position));
                break;
            case "LPWSTR":
                _diagnostics.Add(DiagnosticDescriptors.ClassicMidlType("LPWSTR", "String", position));
                break;
            case "VARIANT":
                _diagnostics.Add(DiagnosticDescriptors.ClassicMidlType("VARIANT", "Object", position));
                break;
        }
    }

    // Classic MIDL (MIDL2) attributes that should produce MIDL2003 in strict mode.
    // These are recognized by BuiltinAttributes (so no MIDL1065) but are not
    // valid MIDL3 constructs — /compat enables them.
    private static readonly HashSet<string> ClassicOnlyAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "object", "local", "pointer_default", "helpstring",
            "dual", "oleautomation", "nonextensible", "hidden",
            "restricted", "source", "propget", "propput", "propputref",
            "id", "in", "out", "retval", "optional", "defaultvalue",
            "size_is", "length_is", "max_is", "iid_is", "range",
            "call_as", "endpoint", "string", "unique", "ptr", "ref",
            "context_handle", "v1_enum", "async_uuid", "callback",
            "noncreatable", "aggregatable", "appobject",
            "entry", "dllname", "custom",
            "annotation", "async", "eventadd", "eventremove",
            "switch_type", "switch_is", "case", "default_case",
            "ignore", "transmit_as", "represent_as", "wire_marshal",
            "first_is", "last_is", "byte_count", "ms_union",
            "handle", "error_status_t", "heap", "idempotent",
            "broadcast", "maybe", "message", "notify", "reflect",
            "disable_consistency_check", "implicit_handle", "explicit_handle",
            "cpp_quote", "midl_pragma",
        };

    private void CheckStrictModeAttributes(GreenNode attrList)
    {
        for (int i = 0; i < attrList.SlotCount; i++)
        {
            var slot = attrList.GetSlot(i);
            if (slot == null) continue;

            if (slot.Kind == SyntaxKind.AttributeList)
            {
                CheckStrictModeAttributes(slot);
                continue;
            }

            if (slot.Kind == SyntaxKind.Attribute)
            {
                var nameNode = slot.GetSlot(0);
                var name = nameNode is SyntaxToken t ? t.Text : ExtractDottedName(nameNode);
                if (name != null && ClassicOnlyAttributes.Contains(name))
                {
                    _diagnostics.Add(DiagnosticDescriptors.ClassicAttribute(name, _posMap.GetPositionOfFirst(name)));
                }
            }
        }
    }

    #endregion

    #region Helpers

    private static void ApplyWellKnownAttributes(ISymbol symbol, List<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            switch (attr.Name.ToLowerInvariant())
            {
                case "contract":
                    var contractName = attr.GetTypeName(0) ?? attr.GetIdentifier(0);
                    var contractVersion = (int?)attr.GetVersion(1);
                    if (symbol is RuntimeClassSymbol rc) { rc.ContractName = contractName; rc.ContractVersion = contractVersion; }
                    if (symbol is InterfaceSymbol intf) { intf.ContractName = contractName; intf.ContractVersion = contractVersion; }
                    if (symbol is EnumSymbol en) { en.ContractName = contractName; en.ContractVersion = contractVersion; }
                    if (symbol is StructSymbol st) { st.ContractName = contractName; st.ContractVersion = contractVersion; }
                    if (symbol is DelegateSymbol del) { del.ContractName = contractName; del.ContractVersion = contractVersion; }
                    if (symbol is EnumMemberSymbol em) { em.ContractName = contractName; em.ContractVersion = contractVersion; }
                    if (symbol is MethodSymbol cm) { cm.ContractName = contractName; cm.ContractVersion = contractVersion; }
                    if (symbol is PropertySymbol cp) { cp.ContractName = contractName; cp.ContractVersion = contractVersion; }
                    if (symbol is EventSymbol ce) { ce.ContractName = contractName; ce.ContractVersion = contractVersion; }
                    if (symbol is ConstructorSymbol cc) { cc.ContractName = contractName; cc.ContractVersion = contractVersion; }
                    break;
                case "contractversion":
                    if (symbol is ApiContractSymbol ac) ac.ContractVersion = (int?)attr.GetVersion(0);
                    break;
                case "version":
                    if (symbol is RuntimeClassSymbol rc2) rc2.Version = (int?)attr.GetVersion(0);
                    if (symbol is InterfaceSymbol intf2) intf2.Version = (int?)attr.GetVersion(0);
                    if (symbol is EnumSymbol enV) enV.Version = (int?)attr.GetVersion(0);
                    if (symbol is StructSymbol stV) stV.Version = (int?)attr.GetVersion(0);
                    if (symbol is DelegateSymbol delV) delV.Version = (int?)attr.GetVersion(0);
                    break;
                case "threading":
                    if (symbol is RuntimeClassSymbol rc3) rc3.ThreadingModel = attr.GetIdentifier(0);
                    break;
                case "marshaling_behavior":
                    if (symbol is RuntimeClassSymbol rc4) rc4.MarshalingBehavior = attr.GetIdentifier(0);
                    break;
                case "deprecated":
                    if (symbol is RuntimeClassSymbol rc5) rc5.DeprecatedMessage = attr.GetString(0);
                    if (symbol is InterfaceSymbol intf3) intf3.DeprecatedMessage = attr.GetString(0);
                    if (symbol is DelegateSymbol del2) del2.DeprecatedMessage = attr.GetString(0);
                    break;
                case "method_name":
                    if (symbol is MethodSymbol m) m.MethodNameOverride = attr.GetString(0);
                    break;
                case "overload":
                    if (symbol is MethodSymbol m2) m2.OverloadGroup = attr.GetString(0);
                    break;
                case "default_overload":
                    if (symbol is MethodSymbol m3) m3.IsDefaultOverload = true;
                    break;
                case "return_name":
                    if (symbol is MethodSymbol m4) m4.ReturnNameOverride = attr.GetString(0);
                    break;
                case "remote_async":
                    if (symbol is MethodSymbol m5) m5.IsRemoteAsync = true;
                    break;
                case "default_interface":
                    if (symbol is RuntimeClassSymbol rc6) rc6.IsDefaultInterface = true;
                    break;
                case "internal":
                    if (symbol is RuntimeClassSymbol rc7) rc7.IsInternal = true;
                    if (symbol is InterfaceSymbol intf4) intf4.IsExclusive = true; // internal interfaces are private
                    break;
                case "exclusiveto":
                    if (symbol is InterfaceSymbol intf_excl)
                    {
                        intf_excl.IsExclusive = true;
                        var exclTarget = attr.GetTypeName(0) ?? attr.GetIdentifier(0);
                        if (exclTarget != null)
                        {
                            if (!exclTarget.Contains('.') && intf_excl.ContainingSymbol is NamespaceSymbol exclNs && exclNs.FullName.Length > 0)
                                exclTarget = $"{exclNs.FullName}.{exclTarget}";
                            intf_excl.ExclusiveToClass = exclTarget;
                        }
                    }
                    break;
                case "experimental":
                    if (symbol is RuntimeClassSymbol rc8) rc8.IsExperimental = true;
                    if (symbol is InterfaceSymbol intf5) intf5.IsExperimental = true;
                    if (symbol is EnumSymbol en2) en2.IsExperimental = true;
                    if (symbol is StructSymbol st2) st2.IsExperimental = true;
                    if (symbol is DelegateSymbol del3) del3.IsExperimental = true;
                    break;
#if FEATURE_STAGING
                case "feature":
                {
                    var featureRef = attr.GetTypeName(0) ?? attr.GetIdentifier(0);
                    if (featureRef != null)
                    {
                        if (symbol is RuntimeClassSymbol rc9) rc9.FeatureName = featureRef;
                        if (symbol is InterfaceSymbol intf6) intf6.FeatureName = featureRef;
                        if (symbol is EnumSymbol en3) en3.FeatureName = featureRef;
                        if (symbol is StructSymbol st3) st3.FeatureName = featureRef;
                        if (symbol is DelegateSymbol del4) del4.FeatureName = featureRef;
                    }
                    break;
                }
#endif
                case "constructor_name":
                    if (symbol is RuntimeClassSymbol rcCN)
                    {
                        rcCN.FactoryInterfaceName = attr.GetString(0);
                        rcCN.FactoryInterfaceGuid = attr.GetGuid(1);
                    }
                    break;
                case "interface_name":
                    if (symbol is RuntimeClassSymbol rcIN)
                    {
                        rcIN.DefaultInterfaceName = attr.GetString(0);
                        rcIN.DefaultInterfaceGuid = attr.GetGuid(1);
                    }
                    break;
                case "static_name":
                    if (symbol is RuntimeClassSymbol rcSN)
                    {
                        rcSN.StaticsInterfaceName = attr.GetString(0);
                        rcSN.StaticsInterfaceGuid = attr.GetGuid(1);
                    }
                    break;
            }
        }
    }

    private void TrackTypeRef(TypeRef typeRef, NamespaceSymbol ns)
    {
        if (!typeRef.IsBuiltIn && typeRef.Name != "?")
        {
            // Resolution is deferred until every tree is bound, by which point the
            // importing loop's tagging is long finished. Record which file this
            // reference — and therefore its Position — came from.
            _unresolvedRefs.Add((typeRef, ns, _currentImportedFilePath));
        }
        foreach (var arg in typeRef.TypeArguments)
        {
            TrackTypeRef(arg, ns);
        }
    }

    private bool TryDefineType(NamespaceSymbol ns, string name, bool isForwardDeclaration = false) =>
        TryDefineType(ns, name, _posMap.GetPositionOfFirst(name), isForwardDeclaration);

    private bool TryDefineType(NamespaceSymbol ns, string name, int namePosition, bool isForwardDeclaration = false)
    {
        int pos = namePosition;
        var fullName = ns.FullName.Length > 0 ? string.Concat(ns.FullName, ".", name) : name;
        if (isForwardDeclaration)
            _forwardDeclaredTypes.Add(fullName);
        if (!_definedTypes.Add(fullName))
        {
            // Forward declaration of an already-defined type → silently skip
            if (isForwardDeclaration) return false;

            // Duplicate from imported file → MIDL import is idempotent
            if (_bindingImportedFile) return false;

            // Full definition of an already-defined external/built-in → allow override
            var existingSymbol = _symbolTable.LookupQualified(fullName);
            if (existingSymbol is { IsExternal: true })
                return true;

            // Full definition of a previously forward-declared type → allow
            if (_forwardDeclaredTypes.Remove(fullName))
                return true;

            _diagnostics.Add(DiagnosticDescriptors.DuplicateDefinition(name, pos));
            return false;
        }

        // Case-insensitive collision check (WinRT type names are case-insensitive)
        if (_definedTypesCaseInsensitive.TryGetValue(fullName, out var existing))
        {
            if (existing != fullName)
            {
                var existingShortName = existing.Contains('.') ? existing[(existing.LastIndexOf('.') + 1)..] : existing;
                _diagnostics.Add(DiagnosticDescriptors.CaseInsensitiveTypeCollision(name, existingShortName, pos));
            }
        }
        else
        {
            _definedTypesCaseInsensitive[fullName] = fullName;
        }

        return true;
    }

    private static int FindSlotIndex(GreenNode node, SyntaxKind kind)
    {
        return FindSlotIndex(node, kind, 0);
    }

    private static int FindSlotIndex(GreenNode node, SyntaxKind kind, int startIndex)
    {
        for (int i = startIndex; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot != null && slot.Kind == kind) return i;
        }
        return -1;
    }

    private static int FindLastSlotIndex(GreenNode node, SyntaxKind kind)
    {
        for (int i = node.SlotCount - 1; i >= 0; i--)
        {
            var slot = node.GetSlot(i);
            if (slot != null && slot.Kind == kind) return i;
        }
        return -1;
    }

    private static GreenNode? FindChild(GreenNode node, SyntaxKind kind)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot != null && slot.Kind == kind) return slot;
        }
        return null;
    }

    /// <summary>Collect the text of all tokens in a green node (ignoring trivia).</summary>
    private static string CollectNodeText(GreenNode? node)
    {
        if (node == null) return "";
        if (node is SyntaxToken t) return t.Text;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot != null) sb.Append(CollectNodeText(slot));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract attributes from a green node, computing accurate source positions
    /// from the green tree FullWidth accumulation (not text-based lookup).
    /// </summary>
    private List<AttributeData> ExtractAttributes(GreenNode? attrListNode)
    {
        if (attrListNode == null) return [];
        int offset = AttributeExtractor.FindNodeOffset(_root, attrListNode) ?? 0;
        return AttributeExtractor.Extract(attrListNode, offset);
    }

    private static bool HasModifier(GreenNode modList, SyntaxKind modKind)
    {
        for (int i = 0; i < modList.SlotCount; i++)
        {
            if (modList.GetSlot(i) is SyntaxToken t && t.Kind == modKind) return true;
        }
        return false;
    }

    private static bool HasAttribute(GreenNode attrListNode, string attrName)
    {
        for (int i = 0; i < attrListNode.SlotCount; i++)
        {
            var slot = attrListNode.GetSlot(i);
            if (slot == null) continue;

            // Could be nested: AttributeList > AttributeList > Attribute
            if (slot.Kind == SyntaxKind.AttributeList)
            {
                if (HasAttribute(slot, attrName)) return true;
                continue;
            }

            if (slot.Kind == SyntaxKind.Attribute)
            {
                var nameNode = slot.GetSlot(0);
                var name = nameNode is SyntaxToken t ? t.Text : ExtractDottedName(nameNode);
                if (string.Equals(name, attrName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private bool TryExtractUuidAttribute(GreenNode attrListNode, out Guid uuid)
    {
        uuid = default;
        for (int i = 0; i < attrListNode.SlotCount; i++)
        {
            var slot = attrListNode.GetSlot(i);
            if (slot == null) continue;

            if (slot.Kind == SyntaxKind.AttributeList)
            {
                if (TryExtractUuidAttribute(slot, out uuid)) return true;
                continue;
            }

            if (slot.Kind == SyntaxKind.Attribute)
            {
                var nameNode = slot.GetSlot(0);
                var name = nameNode is SyntaxToken t ? t.Text : ExtractDottedName(nameNode);
                if (!string.Equals(name, "uuid", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Find AttributeArgumentList, then extract first string/guid literal
                for (int j = 1; j < slot.SlotCount; j++)
                {
                    var argList = slot.GetSlot(j);
                    if (argList == null || argList.Kind != SyntaxKind.AttributeArgumentList)
                        continue;

                    var guidText = ExtractFirstLiteralText(argList);
                    if (guidText != null && Guid.TryParse(guidText, out uuid))
                        return true;
                    if (guidText != null)
                        _diagnostics.Add(DiagnosticDescriptors.UuidWrongFormat(guidText, _posMap.GetPositionOfFirst(guidText)));
                }
            }
        }
        return false;
    }

    private static string? ExtractFirstLiteralText(GreenNode node)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            if (slot == null) continue;

            if (slot is SyntaxToken token)
            {
                if (token.Kind is SyntaxKind.StringLiteralToken or SyntaxKind.GuidLiteralToken)
                {
                    var text = token.Text;
                    // Strip surrounding quotes if present
                    if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                        text = text[1..^1];
                    return text;
                }
            }

            // Recurse into AttributeArgument nodes
            var result = ExtractFirstLiteralText(slot);
            if (result != null) return result;
        }
        return null;
    }

    private static bool HasChildToken(GreenNode node, SyntaxKind kind)
    {
        for (int i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is SyntaxToken t && t.Kind == kind) return true;
        }
        return false;
    }

    private void CheckSameArityOverload(IReadOnlyList<ISymbol> existingMembers, MethodSymbol newMethod)
    {
        if (string.IsNullOrEmpty(newMethod.Name)) return;
        foreach (var member in existingMembers)
        {
            if (member is MethodSymbol existing &&
                existing.Name == newMethod.Name &&
                existing.Parameters.Count == newMethod.Parameters.Count &&
                !(existing.ContractVersion != null && newMethod.ContractVersion != null &&
                  (existing.ContractVersion != newMethod.ContractVersion ||
                   existing.ContractName != newMethod.ContractName)))
            {
                _diagnostics.Add(DiagnosticDescriptors.SameArityOverload(newMethod.Name, existing.Name, GetSymbolPosition(newMethod)));
                return;
            }
        }
    }

    private void CheckWriteOnlyProperty(PropertySymbol prop)
    {
        if (prop.HasSetter && !prop.HasGetter && !string.IsNullOrEmpty(prop.Name))
        {
            _diagnostics.Add(DiagnosticDescriptors.WriteOnlyProperty(prop.Name, GetSymbolPosition(prop)));
        }
    }

    private static bool IsTypeNode(SyntaxKind kind) =>
        kind is SyntaxKind.TypeReference or SyntaxKind.GenericName
            or SyntaxKind.QualifiedName or SyntaxKind.ArrayType
            or SyntaxKind.PointerType;

    private static bool IsIdentifierLike(SyntaxKind kind) =>
        kind == SyntaxKind.IdentifierToken || IsTypeKeywordOrNameable(kind);

    private static bool IsTypeKeywordOrNameable(SyntaxKind kind) =>
        kind is SyntaxKind.BooleanKeyword or SyntaxKind.CharKeyword
            or SyntaxKind.Int16Keyword or SyntaxKind.Int32Keyword or SyntaxKind.Int64Keyword
            or SyntaxKind.UInt8Keyword or SyntaxKind.UInt16Keyword or SyntaxKind.UInt32Keyword
            or SyntaxKind.UInt64Keyword or SyntaxKind.SingleKeyword or SyntaxKind.DoubleKeyword
            or SyntaxKind.StringKeyword or SyntaxKind.GuidKeyword or SyntaxKind.ObjectKeyword
            or SyntaxKind.VoidKeyword or SyntaxKind.HResultKeyword
            or SyntaxKind.EventKeyword or SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
            or SyntaxKind.RequiresKeyword or SyntaxKind.ContractKeyword;

    #endregion

    #region Semantic validation (Pass 3)

    private void ValidateSemantics(NamespaceSymbol ns)
    {
        // Code quality: namespace casing
        if (_emitQualityHints) ValidateNamespaceCasing(ns);

        foreach (var member in ns.Members)
        {
            switch (member)
            {
                case RuntimeClassSymbol rc when !rc.IsExternal:
                    ValidateAccessModifiers(rc);
                    ValidateBaseClass(rc);
                    ValidateEventDelegateTypes(rc.Members);
                    ValidateDuplicateMethodNames(rc.Members);
                    ValidateStaticUnsealedConflict(rc);
                    ValidateDeprecatedInheritance(rc);
                    ValidateConstructorArityPerVersion(rc);
                    ValidateMethodParameters(rc.Members);
                    ValidateExclusiveToConstraints(rc);
                    ValidateDefaultInterface(rc);
                    ValidateNonEmptyRuntimeClass(rc);
                    ValidateParameterConstraints(rc.Members, rc.Name);
                    ValidateStaticClassMembers(rc);
                    ValidatePropertyTypes(rc.Members);
                    ValidateDuplicateProperties(rc.Members, rc.Name);
                    ValidateConstructorReturnTypes(rc);
                    ValidateMemberNameNotTypeName(rc);
                    // Code quality hints
                    if (_emitQualityHints) ValidateRuntimeClassQuality(rc);
                    break;
                case InterfaceSymbol iface when !iface.IsExternal && !iface.IsSynthesized:
                    ValidateRequiredInterfaces(iface);
                    ValidateEventDelegateTypes(iface.Members);
                    ValidateInterfaceMemberNameUniqueness(iface);
                    ValidateMethodNameOverrideUniqueness(iface);
                    ValidateDefaultOverloadExactlyOne(iface.Members, iface.Name);
                    ValidateMethodParameters(iface.Members);
                    ValidateNoProtectedOnInterface(iface);
                    ValidateParameterConstraints(iface.Members, iface.Name);
                    ValidatePropertyTypes(iface.Members);
                    ValidateDuplicateProperties(iface.Members, iface.Name);
                    ValidateDuplicateOverloadNames(iface);
                    ValidateInterfaceMemberNameNotTypeName(iface);
                    // Code quality hints
                    if (_emitQualityHints) ValidateInterfaceQuality(iface);
                    break;
                case StructSymbol s when !s.IsExternal:
                    ValidateStructFieldTypes(s);
                    if (_emitQualityHints && s.DocComment == null)
                        _diagnostics.Add(DiagnosticDescriptors.MissingDocComment(s.Name, GetSymbolPosition(s)));
                    break;
                case EnumSymbol en when !en.IsExternal:
                    if (en.IsFlags && en.UnderlyingType != "UInt32")
                        _diagnostics.Add(DiagnosticDescriptors.FlagsEnumMustBeUInt32(
                            en.Name, GetSymbolPosition(en)));
                    if (en.EnumMembers.Count == 0)
                        _diagnostics.Add(DiagnosticDescriptors.EmptyEnum(
                            en.Name, GetSymbolPosition(en)));
                    if (_emitQualityHints && en.DocComment == null)
                        _diagnostics.Add(DiagnosticDescriptors.MissingDocComment(en.Name, GetSymbolPosition(en)));
                    break;
                case DelegateSymbol d when !d.IsExternal:
                    ValidateDelegateReturnType(d);
                    ValidateMethodParametersForDelegate(d);
                    if (_emitQualityHints && !d.Name.EndsWith("Handler") && !d.Name.EndsWith("Callback") && !d.Name.EndsWith("EventHandler"))
                        _diagnostics.Add(DiagnosticDescriptors.DelegateNamingConvention(d.Name, GetSymbolPosition(d)));
                    if (_emitQualityHints && d.DocComment == null)
                        _diagnostics.Add(DiagnosticDescriptors.MissingDocComment(d.Name, GetSymbolPosition(d)));
                    break;
                case AttributeTypeSymbol at when !at.IsExternal && _emitQualityHints:
                    if (!at.Name.EndsWith("Attribute", StringComparison.Ordinal))
                        _diagnostics.Add(DiagnosticDescriptors.AttributeNamingConvention(
                            at.Name, GetSymbolPosition(at)));
                    break;
            }

            // MIDL3014: Type name should be PascalCase (no underscores)
            if (_emitQualityHints && !member.IsExternal &&
                member.Name.Length > 0 &&
                (member.Name.Contains('_') || char.IsLower(member.Name[0])))
            {
                _diagnostics.Add(DiagnosticDescriptors.TypeNamePascalCase(
                    member.Name, GetSymbolPosition(member)));
            }
        }

        ValidateCircularStructReferences(ns);

        // Validate experimental type references across all types
        ValidateExperimentalReferences(ns);

#if FEATURE_STAGING
        // Validate feature-gated type references
        ValidateFeatureReferences(ns);
#endif

        foreach (var child in ns.ChildNamespaces.Values)
        {
            ValidateSemantics(child);
        }
    }

    private void ValidateAccessModifiers(RuntimeClassSymbol rc)
    {
        // Original MIDL allows protected/overridable members on sealed classes
        // (the modifiers are simply ignored at runtime). Only validate on unsealed
        // classes where the modifiers actually matter for the type system.
    }

    private void ReclassifyBaseLists(NamespaceSymbol ns)
    {
        foreach (var member in ns.Members)
        {
            switch (member)
            {
                case NamespaceSymbol childNs:
                    ReclassifyBaseLists(childNs);
                    break;
                case RuntimeClassSymbol rc when !rc.IsExternal:
                    ReclassifyClassBaseList(rc);
                    break;
            }
        }
    }

    /// <summary>
    /// Walks external interfaces (loaded via WinMdReader) and resolves the TypeRefs
    /// in their RequiredInterfaces lists. WinMdReader parses these TypeRefs from the
    /// imported InterfaceImpl rows but cannot resolve them — only the source-binding
    /// TypeResolver can do that. Runtimeclass flattening relies on these being bound
    /// so it can transitively walk required-interface chains.
    /// </summary>
    private void ResolveExternalRequiredInterfaces(TypeResolver resolver)
    {
        var throwaway = new List<Diagnostic>();
        // Resolving a required interface can lazily load another external interface whose own
        // requirements must be resolved too, so repeat until no new types appear.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var fullName in _symbolTable.GetAllDefinedTypeFullNames().ToList())
            {
                if (!visited.Add(fullName)) continue;
                added = true;
                var sym = _symbolTable.LookupDefinedType(fullName);
                if (sym is InterfaceSymbol { IsExternal: true } iface)
                {
                    var ns = iface.ContainingSymbol as NamespaceSymbol ?? _globalNamespace;
                    foreach (var tr in iface.RequiredInterfaces)
                    {
                        if (tr.ResolvedSymbol is null)
                            resolver.Resolve(tr, ns, throwaway);
                    }
                }
            }
        }
    }

    private static void ReclassifyClassBaseList(RuntimeClassSymbol rc)
    {
        // The parser captures whatever follows ':' in order; the binder conservatively
        // assigned the first entry to BaseClass. If resolution shows that entry is an
        // interface (or a delegate/other non-class), promote it into Interfaces and clear
        // BaseClass so WinMD emission defaults to System.Object.
        if (rc.BaseClass == null) return;
        var resolved = rc.BaseClass.ResolvedSymbol;
        if (resolved is null) return;
        if (resolved is RuntimeClassSymbol) return; // genuine base class

        if (resolved is InterfaceSymbol)
        {
            // Move to Interfaces list if not already present
            if (!rc.Interfaces.Any(i => ReferenceEquals(i.ResolvedSymbol, resolved)))
            {
                // Insert at the front to preserve source order (it was the first entry)
                rc.Interfaces.Insert(0, rc.BaseClass);
            }
            rc.BaseClass = null;
        }
    }

    private void ValidateBaseClass(RuntimeClassSymbol rc)
    {
        if (rc.BaseClass == null || rc.BaseClass.ResolvedSymbol == null) return;

        var baseSymbol = rc.BaseClass.ResolvedSymbol;
        if (baseSymbol is RuntimeClassSymbol baseRc)
        {
            if (!baseRc.IsUnsealed)
                _diagnostics.Add(DiagnosticDescriptors.BaseClassSealed(
                    rc.BaseClass.Name, rc.BaseClass.Position));
        }
        else if (baseSymbol is not InterfaceSymbol)
        {
            // Base resolves to a struct, enum, delegate, etc. — not valid
            _diagnostics.Add(DiagnosticDescriptors.BaseNotRuntimeClass(
                rc.BaseClass.Name, rc.BaseClass.Position));
        }
    }

    private void ValidateMemberNameNotTypeName(RuntimeClassSymbol rc)
    {
        foreach (var member in rc.Members)
        {
            var name = member.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (!string.Equals(name, rc.Name, StringComparison.Ordinal)) continue;

            var kind = member switch
            {
                PropertySymbol => "Property",
                MethodSymbol => "Method",
                EventSymbol => "Event",
                _ => null
            };
            if (kind != null)
            {
                _diagnostics.Add(DiagnosticDescriptors.MemberNameSameAsType(
                    name, rc.Name, kind, _posMap.GetPositionOfFirst(name)));
            }
        }
    }

    private void ValidateInterfaceMemberNameNotTypeName(InterfaceSymbol iface)
    {
        foreach (var member in iface.Members)
        {
            var name = member.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (!string.Equals(name, iface.Name, StringComparison.Ordinal)) continue;

            var kind = member switch
            {
                PropertySymbol => "Property",
                MethodSymbol => "Method",
                EventSymbol => "Event",
                _ => null
            };
            if (kind != null)
            {
                _diagnostics.Add(DiagnosticDescriptors.MemberNameSameAsType(
                    name, iface.Name, kind, _posMap.GetPositionOfFirst(name)));
            }
        }
    }

    private void ValidateRequiredInterfaces(InterfaceSymbol iface)
    {
        foreach (var req in iface.RequiredInterfaces)
        {
            if (req.ResolvedSymbol == null)
            {
                // Only emit if TypeResolver didn't already report MIDL1001
                // Check: if the name is not built-in and not "?", it was already reported
                // We emit our own more specific diagnostic
                _diagnostics.Add(DiagnosticDescriptors.UndefinedRequiredInterface(
                    req.Name, req.Position));
            }
            else if (req.ResolvedSymbol is RuntimeClassSymbol)
            {
                _diagnostics.Add(DiagnosticDescriptors.RuntimeClassOnRequiresList(
                    req.Name, req.Position));
            }
            else if (req.ResolvedSymbol is not InterfaceSymbol)
            {
                var kind = req.ResolvedSymbol switch
                {
                    StructSymbol => "struct",
                    DelegateSymbol => "delegate",
                    EnumSymbol => "enum",
                    ApiContractSymbol => "apicontract",
                    _ => "type"
                };
                _diagnostics.Add(DiagnosticDescriptors.NonInterfaceOnRequiresList(
                    req.Name, kind, req.Position));
            }
        }

        // Detect circular requires chains
        var visited = new HashSet<string> { iface.FullName };
        var path = new List<string> { iface.Name };
        DetectCircularRequires(iface, iface, visited, path);
    }

    private void DetectCircularRequires(InterfaceSymbol root, InterfaceSymbol current,
        HashSet<string> visited, List<string> path)
    {
        foreach (var req in current.RequiredInterfaces)
        {
            if (req.ResolvedSymbol is not InterfaceSymbol reqIface) continue;

            if (reqIface.FullName == root.FullName)
            {
                path.Add(reqIface.Name);
                var cycle = string.Join(" -> ", path);
                _diagnostics.Add(DiagnosticDescriptors.CircularRequires(
                    root.Name, cycle, root.RequiredInterfaces[0].Position));
                path.RemoveAt(path.Count - 1);
                return;
            }

            if (visited.Add(reqIface.FullName))
            {
                path.Add(reqIface.Name);
                DetectCircularRequires(root, reqIface, visited, path);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>MIDL1042: Validate that event types resolve to delegate types.</summary>
    private void ValidateEventDelegateTypes(IReadOnlyList<ISymbol> members)
    {
        foreach (var member in members)
        {
            if (member is EventSymbol evt)
            {
                // Built-in types (Int32, String, etc.) are never delegates
                if (evt.DelegateType.IsBuiltIn)
                {
                    _diagnostics.Add(DiagnosticDescriptors.EventWithNonDelegateType(
                        evt.DelegateType.Name, evt.DelegateType.Position));
                }
                // Resolved to a non-delegate symbol
                else if (evt.DelegateType.ResolvedSymbol != null
                    && evt.DelegateType.ResolvedSymbol is not DelegateSymbol)
                {
                    _diagnostics.Add(DiagnosticDescriptors.EventWithNonDelegateType(
                        evt.DelegateType.Name, evt.DelegateType.Position));
                }
            }
        }
    }

    /// <summary>MIDL1044: Warn when methods share a name AND arity but none has [method_name] / [default_overload].
    /// Overloads with different arity are unambiguous in WinRT metadata (the projection disambiguates by
    /// parameter count) and do NOT require the attributes.</summary>
    private void ValidateDuplicateMethodNames(IReadOnlyList<ISymbol> members)
    {
        // Key includes arity so overloads that differ only by parameter count are not flagged.
        var methodsByNameArityAndContract = new Dictionary<(string, int, string?, int?), List<MethodSymbol>>();
        foreach (var member in members)
        {
            if (member is MethodSymbol m && !string.IsNullOrEmpty(m.Name))
            {
                var key = (m.Name, m.Parameters.Count, m.ContractName, m.ContractVersion);
                if (!methodsByNameArityAndContract.TryGetValue(key, out var list))
                {
                    list = [];
                    methodsByNameArityAndContract[key] = list;
                }
                list.Add(m);
            }
        }

        foreach (var ((name, _, _, _), methods) in methodsByNameArityAndContract)
        {
            if (methods.Count > 1 && methods.TrueForAll(m => string.IsNullOrEmpty(m.OverloadGroup) && string.IsNullOrEmpty(m.MethodNameOverride)))
            {
                // Emit on the second method (first duplicate)
                _diagnostics.Add(DiagnosticDescriptors.DuplicateMethodNameWithoutOverload(
                    name, _posMap.GetPositionOfFirst(name)));
            }
        }
    }

    /// <summary>MIDL1045: Detect circular references between struct field types.</summary>
    private void ValidateExperimentalReferences(NamespaceSymbol ns)
    {
        foreach (var member in ns.Members)
        {
            switch (member)
            {
                case RuntimeClassSymbol rc when !rc.IsExternal && !rc.IsExperimental:
                    CheckExperimentalTypeRef(rc.Name, rc.BaseClass);
                    foreach (var iface in rc.Interfaces)
                        CheckExperimentalTypeRef(rc.Name, iface);
                    CheckExperimentalMembers(rc.Name, rc.Members);
                    break;
                case InterfaceSymbol iface when !iface.IsExternal && !iface.IsSynthesized && !iface.IsExperimental:
                    foreach (var req in iface.RequiredInterfaces)
                        CheckExperimentalTypeRef(iface.Name, req);
                    CheckExperimentalMembers(iface.Name, iface.Members);
                    break;
                case StructSymbol s when !s.IsExternal && !s.IsExperimental:
                    foreach (var field in s.Fields)
                        CheckExperimentalTypeRef(s.Name, field.Type);
                    break;
                case DelegateSymbol d when !d.IsExternal && !d.IsExperimental:
                    CheckExperimentalTypeRef(d.Name, d.ReturnType);
                    foreach (var param in d.Parameters)
                        CheckExperimentalTypeRef(d.Name, param.Type);
                    break;
            }
        }
    }

    private void CheckExperimentalMembers(string ownerName, IReadOnlyList<ISymbol> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodSymbol m:
                    CheckExperimentalTypeRef(ownerName, m.ReturnType);
                    foreach (var p in m.Parameters)
                        CheckExperimentalTypeRef(ownerName, p.Type);
                    break;
                case PropertySymbol p:
                    CheckExperimentalTypeRef(ownerName, p.Type);
                    break;
                case EventSymbol e:
                    CheckExperimentalTypeRef(ownerName, e.DelegateType);
                    break;
                case ConstructorSymbol c:
                    foreach (var p in c.Parameters)
                        CheckExperimentalTypeRef(ownerName, p.Type);
                    break;
            }
        }
    }

    private void CheckExperimentalTypeRef(string ownerName, TypeRef? typeRef)
    {
        if (typeRef == null || typeRef == TypeRef.Void || typeRef == TypeRef.Unknown) return;
        if (IsExperimentalSymbol(typeRef.ResolvedSymbol))
        {
            _diagnostics.Add(DiagnosticDescriptors.ExperimentalTypeReference(
                ownerName, typeRef.Name, typeRef.Position));
        }
        foreach (var arg in typeRef.TypeArguments)
            CheckExperimentalTypeRef(ownerName, arg);
    }

    private static bool IsExperimentalSymbol(ISymbol? symbol) =>
        symbol switch
        {
            RuntimeClassSymbol rc => rc.IsExperimental,
            InterfaceSymbol iface => iface.IsExperimental,
            EnumSymbol en => en.IsExperimental,
            StructSymbol s => s.IsExperimental,
            DelegateSymbol d => d.IsExperimental,
            _ => false
        };

#if FEATURE_STAGING
    private void ValidateFeatureReferences(NamespaceSymbol ns)
    {
        foreach (var member in ns.Members)
        {
            var ownerFeature = GetFeatureName(member);
            switch (member)
            {
                case RuntimeClassSymbol rc when !rc.IsExternal:
                    CheckFeatureTypeRef(rc.Name, ownerFeature, rc.BaseClass);
                    foreach (var iface in rc.Interfaces)
                        CheckFeatureTypeRef(rc.Name, ownerFeature, iface);
                    CheckFeatureMembers(rc.Name, ownerFeature, rc.Members);
                    break;
                case InterfaceSymbol iface when !iface.IsExternal && !iface.IsSynthesized:
                    foreach (var req in iface.RequiredInterfaces)
                        CheckFeatureTypeRef(iface.Name, ownerFeature, req);
                    CheckFeatureMembers(iface.Name, ownerFeature, iface.Members);
                    break;
                case StructSymbol s when !s.IsExternal:
                    foreach (var field in s.Fields)
                        CheckFeatureTypeRef(s.Name, ownerFeature, field.Type);
                    break;
                case DelegateSymbol d when !d.IsExternal:
                    CheckFeatureTypeRef(d.Name, ownerFeature, d.ReturnType);
                    foreach (var param in d.Parameters)
                        CheckFeatureTypeRef(d.Name, ownerFeature, param.Type);
                    break;
            }
        }
    }

    private void CheckFeatureMembers(string ownerName, string? ownerFeature, IReadOnlyList<ISymbol> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodSymbol m:
                    CheckFeatureTypeRef(ownerName, ownerFeature, m.ReturnType);
                    foreach (var p in m.Parameters)
                        CheckFeatureTypeRef(ownerName, ownerFeature, p.Type);
                    break;
                case PropertySymbol p:
                    CheckFeatureTypeRef(ownerName, ownerFeature, p.Type);
                    break;
                case EventSymbol e:
                    CheckFeatureTypeRef(ownerName, ownerFeature, e.DelegateType);
                    break;
                case ConstructorSymbol c:
                    foreach (var p in c.Parameters)
                        CheckFeatureTypeRef(ownerName, ownerFeature, p.Type);
                    break;
            }
        }
    }

    private void CheckFeatureTypeRef(string ownerName, string? ownerFeature, TypeRef? typeRef)
    {
        if (typeRef == null || typeRef == TypeRef.Void || typeRef == TypeRef.Unknown) return;
        var refFeature = GetFeatureName(typeRef.ResolvedSymbol);
        if (refFeature != null && !string.Equals(ownerFeature, refFeature, StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Add(DiagnosticDescriptors.FeatureGatedTypeReference(
                ownerName, typeRef.Name, refFeature, _posMap.GetPositionOfFirst(typeRef.Name)));
        }
        foreach (var arg in typeRef.TypeArguments)
            CheckFeatureTypeRef(ownerName, ownerFeature, arg);
    }

    private static string? GetFeatureName(ISymbol? symbol) =>
        symbol switch
        {
            RuntimeClassSymbol rc => rc.FeatureName,
            InterfaceSymbol iface => iface.FeatureName,
            EnumSymbol en => en.FeatureName,
            StructSymbol s => s.FeatureName,
            DelegateSymbol d => d.FeatureName,
            _ => null
        };
#endif

    private void ValidateCircularStructReferences(NamespaceSymbol ns)
    {
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        var reported = new HashSet<string>();
        bool hasStructs = false;

        foreach (var member in ns.Members)
        {
            if (member is StructSymbol s && !s.IsExternal)
            {
                hasStructs = true;
                DetectStructCycle(s, visiting, visited, reported);
            }
        }
        if (!hasStructs) return;
    }

    private void DetectStructCycle(StructSymbol current, HashSet<string> visiting,
        HashSet<string> visited, HashSet<string> reported)
    {
        if (visited.Contains(current.FullName)) return;
        if (!visiting.Add(current.FullName)) return; // Already visiting — cycle found elsewhere

        foreach (var field in current.Fields)
        {
            if (field.Type.ResolvedSymbol is StructSymbol fieldStruct && !fieldStruct.IsExternal)
            {
                if (visiting.Contains(fieldStruct.FullName))
                {
                    // Cycle detected
                    var key = string.Compare(current.FullName, fieldStruct.FullName, StringComparison.Ordinal) < 0
                        ? string.Concat(current.FullName, "<>", fieldStruct.FullName)
                        : string.Concat(fieldStruct.FullName, "<>", current.FullName);
                    if (reported.Add(key))
                    {
                        _diagnostics.Add(DiagnosticDescriptors.CircularStructReference(
                            current.Name, fieldStruct.Name, GetSymbolPosition(current)));
                    }
                }
                else
                {
                    DetectStructCycle(fieldStruct, visiting, visited, reported);
                }
            }
        }

        visiting.Remove(current.FullName);
        visited.Add(current.FullName);
    }


    private static readonly HashSet<string> AllowedStructFieldBuiltIns = new(StringComparer.Ordinal) { "Boolean","Char","Int16","Int32","Int64","UInt8","UInt16","UInt32","UInt64","Single","Double","String","Guid" };
    private void ValidateStructFieldTypes(StructSymbol s) { foreach (var field in s.Fields) if (!IsAllowedStructFieldType(field.Type)) _diagnostics.Add(DiagnosticDescriptors.StructFieldNotValueType(field.Name, GetSymbolPosition(field))); }
    private static bool IsAllowedStructFieldType(TypeRef typeRef)
    {
        if (AllowedStructFieldBuiltIns.Contains(typeRef.Name))
            return true;
        if (IsIReferenceType(typeRef))
            return true;
        if (typeRef.ResolvedSymbol is EnumSymbol or StructSymbol)
            return true;
        return false;
    }

    private static bool IsIReferenceType(TypeRef typeRef)
    {
        if (typeRef.TypeArguments.Count == 0)
            return false;
        if (typeRef.Name is "IReference" or "Windows.Foundation.IReference")
            return true;
        return typeRef.ResolvedSymbol is InterfaceSymbol iface
            && iface.FullName == "Windows.Foundation.IReference";
    }
    private void ValidateMethodParameters(IReadOnlyList<ISymbol> members) { foreach (var member in members) { List<ParameterSymbol>? p = null; TypeRef? rt = null; string methodName = ""; if (member is MethodSymbol m) { p = m.Parameters; rt = m.ReturnType; methodName = m.Name; } else if (member is ConstructorSymbol c) { p = c.Parameters; methodName = c.Name; } if (p != null) { foreach (var param in p) ValidateParameter(param); ValidateDuplicateParameterNames(p, methodName); } if (rt != null) { if (_mode == CompilationMode.Strict) CheckWinRtTypeCompatibility(rt); ValidateArrayReturnType(rt, _posMap.GetPositionOfFirst(methodName)); } } }
    private void ValidateMethodParametersForDelegate(DelegateSymbol d) { foreach (var param in d.Parameters) ValidateParameter(param); if (_mode == CompilationMode.Strict) CheckWinRtTypeCompatibility(d.ReturnType); ValidateArrayReturnType(d.ReturnType, GetSymbolPosition(d)); }
    private void ValidateParameter(ParameterSymbol param) { if (param.Direction == ParameterDirection.Ref && !param.IsArray) _diagnostics.Add(DiagnosticDescriptors.NoBidirectionalParams(param.Name, GetSymbolPosition(param))); if (param.IsArray && param.Type.TypeArguments.Count > 0) { bool hasArrayArg = false; foreach (var a in param.Type.TypeArguments) { if (a.IsArray) { hasArrayArg = true; break; } } if (hasArrayArg) _diagnostics.Add(DiagnosticDescriptors.NoArrayOfArrays(param.Name, GetSymbolPosition(param))); } if (param.IsArray) { if (!_arraysEnabled) { _diagnostics.Add(DiagnosticDescriptors.ArraySyntaxNotEnabled(GetSymbolPosition(param))); } else { ValidateArrayElementType(param.Type, GetSymbolPosition(param)); } } if (_mode == CompilationMode.Strict) CheckWinRtTypeCompatibility(param.Type); }
    private void ValidateArrayElementType(TypeRef arrayType, int position)
    {
        if (arrayType.ResolvedSymbol is InterfaceSymbol)
            _diagnostics.Add(DiagnosticDescriptors.ArrayOfInterfaceNotAllowed(arrayType.Name, position));
        else if (arrayType.ResolvedSymbol is DelegateSymbol)
            _diagnostics.Add(DiagnosticDescriptors.ArrayOfDelegateNotAllowed(arrayType.Name, position));
    }
    private void ValidateArrayReturnType(TypeRef returnType, int position)
    {
        if (returnType.IsArray)
        {
            if (!_arraysEnabled) _diagnostics.Add(DiagnosticDescriptors.ArraySyntaxNotEnabled(position));
            else ValidateArrayElementType(returnType, position);
        }
    }
    private static readonly HashSet<string> COnlyTypes = new(StringComparer.Ordinal) { "wchar_t","handle_t","error_status_t" };
    private void CheckWinRtTypeCompatibility(TypeRef typeRef) { if (typeRef == TypeRef.Void || typeRef == TypeRef.Unknown) return; if (COnlyTypes.Contains(typeRef.Name)) _diagnostics.Add(DiagnosticDescriptors.WinRtTypeNotAllowed(typeRef.Name, typeRef.Position)); }
    private void ValidateDelegateReturnType(DelegateSymbol d) { /* WinRT delegates can return any type */ }
    private void ValidateInterfaceMemberNameUniqueness(InterfaceSymbol iface) { var seen = new Dictionary<string, ISymbol>(StringComparer.Ordinal); foreach (var member in iface.Members) { var name = member.Name; if (string.IsNullOrEmpty(name)) continue; if (member is MethodSymbol m && !string.IsNullOrEmpty(m.OverloadGroup)) continue; if (seen.TryGetValue(name, out var existing)) { if (existing is MethodSymbol em && !string.IsNullOrEmpty(em.OverloadGroup)) continue; _diagnostics.Add(DiagnosticDescriptors.InterfaceMemberNameCollision(name, iface.Name, GetSymbolPosition(member))); } else seen[name] = member; } }
    // Overload sets are keyed by the metadata name (the [overload("X")] value, else the method name), as
    // midl.exe does for MIDL4015: the method name is the unique ABI name that goes to OverloadAttribute.
    private void ValidateDefaultOverloadExactlyOne(IReadOnlyList<ISymbol> members, string cn) { var g = new Dictionary<(string,int,string?,int?), List<MethodSymbol>>(); foreach (var member in members) if (member is MethodSymbol m && !string.IsNullOrEmpty(m.Name)) { var k = (m.OverloadGroup ?? m.Name, m.Parameters.Count, m.ContractName, m.ContractVersion); if (!g.TryGetValue(k, out var l)) { l = []; g[k] = l; } l.Add(m); } foreach (var (k, ms) in g) { if (ms.Count < 2) continue; int dc = 0; foreach (var m in ms) { if (m.IsDefaultOverload) dc++; } if (dc == 0) _diagnostics.Add(DiagnosticDescriptors.SameArityNeedsDefaultOverload(k.Item1, k.Item2, GetSymbolPosition(ms[0]))); else if (dc > 1) _diagnostics.Add(DiagnosticDescriptors.DefaultOverloadNotExactlyOne(k.Item1, k.Item2, dc, GetSymbolPosition(ms[0]))); } }
    private void ValidateDeprecatedInheritance(RuntimeClassSymbol rc) { if (rc.BaseClass?.ResolvedSymbol is not RuntimeClassSymbol baseRc) return; if (string.IsNullOrEmpty(baseRc.DeprecatedMessage)) return; if (!string.IsNullOrEmpty(rc.DeprecatedMessage)) return; _diagnostics.Add(DiagnosticDescriptors.DeprecatedBaseClassInheritance(rc.Name, baseRc.Name, GetSymbolPosition(rc))); }
    private void ValidateMethodNameOverrideUniqueness(InterfaceSymbol iface) { var seen = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var member in iface.Members) { if (member is not MethodSymbol m || string.IsNullOrEmpty(m.MethodNameOverride)) continue; if (!seen.TryAdd(m.MethodNameOverride, m.Name)) _diagnostics.Add(DiagnosticDescriptors.DuplicateMethodNameOverride(m.MethodNameOverride, iface.Name, GetSymbolPosition(m))); } }
    private void ValidateConstructorArityPerVersion(RuntimeClassSymbol rc) { List<ConstructorSymbol>? ctors = null; foreach (var member in rc.Members) { if (member is ConstructorSymbol ctor) { ctors ??= new List<ConstructorSymbol>(); ctors.Add(ctor); } } if (ctors == null || ctors.Count < 2) return; var g = new Dictionary<(int,int?), List<ConstructorSymbol>>(); foreach (var ctor in ctors) { var k = (ctor.Parameters.Count, ctor.ContractVersion); if (!g.TryGetValue(k, out var l)) { l = []; g[k] = l; } l.Add(ctor); } foreach (var (k, l) in g) if (l.Count > 1) _diagnostics.Add(DiagnosticDescriptors.ConstructorArityConflict(rc.Name, k.Item1, GetSymbolPosition(rc))); }
    private void ValidateStaticUnsealedConflict(RuntimeClassSymbol rc) { if (rc.IsStatic && rc.IsUnsealed) _diagnostics.Add(DiagnosticDescriptors.StaticUnsealedConflict(rc.Name, GetSymbolPosition(rc))); }

    private void ValidateDuplicateParameterNames(List<ParameterSymbol> parameters, string methodName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in parameters)
        {
            if (!string.IsNullOrEmpty(param.Name) && !seen.Add(param.Name))
                _diagnostics.Add(DiagnosticDescriptors.DuplicateParameterName(param.Name, methodName, GetSymbolPosition(param)));
        }
    }

    private void ValidateStaticClassMembers(RuntimeClassSymbol rc)
    {
        if (!rc.IsStatic) return;
        foreach (var member in rc.Members)
        {
            switch (member)
            {
                case MethodSymbol m when !m.IsStatic:
                    _diagnostics.Add(DiagnosticDescriptors.StaticClassInstanceMethod(m.Name, rc.Name, GetSymbolPosition(m)));
                    break;
                case ConstructorSymbol c:
                    _diagnostics.Add(DiagnosticDescriptors.StaticClassInstanceMethod(c.Name, rc.Name, GetSymbolPosition(c)));
                    break;
                case PropertySymbol p when !p.IsStatic:
                    _diagnostics.Add(DiagnosticDescriptors.StaticClassInstanceMethod(p.Name, rc.Name, GetSymbolPosition(p)));
                    break;
                case EventSymbol e when !e.IsStatic:
                    _diagnostics.Add(DiagnosticDescriptors.StaticClassInstanceMethod(e.Name, rc.Name, GetSymbolPosition(e)));
                    break;
            }
        }
    }

    private void ValidatePropertyTypes(IReadOnlyList<ISymbol> members)
    {
        foreach (var member in members)
        {
            if (member is PropertySymbol prop && prop.Type.Name is "void" or "Void")
                _diagnostics.Add(DiagnosticDescriptors.PropertyVoidType(prop.Name, GetSymbolPosition(prop)));
        }
    }

    private void ValidateDuplicateProperties(IReadOnlyList<ISymbol> members, string typeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is PropertySymbol prop && !string.IsNullOrEmpty(prop.Name) && !seen.Add(prop.Name))
                _diagnostics.Add(DiagnosticDescriptors.DuplicatePropertyName(prop.Name, typeName, GetSymbolPosition(prop)));
        }
    }

    private void ValidateConstructorReturnTypes(RuntimeClassSymbol rc)
    {
        foreach (var member in rc.Members)
        {
            if (member is MethodSymbol m && string.Equals(m.Name, rc.Name, StringComparison.Ordinal))
                _diagnostics.Add(DiagnosticDescriptors.ConstructorWithReturnType(m.Name, GetSymbolPosition(m)));
        }
    }

    private void ValidateDuplicateOverloadNames(InterfaceSymbol iface)
    {
        // [overload("X")] names the overload set (the projected MethodDef name) and is shared by its
        // methods; each method's own name is the unique ABI name written to OverloadAttribute. midl.exe
        // rejects two [overload] methods with the same name (MIDL2096), never a shared [overload] value.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in iface.Members)
        {
            if (member is MethodSymbol m && !string.IsNullOrEmpty(m.OverloadGroup) && !seen.Add(m.Name))
                _diagnostics.Add(DiagnosticDescriptors.DuplicateOverloadName(m.Name, iface.Name, GetSymbolPosition(m)));
        }
    }

        private static bool IsTypeDeclaration(SyntaxKind kind) => kind is
        SyntaxKind.RuntimeClassDeclaration or SyntaxKind.InterfaceDeclaration
        or SyntaxKind.DelegateDeclaration or SyntaxKind.StructDeclaration
        or SyntaxKind.EnumDeclaration or SyntaxKind.ApiContractDeclaration
        or SyntaxKind.AttributeDeclaration;

    private static string? ExtractTypeName(GreenNode node)
    {
        // Type declarations typically have: [attrs] keyword Name { ... }
        // Find the identifier after the keyword
        for (int i = 0; i < node.SlotCount - 1; i++)
        {
            var slot = node.GetSlot(i);
            if (slot is SyntaxToken { Kind: SyntaxKind.RuntimeclassKeyword or SyntaxKind.InterfaceKeyword
                or SyntaxKind.DelegateKeyword or SyntaxKind.StructKeyword
                or SyntaxKind.EnumKeyword or SyntaxKind.ApicontractKeyword
                or SyntaxKind.AttributeKeyword })
            {
                var next = node.GetSlot(i + 1) as SyntaxToken;
                if (next is { Kind: SyntaxKind.IdentifierToken })
                    return next.Text;
            }
        }
        return null;
    }

    #endregion

    #region Interface member validation

    private void ValidateNoProtectedOnInterface(InterfaceSymbol iface)
    {
        foreach (var m in iface.Members)
        {
            bool isProtected = m switch
            {
                MethodSymbol ms => ms.IsProtected,
                PropertySymbol ps => ps.IsProtected || ps.IsGetterProtected || ps.IsSetterProtected,
                EventSymbol es => es.IsProtected,
                _ => false
            };

            if (isProtected)
            {
                _diagnostics.Add(DiagnosticDescriptors.ProtectedMemberOnInterface(
                    m.Name, iface.Name, GetSymbolPosition(m)));
            }
        }
    }

    #endregion

    #region ExclusiveTo and Default Interface validation

    private void ValidateExclusiveToConstraints(RuntimeClassSymbol rc)
    {
        foreach (var iface in rc.ImplementedInterfaces)
        {
            if (iface.IsExclusive && iface.ExclusiveToClass != null &&
                !string.Equals(iface.ExclusiveToClass, rc.FullName, StringComparison.Ordinal))
            {
                _diagnostics.Add(DiagnosticDescriptors.ExclusiveToViolation(
                    iface.Name, iface.ExclusiveToClass, rc.Name,
                    GetSymbolPosition(iface)));
            }
        }
    }

    private void ValidateDefaultInterface(RuntimeClassSymbol rc)
    {
        // Count non-synthesized member interfaces
        int nonSynthCount = 0;
        foreach (var iface in rc.ImplementedInterfaces)
        {
            if (!iface.IsSynthesized) nonSynthCount++;
        }

        if (nonSynthCount <= 1) return;

        // InterfaceSynthesizer auto-picks a default, but warn if none was explicitly marked
        if (rc.DefaultInterface == null)
        {
            _diagnostics.Add(DiagnosticDescriptors.MissingDefaultInterface(
                rc.Name, nonSynthCount,
                GetSymbolPosition(rc)));
        }
    }

    private void ValidateNonEmptyRuntimeClass(RuntimeClassSymbol rc)
    {
        // Static classes don't need member interfaces
        if (rc.IsStatic) return;
        // [default_interface] explicitly marks a class as having an interface
        if (rc.IsDefaultInterface) return;
        // Classes with a base class inherit its interface
        if (rc.BaseClass != null) return;
        // Classes with explicit interfaces are fine
        if (rc.Interfaces.Count > 0) return;
        // Classes with non-constructor members get a synthesized default interface
        bool hasNonConstructorMember = false;
        foreach (var m in rc.Members) { if (m is not ConstructorSymbol) { hasNonConstructorMember = true; break; } }
        if (hasNonConstructorMember) return;

        _diagnostics.Add(DiagnosticDescriptors.EmptyRuntimeClassNoInterface(
            rc.Name, GetSymbolPosition(rc)));
    }

    private void ValidateParameterConstraints(IReadOnlyList<ISymbol> members, string ownerName)
    {
        foreach (var member in members)
        {
            List<ParameterSymbol>? parameters = null;
            string methodName = ownerName;
            if (member is MethodSymbol m) { parameters = m.Parameters; methodName = m.Name; }
            else if (member is ConstructorSymbol c) { parameters = c.Parameters; methodName = c.Name; }
            if (parameters == null) continue;

            foreach (var param in parameters)
            {
                // Check for [optional] attribute on parameters
                bool hasOptional = false;
                foreach (var a in param.Attributes) { if (a.Name.Equals("optional", StringComparison.OrdinalIgnoreCase)) { hasOptional = true; break; } }
                if (hasOptional)
                {
                    _diagnostics.Add(DiagnosticDescriptors.OptionalParamNotAllowed(
                        param.Name, methodName, GetSymbolPosition(param)));
                }

                // Check parameter class has default interface
                if (param.Type.ResolvedSymbol is RuntimeClassSymbol paramRc && !paramRc.IsExternal)
                {
                    bool hasDefault = paramRc.IsDefaultInterface
                        || paramRc.DefaultInterface != null
                        || paramRc.Members.Count > 0;
                    if (!hasDefault)
                    {
                        foreach (var i in paramRc.Interfaces) { if (i.IsDefaultInterface) { hasDefault = true; break; } }
                    }
                    if (!hasDefault && !paramRc.IsStatic)
                    {
                        // Point squiggle at the type reference, not the parameter name
                        int typePos = param.Type.Position > 0 ? param.Type.Position : GetSymbolPosition(param);
                        int typeLen = Math.Max(param.Type.Name.Length, 1);
                        _diagnostics.Add(new Diagnostics.Diagnostic(
                            Diagnostics.DiagnosticSeverity.Error, "MIDL1077",
                            $"Parameter '{param.Name}' in '{methodName}': runtimeclass '{paramRc.Name}' has no default interface and cannot be used as a parameter type.",
                            typePos, typeLen));
                    }
                }
            }
        }
    }

    #endregion

    #region Code Quality Hints

    private void ValidateNamespaceCasing(NamespaceSymbol ns)
    {
        if (string.IsNullOrEmpty(ns.Name) || ns.IsExternal) return;
        foreach (var segment in ns.Name.Split('.'))
        {
            if (segment.Length > 0 && char.IsLower(segment[0]))
            {
                _diagnostics.Add(DiagnosticDescriptors.NamespaceCasingConvention(
                    segment, _posMap.GetPositionOfFirst(segment)));
            }
        }
    }

    private void ValidateInterfaceQuality(InterfaceSymbol iface)
    {
        // MIDL3020: Missing doc comment (skip synthesized interfaces)
        if (iface.DocComment == null && !iface.IsSynthesized)
        {
            _diagnostics.Add(DiagnosticDescriptors.MissingDocComment(
                iface.Name, GetSymbolPosition(iface)));
        }

        // MIDL3001: Interface should follow I + PascalCase convention
        if (iface.Name.Length >= 2 && iface.Name[0] == 'I' && char.IsUpper(iface.Name[1]))
        {
            // Good: IFoo, IBar, IStringable — no warning
        }
        else if (iface.Name.Length > 0 && iface.Name[0] != 'I')
        {
            // Missing I prefix: Foo → IFoo
            _diagnostics.Add(DiagnosticDescriptors.InterfaceNamingConvention(
                iface.Name, $"I{iface.Name}", GetSymbolPosition(iface)));
        }
        else if (iface.Name.Length > 1 && iface.Name[0] == 'I' && char.IsLower(iface.Name[1]))
        {
            // Has I but next char is lowercase: Ibar → IBar
            var suggestion = "I" + char.ToUpperInvariant(iface.Name[1]) + iface.Name[2..];
            _diagnostics.Add(DiagnosticDescriptors.InterfaceNamingConvention(
                iface.Name, suggestion, GetSymbolPosition(iface)));
        }

        // MIDL3003: Empty interface
        if (iface.Members.Count == 0)
        {
            _diagnostics.Add(DiagnosticDescriptors.EmptyInterface(
                iface.Name, GetSymbolPosition(iface)));
        }

        // MIDL3016: Get+set property can use shorthand (skip if already shorthand)
        foreach (var prop in iface.Members.OfType<PropertySymbol>())
        {
            if (prop.HasGetter && prop.HasSetter && !prop.IsShorthand &&
                !prop.IsGetterProtected && !prop.IsSetterProtected)
            {
                _diagnostics.Add(DiagnosticDescriptors.PropertyCanUseShorthand(
                    prop.Name, iface.Name, GetSymbolPosition(prop)));
            }
        }

        // MIDL3013: Async method should end with 'Async'
        foreach (var method in iface.Members.OfType<MethodSymbol>())
        {
            if (method.ReturnType.Name.Contains("IAsync") && !method.Name.EndsWith("Async"))
            {
                _diagnostics.Add(DiagnosticDescriptors.AsyncMethodNamingConvention(
                    method.Name, iface.Name, GetSymbolPosition(method)));
            }
        }

        // MIDL3015: Member name should be PascalCase
        foreach (var m in iface.Members)
        {
            var memberName = m switch
            {
                MethodSymbol ms => ms.Name,
                PropertySymbol ps => ps.Name,
                _ => null
            };
            if (memberName is { Length: > 0 } && char.IsLower(memberName[0]))
            {
                _diagnostics.Add(DiagnosticDescriptors.MemberNamePascalCase(
                    memberName, iface.Name, _posMap.GetPositionOfFirst(memberName)));
            }
        }
    }

    private void ValidateRuntimeClassQuality(RuntimeClassSymbol rc)
    {
        // MIDL3020: Missing doc comment
        if (rc.DocComment == null)
        {
            _diagnostics.Add(DiagnosticDescriptors.MissingDocComment(
                rc.Name, GetSymbolPosition(rc)));
        }

        bool hasMembers = rc.Members.Count > 0;
        bool hasConstructors = false;
        foreach (var m in rc.Members) { if (m is ConstructorSymbol) { hasConstructors = true; break; } }
        bool hasActivatable = false;
        foreach (var a in rc.Attributes) { if (a.Name.Equals("activatable", StringComparison.OrdinalIgnoreCase)) { hasActivatable = true; break; } }

        // MIDL3002: Empty runtimeclass
        if (!hasMembers && !rc.IsStatic)
        {
            _diagnostics.Add(DiagnosticDescriptors.EmptyRuntimeClass(
                rc.Name, GetSymbolPosition(rc)));
        }

        // MIDL3005: No constructor and not activatable
        // Suppress for types likely obtained from elsewhere (EventArgs, etc.)
        if (!hasConstructors && !hasActivatable && !rc.IsStatic && hasMembers)
        {
            bool isEventArgs = rc.Name.EndsWith("EventArgs", StringComparison.Ordinal);
            bool hasDefaultInterface = rc.IsDefaultInterface;
            bool allStatic = true;
            foreach (var m in rc.Members) { if (m is not ConstructorSymbol && !IsStaticMember(m)) { allStatic = false; break; } }

            // Don't warn for EventArgs types (produced by event handlers, not constructed)
            // Don't warn for all-static classes (MIDL3019 handles those)
            // Don't warn for [default_interface] classes (explicitly opted in)
            // Don't warn if // [obtained-from:...] comment present
            if (!isEventArgs && !allStatic && !hasDefaultInterface && !HasObtainedFromComment(rc))
            {
                _diagnostics.Add(DiagnosticDescriptors.RuntimeClassNoConstructor(
                    rc.Name, GetSymbolPosition(rc)));
            }
        }

        // MIDL3019: All members are static — suggest 'static runtimeclass'
        if (hasMembers && !rc.IsStatic && !rc.IsDefaultInterface)
        {
            bool allStatic = true;
            bool hasNonCtorMembers = false;
            foreach (var m in rc.Members)
            {
                if (m is ConstructorSymbol) continue;
                hasNonCtorMembers = true;
                if (!IsStaticMember(m)) { allStatic = false; break; }
            }
            if (allStatic && hasNonCtorMembers && !hasConstructors)
            {
                _diagnostics.Add(DiagnosticDescriptors.ClassCouldBeStatic(
                    rc.Name, GetSymbolPosition(rc)));
            }
        }

        // MIDL3007: Setter-only property
        // MIDL3016: Get+set property can use shorthand
        foreach (var prop in rc.Members.OfType<PropertySymbol>())
        {
            if (prop.HasSetter && !prop.HasGetter)
            {
                _diagnostics.Add(DiagnosticDescriptors.PropertySetterOnly(
                    prop.Name, rc.Name, GetSymbolPosition(prop)));
            }
            else if (prop.HasGetter && prop.HasSetter && !prop.IsShorthand &&
                     !prop.IsProtected && !prop.IsOverridable &&
                     !prop.IsGetterProtected && !prop.IsSetterProtected)
            {
                _diagnostics.Add(DiagnosticDescriptors.PropertyCanUseShorthand(
                    prop.Name, rc.Name, GetSymbolPosition(prop)));
            }
        }

        // MIDL3008: Method name clashes with type name
        foreach (var method in rc.Members.OfType<MethodSymbol>())
        {
            if (string.Equals(method.Name, rc.Name, StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add(DiagnosticDescriptors.MethodNameClash(
                    method.Name, rc.Name, GetSymbolPosition(method)));
            }
        }

        // MIDL3013: Async method should end with 'Async'
        foreach (var method in rc.Members.OfType<MethodSymbol>())
        {
            if (method.ReturnType.Name.Contains("IAsync") && !method.Name.EndsWith("Async"))
            {
                _diagnostics.Add(DiagnosticDescriptors.AsyncMethodNamingConvention(
                    method.Name, rc.Name, GetSymbolPosition(method)));
            }
        }

        // MIDL3015: Member name should be PascalCase
        foreach (var m in rc.Members)
        {
            var memberName = m switch
            {
                MethodSymbol ms => ms.Name,
                PropertySymbol ps => ps.Name,
                _ => null
            };
            if (memberName is { Length: > 0 } && char.IsLower(memberName[0]))
            {
                _diagnostics.Add(DiagnosticDescriptors.MemberNamePascalCase(
                    memberName, rc.Name, _posMap.GetPositionOfFirst(memberName)));
            }
        }

        // MIDL3017: Redundant default attributes
        ValidateRedundantDefaults(rc.Attributes, rc.ThreadingModel, rc.MarshalingBehavior);

        // MIDL3018: Obsolete/dead attributes
        ValidateObsoleteAttributes(rc.Attributes);
    }

    private static readonly HashSet<string> ObsoleteAttributes =
        new(StringComparer.OrdinalIgnoreCase) { "dualapipartition", "allowforweb", "webhosthidden" };

    private void ValidateRedundantDefaults(List<AttributeData> attributes, string? threadingModel, string? marshalingBehavior)
    {
        foreach (var attr in attributes)
        {
            if (attr.Name.Equals("threading", StringComparison.OrdinalIgnoreCase) &&
                threadingModel != null &&
                threadingModel.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                // [threading(both)] is the default — can be omitted
                int len = attr.Arguments.Count > 0 && attr.Arguments[0].Length > 0
                    ? attr.Arguments[0].Position + attr.Arguments[0].Length - attr.Position + 1 // include closing paren
                    : attr.Name.Length;
                _diagnostics.Add(DiagnosticDescriptors.RedundantDefaultAttribute(
                    "threading", "both", attr.Position, len));
            }
            else if (attr.Name.Equals("marshaling_behavior", StringComparison.OrdinalIgnoreCase) &&
                     marshalingBehavior != null &&
                     marshalingBehavior.Equals("agile", StringComparison.OrdinalIgnoreCase))
            {
                int len = attr.Arguments.Count > 0 && attr.Arguments[0].Length > 0
                    ? attr.Arguments[0].Position + attr.Arguments[0].Length - attr.Position + 1
                    : attr.Name.Length;
                _diagnostics.Add(DiagnosticDescriptors.RedundantDefaultAttribute(
                    "marshaling_behavior", "agile", attr.Position, len));
            }
        }
    }

    private void ValidateObsoleteAttributes(List<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (ObsoleteAttributes.Contains(attr.Name))
            {
                _diagnostics.Add(DiagnosticDescriptors.ObsoleteAttribute(
                    attr.Name, attr.Position, attr.Name.Length));
            }
        }
    }

    #endregion

    private static bool IsStaticMember(ISymbol member) => member switch
    {
        MethodSymbol m => m.IsStatic,
        PropertySymbol p => p.IsStatic,
        EventSymbol e => e.IsStatic,
        _ => false
    };

    /// <summary>
    /// Check if a symbol has a preceding comment containing "[obtained-from:" 
    /// which suppresses the MIDL3005 "not activatable" diagnostic.
    /// Scans tokens near the symbol's position for the comment in leading trivia.
    /// </summary>
    private bool HasObtainedFromComment(ISymbol symbol)
    {
        int pos = GetSymbolPosition(symbol);
        if (pos <= 0) return false;

        // Scan tokens near this position — the comment may be trivia on the 
        // runtimeclass keyword, an attribute bracket, or the name token itself.
        var tokens = _posMap.Tokens;
        int offset = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            int tokenFullEnd = offset + token.FullWidth;

            // Check tokens in a window around the target position
            if (tokenFullEnd > pos - 200 && offset < pos + 50)
            {
                foreach (var trivia in token.LeadingTrivia)
                {
                    if (trivia.Kind == SyntaxKind.SingleLineCommentTrivia &&
                        trivia is Syntax.SyntaxTrivia triviaNode &&
                        triviaNode.Text.Contains("[obtained-from:", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (offset > pos + 50) break;
            offset = tokenFullEnd;
        }
        return false;
    }

    private static int SumTriviaWidth(GreenNode[] trivia)
    {
        int width = 0;
        foreach (var t in trivia) width += t.FullWidth;
        return width;
    }
}
