// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Midl.Analysis.Symbols;
using Midl.Syntax;

namespace Midl.Analysis;

/// <summary>
/// Lazily loads WinRT types from a .winmd file. Builds a lightweight name→handle
/// index on open, then decodes individual TypeDefs only when requested.
/// </summary>
public class LazyWinMdProvider : IDisposable
{
    private const int TdWindowsRuntime = 0x4000;

    private readonly PEReader _peReader;
    private readonly MetadataReader _mdReader;
    private readonly string? _sourcePath;

    public string? SourcePath => _sourcePath;

    // Index: fully-qualified name → TypeDefinitionHandle
    private readonly Dictionary<string, TypeDefinitionHandle> _typeIndex;

    // Cache: already-resolved symbols
    private readonly Dictionary<string, ISymbol> _cache = new(StringComparer.Ordinal);

    public LazyWinMdProvider(string winmdPath)
    {
        var bytes = File.ReadAllBytes(winmdPath);
        _peReader = new PEReader(new MemoryStream(bytes));
        // Raw metadata, as in WinMdReader: no WinRT->CLR projection of referenced type names.
        _mdReader = _peReader.GetMetadataReader(MetadataReaderOptions.None);
        _sourcePath = winmdPath;

        // Build lightweight index: just names → handles (fast, no type decoding)
        _typeIndex = new(StringComparer.Ordinal);
        foreach (var handle in _mdReader.TypeDefinitions)
        {
            var td = _mdReader.GetTypeDefinition(handle);
            var name = _mdReader.GetString(td.Name);
            if (name == "<Module>") continue;

            // Index all WinRT-flagged types regardless of CLR visibility.
            // Synthesized exclusive interfaces are non-public but must still be indexed.
            var attrs = td.Attributes;
            if (((int)attrs & TdWindowsRuntime) == 0) continue;

            var ns = _mdReader.GetString(td.Namespace);
            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            _typeIndex[fullName] = handle;
        }
    }

    internal LazyWinMdProvider(byte[] winmdBytes)
    {
        _peReader = new PEReader(new MemoryStream(winmdBytes));
        _mdReader = _peReader.GetMetadataReader(MetadataReaderOptions.None);
        _sourcePath = null;

        _typeIndex = new(StringComparer.Ordinal);
        foreach (var handle in _mdReader.TypeDefinitions)
        {
            var td = _mdReader.GetTypeDefinition(handle);
            var name = _mdReader.GetString(td.Name);
            if (name == "<Module>") continue;

            var attrs = td.Attributes;
            if (((int)attrs & TdWindowsRuntime) == 0) continue;

            var ns = _mdReader.GetString(td.Namespace);
            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            _typeIndex[fullName] = handle;
        }
    }

    /// <summary>Number of types available (for diagnostics/logging).</summary>
    public int TypeCount => _typeIndex.Count;

    /// <summary>
    /// Number of types actually decoded so far. The whole point of this provider is that
    /// constructing it indexes names without decoding any type, so tests assert on this
    /// directly rather than timing the difference.
    /// </summary>
    internal int DecodedTypeCount => _cache.Count;

    /// <summary>Check if a type exists without fully loading it.</summary>
    public bool HasType(string fullName) => _typeIndex.ContainsKey(fullName);

    /// <summary>Try to resolve a type by fully-qualified name. Loads on first access.</summary>
    public ISymbol? TryResolve(string fullName)
    {
        MidlTrace.Binder($"LazyWinMd.TryResolve: {fullName}");
        if (_cache.TryGetValue(fullName, out var cached))
            return cached;

        if (!_typeIndex.TryGetValue(fullName, out var handle))
        {
            MidlTrace.Binder($"LazyWinMd.TryResolve: {fullName} not in index");
            return null;
        }

        var symbol = WinMdReader.ReadSingleType(_mdReader, handle, _sourcePath);
        if (symbol != null)
        {
            _cache[fullName] = symbol;
            if (symbol is RuntimeClassSymbol rc)
                MidlTrace.Binder($"LazyWinMd.TryResolve: {fullName} → RuntimeClass IsUnsealed={rc.IsUnsealed}");
            else
                MidlTrace.Binder($"LazyWinMd.TryResolve: {fullName} → {symbol.Kind}");
        }

        return symbol;
    }

    /// <summary>Returns all fully-qualified type names in this provider.</summary>
    public IEnumerable<string> GetAllTypeNames() => _typeIndex.Keys;

    /// <summary>Find all types in a namespace (for completion suggestions).</summary>
    public IEnumerable<string> GetTypesInNamespace(string ns)
    {
        var prefix = ns + ".";
        return _typeIndex.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)
            && !k.AsSpan(prefix.Length).Contains('.'));
    }

    public void Dispose()
    {
        _peReader?.Dispose();
    }
}
