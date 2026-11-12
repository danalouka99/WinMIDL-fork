// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Midl.Analysis.Symbols;

/// <summary>
/// Represents a reference to a type, possibly unresolved.
/// </summary>
public class TypeRef
{
    public static readonly TypeRef Unknown = new("?");
    public static readonly TypeRef Void = new("void") { IsBuiltIn = true };

    public string Name { get; }
    public IReadOnlyList<TypeRef> TypeArguments { get; }
    public bool IsArray { get; }
    public int ArrayRank { get; init; } = 1;
    public int? ArrayLength { get; init; }
    public bool IsBuiltIn { get; init; }
    public ISymbol? ResolvedSymbol { get; set; }
    public int Position { get; init; }

    /// <summary>
    /// True when a signature read from a referenced WinMD encodes this type as a value type
    /// (ELEMENT_TYPE_VALUETYPE). Lets unresolved external structs and enums keep their
    /// valuetype encoding when re-emitted (for example in flattened interface members).
    /// </summary>
    public bool IsValueType { get; init; }

    /// <summary>When reading from WinMD InterfaceImpl rows, indicates this is a [default] interface.</summary>
    public bool IsDefaultInterface { get; set; }
    /// <summary>When reading from WinMD InterfaceImpl rows, indicates this is a [protected] interface.</summary>
    public bool IsProtectedInterface { get; set; }
    /// <summary>When reading from WinMD InterfaceImpl rows, indicates this is an [overridable] interface.</summary>
    public bool IsOverridableInterface { get; set; }

    public TypeRef(string name, IReadOnlyList<TypeRef>? typeArguments = null, bool isArray = false)
    {
        Name = name;
        TypeArguments = typeArguments ?? [];
        IsArray = isArray;
        IsBuiltIn = IsBuiltInType(name);
    }

    public TypeRef WithArray() => new(Name, TypeArguments, isArray: true)
    {
        IsBuiltIn = IsBuiltIn,
        IsValueType = IsValueType,
        Position = Position,
        ArrayRank = 1,
        ArrayLength = null,
    };

    public string DisplayName
    {
        get
        {
            var baseName = TypeArguments.Count > 0
                ? $"{Name}<{string.Join(", ", TypeArguments.Select(a => a.DisplayName))}>"
                : Name;
            return IsArray ? $"{baseName}[]" : baseName;
        }
    }

    internal static bool IsBuiltInType(string name) => name switch
    {
        "Boolean" or "Char" or "Char16" or "Int16" or "Int32" or "Int64"
            or "UInt8" or "UInt16" or "UInt32" or "UInt64"
            or "Single" or "Double" or "String" or "Guid"
            or "Object" or "void" or "HRESULT"
            or "wchar_t" or "handle_t" or "error_status_t"
            or "byte" or "short" or "long" or "int" or "float"
            or "double" or "boolean" or "char"
            or "hyper" or "unsigned" or "signed" or "small" => true,
        _ => false,
    };
}
