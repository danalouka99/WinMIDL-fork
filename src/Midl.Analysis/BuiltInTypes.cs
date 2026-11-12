// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis.Symbols;

namespace Midl.Analysis;

/// <summary>
/// Registers built-in WinRT types that MIDL knows about intrinsically.
/// These are available without importing any .winmd files.
/// Built-in types use detached namespace hierarchies so they don't
/// appear in the compilation's output namespace tree.
/// </summary>
public static class BuiltInTypes
{
    internal const string BuiltInSource = "<built-in>";

    // Detached namespace cache so multiple types in the same namespace share one instance
    // ThreadStatic to avoid races when parallel tests call Register concurrently
    [ThreadStatic]
    private static Dictionary<string, NamespaceSymbol>? _detachedNamespaces;

    /// <summary>
    /// Multi-word C integer types and the WinRT type each corresponds to.
    ///
    /// Classic MIDL and C spell these with more than one word; MIDL3 has no such spelling.
    /// The table therefore serves two purposes and must stay one table: compat mode registers
    /// it as aliases so the types resolve, and strict mode reads it to say which WinRT type to
    /// use instead. Two copies would eventually disagree, and the disagreement would surface
    /// as a suggestion that does not match what the compiler actually accepts.
    ///
    /// <c>signed char</c> is an 8-bit signed integer, which WinRT has no type for; Int16 is the
    /// narrowest that represents every value without truncating.
    /// </summary>
    internal static readonly Dictionary<string, string> MultiWordClassicTypes = new(StringComparer.Ordinal)
    {
        ["unsigned char"] = "UInt8",
        ["unsigned small"] = "UInt8",
        ["unsigned short"] = "UInt16",
        ["unsigned int"] = "UInt32",
        ["unsigned long"] = "UInt32",
        ["unsigned hyper"] = "UInt64",
        ["unsigned long long"] = "UInt64",
        ["unsigned __int8"] = "UInt8",
        ["unsigned __int16"] = "UInt16",
        ["unsigned __int32"] = "UInt32",
        ["unsigned __int64"] = "UInt64",
        ["unsigned __int3264"] = "UInt32",

        ["signed char"] = "Int16",
        ["signed small"] = "Int16",
        ["signed short"] = "Int16",
        ["signed int"] = "Int32",
        ["signed long"] = "Int32",
        ["signed hyper"] = "Int64",
        ["signed long long"] = "Int64",
        ["signed __int8"] = "Int16",
        ["signed __int16"] = "Int16",
        ["signed __int32"] = "Int32",
        ["signed __int64"] = "Int64",
        ["signed __int3264"] = "Int32",

        ["long long"] = "Int64",
    };

    /// <summary>
    /// Register all built-in WinRT types into the symbol table.
    /// Called once at compilation start, before user code binding.
    /// </summary>
    public static void Register(SymbolTable symbolTable, CompilationMode mode)
    {
        _detachedNamespaces = new(StringComparer.Ordinal);

        // 1. Core WinRT interfaces (global namespace)
        RegisterInterface(symbolTable, "", "IInspectable");
        RegisterInterface(symbolTable, "", "IUnknown");

        // 2. Windows.Foundation types
        RegisterInterface(symbolTable, "Windows.Foundation", "IStringable");
        RegisterInterface(symbolTable, "Windows.Foundation", "IClosable");
        RegisterInterface(symbolTable, "Windows.Foundation", "IAsyncAction");
        RegisterInterface(symbolTable, "Windows.Foundation", "IAsyncInfo");
        RegisterInterface(symbolTable, "Windows.Foundation", "IMemoryBuffer");
        RegisterInterface(symbolTable, "Windows.Foundation", "IPropertyValue");
        RegisterStruct(symbolTable, "Windows.Foundation", "DateTime");
        RegisterStruct(symbolTable, "Windows.Foundation", "TimeSpan");
        RegisterStruct(symbolTable, "Windows.Foundation", "Point");
        RegisterStruct(symbolTable, "Windows.Foundation", "Size");
        RegisterStruct(symbolTable, "Windows.Foundation", "Rect");
        RegisterStruct(symbolTable, "Windows.Foundation", "EventRegistrationToken");
        RegisterStruct(symbolTable, "Windows.Foundation", "HResult");
        RegisterRuntimeClass(symbolTable, "Windows.Foundation", "Uri");
        RegisterDelegate(symbolTable, "Windows.Foundation", "EventHandler");
        RegisterDelegate(symbolTable, "Windows.Foundation", "TypedEventHandler");
        RegisterDelegate(symbolTable, "Windows.Foundation", "AsyncActionCompletedHandler");

        // Parameterized types
        RegisterInterface(symbolTable, "Windows.Foundation", "IAsyncOperation");
        RegisterInterface(symbolTable, "Windows.Foundation", "IAsyncActionWithProgress");
        RegisterInterface(symbolTable, "Windows.Foundation", "IAsyncOperationWithProgress");
        RegisterInterface(symbolTable, "Windows.Foundation", "IReference");

        // 3. Windows.Foundation.Collections types
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IIterable");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IIterator");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IVector");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IVectorView");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IMap");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IMapView");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IKeyValuePair");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IObservableVector");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IObservableMap");
        RegisterDelegate(symbolTable, "Windows.Foundation.Collections", "VectorChangedEventHandler");
        RegisterDelegate(symbolTable, "Windows.Foundation.Collections", "MapChangedEventHandler");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IMapChangedEventArgs");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IPropertySet");
        RegisterInterface(symbolTable, "Windows.Foundation.Collections", "IVectorChangedEventArgs");
        RegisterEnum(symbolTable, "Windows.Foundation.Collections", "CollectionChange");

        // 4. Global type aliases — always available
        symbolTable.RegisterAlias("EventRegistrationToken", "Windows.Foundation.EventRegistrationToken");
        symbolTable.RegisterAlias("Char", "Char16");

#if WITH_COMPAT
        // 5. Win32 / C / MIDL2 type aliases (compat mode only)
        if (mode == CompilationMode.Compat)
        {
            symbolTable.RegisterAlias("HSTRING", "String");
            symbolTable.RegisterAlias("HRESULT", "Int32");
            symbolTable.RegisterAlias("GUID", "Guid");
            symbolTable.RegisterAlias("BYTE", "UInt8");
            symbolTable.RegisterAlias("UINT8", "UInt8");
            symbolTable.RegisterAlias("INT16", "Int16");
            symbolTable.RegisterAlias("UINT16", "UInt16");
            symbolTable.RegisterAlias("INT32", "Int32");
            symbolTable.RegisterAlias("UINT32", "UInt32");
            symbolTable.RegisterAlias("INT64", "Int64");
            symbolTable.RegisterAlias("UINT64", "UInt64");
            symbolTable.RegisterAlias("DWORD", "UInt32");
            symbolTable.RegisterAlias("WORD", "UInt16");
            symbolTable.RegisterAlias("FLOAT", "Single");
            symbolTable.RegisterAlias("DOUBLE", "Double");
            symbolTable.RegisterAlias("BOOL", "Boolean");
            symbolTable.RegisterAlias("WCHAR", "Char16");
            symbolTable.RegisterAlias("ULONGLONG", "UInt64");
            symbolTable.RegisterAlias("LONGLONG", "Int64");
            symbolTable.RegisterAlias("__int64", "Int64");
            symbolTable.RegisterAlias("AsyncStatus", "Int32");
            symbolTable.RegisterAlias("ULONG", "UInt32");
            symbolTable.RegisterAlias("LONG", "Int32");
            symbolTable.RegisterAlias("USHORT", "UInt16");
            symbolTable.RegisterAlias("SHORT", "Int16");
            symbolTable.RegisterAlias("UCHAR", "UInt8");
            symbolTable.RegisterAlias("CHAR", "UInt8");
            symbolTable.RegisterAlias("LPVOID", "Object");
            symbolTable.RegisterAlias("HANDLE", "UInt64");
            symbolTable.RegisterAlias("SIZE_T", "UInt64");
            symbolTable.RegisterAlias("UINT", "UInt32");
            symbolTable.RegisterAlias("INT", "Int32");
            symbolTable.RegisterAlias("LPWSTR", "String");
            symbolTable.RegisterAlias("LPCWSTR", "String");
            symbolTable.RegisterAlias("LPSTR", "String");
            symbolTable.RegisterAlias("LPCSTR", "String");
            symbolTable.RegisterAlias("BSTR", "String");
            symbolTable.RegisterAlias("VARIANT", "Object");
            symbolTable.RegisterAlias("IID", "Guid");
            symbolTable.RegisterAlias("TCHAR", "Char16");
            symbolTable.RegisterAlias("OLECHAR", "Char16");
            symbolTable.RegisterAlias("LPTSTR", "String");
            symbolTable.RegisterAlias("LPCTSTR", "String");
            symbolTable.RegisterAlias("CLSID", "Guid");
            symbolTable.RegisterAlias("REFIID", "Guid");
            symbolTable.RegisterAlias("REFCLSID", "Guid");
            symbolTable.RegisterAlias("REFGUID", "Guid");
            // Additional classic COM / OLE pointer typedefs
            symbolTable.RegisterAlias("LPOLESTR", "String");
            symbolTable.RegisterAlias("LPCOLESTR", "String");
            symbolTable.RegisterAlias("BSTR", "String");
            symbolTable.RegisterAlias("LCID", "UInt32");
            symbolTable.RegisterAlias("LANGID", "UInt16");
            symbolTable.RegisterAlias("ULONG_PTR", "UInt64");
            symbolTable.RegisterAlias("LONG_PTR", "Int64");
            symbolTable.RegisterAlias("DWORD_PTR", "UInt64");
            symbolTable.RegisterAlias("UINT_PTR", "UInt64");
            symbolTable.RegisterAlias("INT_PTR", "Int64");
            symbolTable.RegisterAlias("WPARAM", "UInt64");
            symbolTable.RegisterAlias("LPARAM", "Int64");
            symbolTable.RegisterAlias("LRESULT", "Int64");
            symbolTable.RegisterAlias("BOOLEAN", "Boolean");
            symbolTable.RegisterAlias("PVOID", "Object");
            symbolTable.RegisterAlias("DWORDLONG", "UInt64");
            symbolTable.RegisterAlias("QWORD", "UInt64");
            symbolTable.RegisterAlias("SCODE", "Int32");
            symbolTable.RegisterAlias("DATE", "Double");
            symbolTable.RegisterAlias("CY", "Int64");
            symbolTable.RegisterAlias("DECIMAL", "Int64");
            symbolTable.RegisterAlias("VARIANT_BOOL", "Int16");
            symbolTable.RegisterAlias("RPCOLEDATAREP", "UInt32");
        }

        // 6. Compat-mode classic C lowercase types
        if (mode == CompilationMode.Compat)
        {
            symbolTable.RegisterAlias("double", "Double");
            symbolTable.RegisterAlias("float", "Single");
            symbolTable.RegisterAlias("boolean", "Boolean");
            symbolTable.RegisterAlias("int", "Int32");
            symbolTable.RegisterAlias("long", "Int32");
            symbolTable.RegisterAlias("short", "Int16");
            symbolTable.RegisterAlias("byte", "UInt8");
            symbolTable.RegisterAlias("char", "Char16");
            symbolTable.RegisterAlias("wchar_t", "Char16");
            symbolTable.RegisterAlias("hyper", "Int64");
            symbolTable.RegisterAlias("small", "UInt8");
            symbolTable.RegisterAlias("unsigned", "UInt32");

            foreach (var kv in MultiWordClassicTypes)
                symbolTable.RegisterAlias(kv.Key, kv.Value);
        }
#endif

        _detachedNamespaces = null;
    }

    /// <summary>
    /// Creates a detached namespace hierarchy not connected to the real global namespace.
    /// This ensures built-in types don't appear in compilation output.
    /// </summary>
    private static NamespaceSymbol GetDetachedNamespace(string dottedName)
    {
        _detachedNamespaces ??= new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dottedName))
        {
            if (!_detachedNamespaces.TryGetValue("", out var globalNs))
            {
                globalNs = new NamespaceSymbol("");
                _detachedNamespaces[""] = globalNs;
            }
            return globalNs;
        }

        if (_detachedNamespaces.TryGetValue(dottedName, out var cached))
            return cached;

        var parts = dottedName.Split('.');
        var current = GetDetachedNamespace("");
        var pathSoFar = "";
        foreach (var part in parts)
        {
            pathSoFar = pathSoFar.Length > 0 ? $"{pathSoFar}.{part}" : part;
            if (!_detachedNamespaces.TryGetValue(pathSoFar, out var child))
            {
                child = new NamespaceSymbol(part, current);
                _detachedNamespaces[pathSoFar] = child;
            }
            current = child;
        }
        return current;
    }

    private static void RegisterInterface(SymbolTable symbolTable, string namespaceName, string name)
    {
        var ns = GetDetachedNamespace(namespaceName);
        var symbol = new InterfaceSymbol(name, ns)
        {
            IsExternal = true,
            SourceWinMdPath = BuiltInSource,
        };
        symbolTable.Define(symbol);
    }

    private static void RegisterStruct(SymbolTable symbolTable, string namespaceName, string name)
    {
        var ns = GetDetachedNamespace(namespaceName);
        var symbol = new StructSymbol(name, ns)
        {
            IsExternal = true,
            SourceWinMdPath = BuiltInSource,
        };
        symbolTable.Define(symbol);
    }

    private static void RegisterEnum(SymbolTable symbolTable, string namespaceName, string name)
    {
        var ns = GetDetachedNamespace(namespaceName);
        var symbol = new EnumSymbol(name, ns)
        {
            IsExternal = true,
            SourceWinMdPath = BuiltInSource,
        };
        symbolTable.Define(symbol);
    }

    private static void RegisterDelegate(SymbolTable symbolTable, string namespaceName, string name)
    {
        var ns = GetDetachedNamespace(namespaceName);
        var symbol = new DelegateSymbol(name, ns)
        {
            IsExternal = true,
            SourceWinMdPath = BuiltInSource,
        };
        symbolTable.Define(symbol);
    }

    private static void RegisterRuntimeClass(SymbolTable symbolTable, string namespaceName, string name)
    {
        var ns = GetDetachedNamespace(namespaceName);
        var symbol = new RuntimeClassSymbol(name, ns)
        {
            IsExternal = true,
            SourceWinMdPath = BuiltInSource,
        };
        symbolTable.Define(symbol);
    }
}
