// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;

namespace Midl.Analysis;

/// <summary>
/// Propagates version/contract info through the symbol hierarchy and validates
/// version constraints after interface synthesis.
/// </summary>
public class VersionPropagator
{
    private readonly List<Diagnostic> _diagnostics;

    public VersionPropagator(List<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void Propagate(Compilation compilation)
    {
        var unitSourceMaps = compilation.UnitSourceMaps;
        // Build a lookup by full name and by simple name only when unambiguous.
        var contracts = new Dictionary<string, ApiContractSymbol>(StringComparer.OrdinalIgnoreCase);
        var contractSymbols = compilation.GetAllTypes().OfType<ApiContractSymbol>().ToList();
        foreach (var contract in contractSymbols)
        {
            contracts[contract.FullName] = contract;
        }
        foreach (var group in contractSymbols.GroupBy(
            contract => contract.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
                contracts[group.Key] = group.Single();
        }

        foreach (var type in compilation.GetAllTypes().ToList())
        {
            // Types reached through an import report at offsets in their own file.
            int diagStart = _diagnostics.Count;
            NormalizeContractMetadata(type, contracts);
            switch (type)
            {
                case RuntimeClassSymbol rc:
                    ValidateContractReference(rc, rc.ContractName, rc.Attributes, contracts);
                    PropagateToSynthesizedInterfaces(rc);
                    PropagateToMembers(rc);
                    ValidateRuntimeClassInterfaces(rc, contracts);
                    break;

                case EnumSymbol es:
                    ValidateContractReference(es, es.ContractName, es.Attributes, contracts);
                    ValidateEnumMemberVersions(es, contracts);
                    break;

                case StructSymbol st:
                    ValidateContractReference(st, st.ContractName, st.Attributes, contracts);
                    break;

                case DelegateSymbol del:
                    ValidateContractReference(del, del.ContractName, del.Attributes, contracts);
                    break;

                case InterfaceSymbol iface when !iface.IsSynthesized:
                    ValidateContractReference(iface, iface.ContractName, iface.Attributes, contracts);
                    break;
            }
            DiagnosticSourceTagger.Tag(_diagnostics, diagStart, type.SourceFile, unitSourceMaps);
        }
    }

    private static void NormalizeContractMetadata(
        ISymbol symbol,
        Dictionary<string, ApiContractSymbol> contracts)
    {
        switch (symbol)
        {
            case RuntimeClassSymbol rc:
                PropagateInternalContractVisibility(rc, rc.ContractName, rc.Attributes, contracts);
                break;
            case EnumSymbol es:
                PropagateInternalContractVisibility(es, es.ContractName, es.Attributes, contracts);
                break;
            case StructSymbol st:
                PropagateInternalContractVisibility(st, st.ContractName, st.Attributes, contracts);
                break;
            case DelegateSymbol del:
                PropagateInternalContractVisibility(del, del.ContractName, del.Attributes, contracts);
                break;
            case InterfaceSymbol iface:
                PropagateInternalContractVisibility(iface, iface.ContractName, iface.Attributes, contracts);
                break;
        }
    }

    private static void PropagateInternalContractVisibility(
        ISymbol symbol,
        string? contractName,
        List<AttributeData> attributes,
        Dictionary<string, ApiContractSymbol> contracts)
    {
        if (contractName == null || !TryResolveContract(symbol, contractName, contracts, out var contract))
            return;

        var internalAttribute = contract.Attributes.FirstOrDefault(
            attribute => attribute.Name.Equals("internal", StringComparison.OrdinalIgnoreCase));
        if (internalAttribute != null && !attributes.Any(
            attribute => attribute.Name.Equals("internal", StringComparison.OrdinalIgnoreCase)))
        {
            attributes.Add(new AttributeData
            {
                Name = internalAttribute.Name,
                Position = internalAttribute.Position,
            });
        }
    }

    /// <summary>
    /// Rule 1: Validate that a referenced contract name exists in the symbol table.
    /// Only validates contracts that could be locally defined (not fully-qualified external references).
    /// </summary>
    private void ValidateContractReference(
        ISymbol symbol,
        string? contractName,
        List<AttributeData> attributes,
        Dictionary<string, ApiContractSymbol> contracts)
    {
        if (contractName == null) return;
        if (TryResolveContract(symbol, contractName, contracts, out _)) return;

        // Fully-qualified contract names (containing '.') typically refer to external
        // contracts from .winmd references — skip validation for those.
        if (contractName.Contains('.')) return;

        // Find the position from the contract attribute
        var contractAttr = attributes.FirstOrDefault(
            a => a.Name.Equals("contract", StringComparison.OrdinalIgnoreCase));
        int position = contractAttr?.Position ?? 0;

        _diagnostics.Add(DiagnosticDescriptors.UndefinedContract(contractName, position));
    }

    private static bool TryResolveContract(
        ISymbol symbol,
        string contractName,
        Dictionary<string, ApiContractSymbol> contracts,
        out ApiContractSymbol contract)
    {
        if (!contractName.Contains('.'))
        {
            var ns = GetNamespace(symbol);
            if (ns.Length > 0 && contracts.TryGetValue($"{ns}.{contractName}", out contract!))
                return true;
        }

        return contracts.TryGetValue(contractName, out contract!);
    }

    private static string GetNamespace(ISymbol symbol)
    {
        for (var current = symbol.ContainingSymbol; current != null; current = current.ContainingSymbol)
        {
            if (current is NamespaceSymbol ns && ns.FullName.Length > 0)
                return ns.FullName;
        }

        return "";
    }

    /// <summary>
    /// Rule 2: Synthesized interfaces inherit the class's contract/version if they don't have their own.
    /// </summary>
    private static void PropagateToSynthesizedInterfaces(RuntimeClassSymbol rc)
    {
        if (rc.ContractName == null) return;

        foreach (var iface in rc.ImplementedInterfaces)
        {
            if (iface.IsSynthesized && iface.ContractName == null)
            {
                iface.ContractName = rc.ContractName;
                iface.ContractVersion = rc.ContractVersion;
            }
        }
    }

    /// <summary>
    /// Rule 3: Members without explicit [contract] inherit the enclosing class's contract.
    /// </summary>
    private static void PropagateToMembers(RuntimeClassSymbol rc)
    {
        if (rc.ContractName == null) return;

        foreach (var member in rc.Members)
        {
            switch (member)
            {
                case MethodSymbol m when m.ContractName == null:
                    m.ContractName = rc.ContractName;
                    m.ContractVersion = rc.ContractVersion;
                    break;
                case PropertySymbol p when p.ContractName == null:
                    p.ContractName = rc.ContractName;
                    p.ContractVersion = rc.ContractVersion;
                    break;
                case EventSymbol e when e.ContractName == null:
                    e.ContractName = rc.ContractName;
                    e.ContractVersion = rc.ContractVersion;
                    break;
                case ConstructorSymbol c when c.ContractName == null:
                    c.ContractName = rc.ContractName;
                    c.ContractVersion = rc.ContractVersion;
                    break;
            }
        }
    }

    /// <summary>
    /// Rules 5 and 6: Validate the versions and contract families of a runtimeclass's own members
    /// (its synthesized interfaces), as midl.exe does (MIDL5132, MIDL5164).
    /// </summary>
    private void ValidateRuntimeClassInterfaces(
        RuntimeClassSymbol rc, Dictionary<string, ApiContractSymbol> contracts)
    {
        if (rc.ContractName == null) return;

        // Collect all contracts the class uses (from its own attributes)
        var classContracts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeClassContract = GetContractIdentity(rc, rc.ContractName, contracts);
        classContracts.Add(runtimeClassContract);
        foreach (var attr in rc.Attributes)
        {
            if (attr.Name.Equals("contract", StringComparison.OrdinalIgnoreCase))
            {
                var name = attr.GetTypeName(0) ?? attr.GetIdentifier(0);
                if (name != null)
                    classContracts.Add(GetContractIdentity(rc, name, contracts));
            }
        }

        // Check synthesized interfaces. Interfaces the class declares it implements are not checked:
        // midl.exe accepts an interface that is older than the class, exclusive or not, or that belongs
        // to another contract, and stamps the class's members from it with the class's contract.
        foreach (var iface in rc.ImplementedInterfaces)
        {
            ValidateInterfaceForClass(
                iface,
                rc,
                runtimeClassContract,
                classContracts,
                contracts);
        }
    }

    private void ValidateInterfaceForClass(
        InterfaceSymbol iface,
        RuntimeClassSymbol rc,
        string runtimeClassContract,
        HashSet<string> classContracts,
        Dictionary<string, ApiContractSymbol> contracts)
    {
        if (iface.ContractName == null) return;
        var interfaceContract = GetContractIdentity(iface, iface.ContractName, contracts);

        int ifacePosition = 0;
        var ifaceContractAttr = iface.Attributes.FirstOrDefault(
            a => a.Name.Equals("contract", StringComparison.OrdinalIgnoreCase));
        if (ifaceContractAttr != null)
            ifacePosition = ifaceContractAttr.Position;

        // Rule 5: Interface version >= class version (same contract family)
        if (interfaceContract.Equals(runtimeClassContract, StringComparison.OrdinalIgnoreCase)
            && iface.ContractVersion.HasValue && rc.ContractVersion.HasValue
            && iface.ContractVersion.Value < rc.ContractVersion.Value)
        {
            _diagnostics.Add(DiagnosticDescriptors.InterfaceVersionTooEarly(
                iface.Name, iface.ContractVersion.Value, rc.ContractVersion.Value, ifacePosition));
        }

        // Rule 6: Contract family consistency
        if (!classContracts.Contains(interfaceContract))
        {
            _diagnostics.Add(DiagnosticDescriptors.ContractFamilyMismatch(
                iface.Name, iface.ContractName, rc.Name, ifacePosition));
        }
    }

    private static string GetContractIdentity(
        ISymbol symbol,
        string contractName,
        Dictionary<string, ApiContractSymbol> contracts)
    {
        return TryResolveContract(symbol, contractName, contracts, out var contract)
            ? contract.FullName
            : contractName;
    }

    /// <summary>
    /// Rule 7: Enum member version >= enum version.
    /// </summary>
    private void ValidateEnumMemberVersions(
        EnumSymbol es, Dictionary<string, ApiContractSymbol> contracts)
    {
        if (!es.ContractVersion.HasValue) return;

        foreach (var member in es.EnumMembers)
        {
            ValidateContractReference(member, member.ContractName, member.Attributes, contracts);

            if (member.ContractVersion.HasValue
                && member.ContractVersion.Value < es.ContractVersion.Value)
            {
                int position = 0;
                var memberAttr = member.Attributes.FirstOrDefault(
                    a => a.Name.Equals("contract", StringComparison.OrdinalIgnoreCase));
                if (memberAttr != null) position = memberAttr.Position;

                _diagnostics.Add(DiagnosticDescriptors.EnumMemberVersionTooEarly(
                    member.Name, member.ContractVersion.Value, es.ContractVersion.Value, position));
            }
        }
    }
}
