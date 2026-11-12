// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using Midl.Analysis.Symbols;

namespace Midl.Analysis;

/// <summary>
/// Computes deterministic WinRT ABI GUIDs for interfaces and delegates.
/// Uses UUID v5 (SHA-1) with the WinRT interface namespace GUID,
/// seeded by a string built from the type's ABI method signatures.
/// Matches the algorithm in original midl.exe (ModernNode.cxx).
/// </summary>
public static class AbiGuidComputer
{
    /// <summary>
    /// Well-known namespace UUID for non-parameterized interface GUID generation.
    /// Different from the pinterface namespace UUID (11f47ad5-...).
    /// </summary>
    private static readonly Guid InterfaceNamespaceGuid = new("e72a134c-baf7-4dd3-b542-77848e87b138");

    /// <summary>
    /// Computes the ABI GUID for an interface.
    /// Returns the explicit UUID if one is set, otherwise computes from the ABI signature.
    /// </summary>
    public static Guid ComputeGuid(InterfaceSymbol iface)
    {
        if (iface.Uuid.HasValue) return iface.Uuid.Value;
        return ComputeGuid(iface.FullName, iface.Members.ToList());
    }

    /// <summary>
    /// Computes the ABI GUID for a delegate.
    /// Returns the explicit UUID if one is set, otherwise computes from the ABI signature.
    /// </summary>
    public static Guid ComputeGuid(DelegateSymbol del)
    {
        if (del.Uuid.HasValue) return del.Uuid.Value;
        var invokeMethod = new MethodSymbol("Invoke", null) { ReturnType = del.ReturnType };
        invokeMethod.Parameters.AddRange(del.Parameters);
        return ComputeGuid(del.FullName, new List<ISymbol> { invokeMethod });
    }

    /// <summary>
    /// Core GUID computation from a full name and member list.
    /// </summary>
    public static Guid ComputeGuid(string fullName, IReadOnlyList<ISymbol> members)
    {
        var seed = BuildAbiSignatureSeed(fullName, members);
        return GenerateUuidV5(InterfaceNamespaceGuid, seed);
    }

    /// <summary>
    /// Builds the ABI signature seed string for GUID generation.
    /// Format: "{fullName}:HRESULT method1(params);HRESULT method2(params);..."
    /// </summary>
    public static string BuildAbiSignatureSeed(string fullName, IReadOnlyList<ISymbol> members)
    {
        var sb = new StringBuilder();
        sb.Append(fullName);
        sb.Append(':');

        foreach (var member in members)
        {
            switch (member)
            {
                case MethodSymbol method:
                    AppendMethodSeed(sb, method.AbiName, method.Parameters, method.ReturnType);
                    break;
                case PropertySymbol prop:
                    AppendMethodSeed(sb, $"get_{prop.Name}", [], prop.Type);
                    if (prop.HasSetter)
                    {
                        var setterParam = new ParameterSymbol("value", null) { Type = prop.Type };
                        AppendMethodSeed(sb, $"put_{prop.Name}", [setterParam], TypeRef.Void);
                    }
                    break;
                case EventSymbol evt:
                    AppendEventAddSeed(sb, evt);
                    AppendEventRemoveSeed(sb, evt);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendMethodSeed(StringBuilder sb, string name,
        IReadOnlyList<ParameterSymbol> parameters, TypeRef returnType)
    {
        sb.Append("HRESULT ");
        sb.Append(name);
        sb.Append('(');

        bool first = true;
        foreach (var p in parameters)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendTypeSeed(sb, p.Type);
            if (IsReferenceType(p.Type))
                sb.Append('*');
        }

        if (returnType.Name != "void")
        {
            if (!first) sb.Append(',');
            AppendTypeSeed(sb, returnType);
            sb.Append(IsReferenceType(returnType) ? "**" : "*");
        }

        sb.Append(");");
    }

    /// <summary>
    /// Determines if a TypeRef is a reference type in ABI (passed as pointer).
    /// Interfaces, delegates, runtime classes, and Object (IInspectable) are reference types.
    /// String (HSTRING) is a value-like handle: no extra * for input, single * for output.
    /// </summary>
    private static bool IsReferenceType(TypeRef typeRef)
    {
        if (typeRef.Name == "Object") return true;
        return typeRef.ResolvedSymbol is InterfaceSymbol or DelegateSymbol or RuntimeClassSymbol;
    }

    private static void AppendEventAddSeed(StringBuilder sb, EventSymbol evt)
    {
        sb.Append("HRESULT add_");
        sb.Append(evt.Name);
        sb.Append('(');
        AppendTypeSeed(sb, evt.DelegateType);
        sb.Append("*,Windows.Foundation.EventRegistrationToken*);");
    }

    private static void AppendEventRemoveSeed(StringBuilder sb, EventSymbol evt)
    {
        sb.Append("HRESULT remove_");
        sb.Append(evt.Name);
        sb.Append("(EventRegistrationToken);");
    }

    private static void AppendTypeSeed(StringBuilder sb, TypeRef typeRef)
    {
        if (typeRef.TypeArguments.Count > 0)
        {
            var resolvedName = typeRef.ResolvedSymbol?.FullName ?? typeRef.Name;
            sb.Append(resolvedName);
            sb.Append('`');
            sb.Append(typeRef.TypeArguments.Count);
            sb.Append('<');
            for (int i = 0; i < typeRef.TypeArguments.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendTypeSeed(sb, typeRef.TypeArguments[i]);
            }
            sb.Append('>');
        }
        else
        {
            sb.Append(typeRef.ResolvedSymbol?.FullName ?? typeRef.Name);
        }
    }

    /// <summary>
    /// Computes a UUID v5 (SHA-1 based) from a namespace UUID and a name string.
    /// </summary>
    private static Guid GenerateUuidV5(Guid namespaceId, string name)
    {
        var nsBytes = namespaceId.ToByteArray();
        // Convert to big-endian (network byte order / RFC 4122)
        (nsBytes[0], nsBytes[3]) = (nsBytes[3], nsBytes[0]);
        (nsBytes[1], nsBytes[2]) = (nsBytes[2], nsBytes[1]);
        (nsBytes[4], nsBytes[5]) = (nsBytes[5], nsBytes[4]);
        (nsBytes[6], nsBytes[7]) = (nsBytes[7], nsBytes[6]);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nsBytes.Length + nameBytes.Length];
        Array.Copy(nsBytes, input, nsBytes.Length);
        Array.Copy(nameBytes, 0, input, nsBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);

        // Set version 5 and variant RFC 4122
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Convert from big-endian UUID to .NET Guid
        int a = (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3];
        short b = (short)((hash[4] << 8) | hash[5]);
        short c = (short)((hash[6] << 8) | hash[7]);
        byte[] d = { hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15] };

        return new Guid(a, b, c, d);
    }
}
