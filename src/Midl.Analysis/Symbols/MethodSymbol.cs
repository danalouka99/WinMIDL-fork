// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Midl.Analysis.Symbols;

public class MethodSymbol : ISymbol
{
    public string Name { get; }
    public SymbolKind Kind => SymbolKind.Method;
    public ISymbol? ContainingSymbol { get; }

    public string FullName =>
        ContainingSymbol is { FullName.Length: > 0 }
            ? $"{ContainingSymbol.FullName}.{Name}"
            : Name;

    public TypeRef ReturnType { get; set; } = TypeRef.Void;
    public List<ParameterSymbol> Parameters { get; } = [];
    public bool IsStatic { get; set; }
    public bool IsProtected { get; set; }
    public bool IsOverridable { get; set; }
    public bool IsExternal { get; set; }
    public string? SourceWinMdPath { get; set; }
    public string? SourceFile { get; set; }
    public int SourcePosition { get; set; } = -1;
    public DocComment? DocComment { get; set; }

    // Extracted attribute data
    public List<AttributeData> Attributes { get; } = new();
    public string? MethodNameOverride { get; set; }
    public string? OverloadGroup { get; set; }
    public bool IsDefaultOverload { get; set; }
    /// <summary>Set when the method shares its name with other methods of its interface (midl.exe overload set).</summary>
    public bool IsInOverloadSet { get; set; }
    /// <summary>
    /// The method's name in the ABI (C++ header, IID seed, OverloadAttribute): the [method_name] value,
    /// else its own name. With [overload("X")], X is the projected (MethodDef) name instead.
    /// </summary>
    public string AbiName => MethodNameOverride ?? Name;
    /// <summary>The value midl.exe writes to OverloadAttribute, or null when it writes none.</summary>
    public string? OverloadAttributeValue =>
        OverloadGroup != null || MethodNameOverride != null || IsInOverloadSet ? AbiName : null;
    public string? ReturnNameOverride { get; set; }
    /// <summary>Name of the return value's Param row, for methods read from a referenced WinMD.</summary>
    public string? ReturnParameterName { get; set; }
    public bool IsRemoteAsync { get; set; }
    public string? ContractName { get; set; }
    public int? ContractVersion { get; set; }

    public MethodSymbol(string name, ISymbol? containingSymbol)
    {
        Name = name;
        ContainingSymbol = containingSymbol;
    }
}
