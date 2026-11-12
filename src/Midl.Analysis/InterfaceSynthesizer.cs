// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis.Symbols;

namespace Midl.Analysis;

/// <summary>
/// Walks all RuntimeClassSymbols after binding and synthesizes the required
/// exclusive WinRT interfaces (instance, factory, statics, protected, overrides).
/// </summary>
public partial class InterfaceSynthesizer
{
    public void Synthesize(Compilation compilation)
    {
        foreach (var type in compilation.GetAllTypes().ToList())
        {
            if (type is RuntimeClassSymbol rc)
            {
                SynthesizeForClass(rc);
            }
        }

        foreach (var type in compilation.GetAllTypes().ToList())
        {
            if (type is RuntimeClassSymbol rc)
            {
                foreach (var iface in rc.ImplementedInterfaces)
                    AssignOverloadSetAbiNames(iface);
            }
            else if (type is InterfaceSymbol { IsExternal: false, IsSynthesized: false } iface)
            {
                AssignOverloadSetAbiNames(iface);
            }
        }
    }

    /// <summary>
    /// midl.exe gives the methods of one interface that share a name distinct ABI names: the first keeps
    /// its name and the n-th unnamed one becomes Name{n}, unless [method_name] names it. Each gets an
    /// OverloadAttribute with its ABI name. Methods with [overload("X")] already have unique names.
    /// </summary>
    internal static void AssignOverloadSetAbiNames(InterfaceSymbol iface)
    {
        var sets = iface.Members.OfType<MethodSymbol>()
            .Where(m => m.OverloadGroup == null)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var set in sets)
        {
            int unnamed = 0;
            foreach (var method in set)
            {
                method.IsInOverloadSet = true;
                if (method.MethodNameOverride != null) continue;
                unnamed++;
                method.MethodNameOverride = unnamed == 1 ? method.Name : $"{method.Name}{unnamed}";
            }
        }
    }

    private static string? GetStringAttribute(List<AttributeData> attributes, string name) =>
        attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.GetString(0);

    private void SynthesizeForClass(RuntimeClassSymbol rc)
    {
        var ns = rc.ContainingSymbol as NamespaceSymbol;

        // Collect members by category
        var instanceMembers = new List<ISymbol>();
        var staticMembers = new List<ISymbol>();
        var protectedMembers = new List<ISymbol>();
        var overridableMembers = new List<ISymbol>();
        var parameterizedCtors = new List<ConstructorSymbol>();
        bool hasDefaultCtor = false;

        foreach (var member in rc.Members)
        {
            switch (member)
            {
                case ConstructorSymbol ctor:
                    if (ctor.Parameters.Count == 0)
                        hasDefaultCtor = true;
                    else
                        parameterizedCtors.Add(ctor);
                    break;

                case MethodSymbol method:
                    if (method.IsProtected)
                        protectedMembers.Add(method);
                    else if (method.IsOverridable)
                        overridableMembers.Add(method);
                    else if (method.IsStatic)
                        staticMembers.Add(method);
                    else
                        instanceMembers.Add(method);
                    break;

                case PropertySymbol prop:
                    if (prop.IsProtected)
                        protectedMembers.Add(prop);
                    else if (prop.IsOverridable)
                        overridableMembers.Add(prop);
                    else if (prop.IsStatic)
                        staticMembers.Add(prop);
                    else
                        instanceMembers.Add(prop);
                    break;

                case EventSymbol evt:
                    if (evt.IsProtected)
                        protectedMembers.Add(evt);
                    else if (evt.IsOverridable)
                        overridableMembers.Add(evt);
                    else if (evt.IsStatic)
                        staticMembers.Add(evt);
                    else
                        instanceMembers.Add(evt);
                    break;
            }
        }

        // Rule A: Instance member interfaces, partitioned by contract version.
        var instanceGroups = GroupMembersByContract(instanceMembers, rc);
        for (int groupIndex = 0; groupIndex < instanceGroups.Count; groupIndex++)
        {
            var baseName = rc.DefaultInterfaceName != null
                ? ExtractSimpleName(rc.DefaultInterfaceName)
                : $"I{rc.Name}";
            var ifaceName = GetUniqueName(
                groupIndex == 0 ? baseName : $"{baseName}{groupIndex + 1}",
                ns);
            var group = instanceGroups[groupIndex];
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                group.Members,
                group.ContractName,
                group.ContractVersion);
            if (groupIndex == 0 && rc.DefaultInterfaceGuid != null)
                iface.Uuid = rc.DefaultInterfaceGuid;
            if (groupIndex == 0)
                rc.DefaultInterface = iface;
            rc.ImplementedInterfaces.Add(iface);
        }

        // Rule B: Factory interfaces (parameterized constructors), partitioned by contract version.
        var factoryGroups = GroupMembersByContract(parameterizedCtors.Cast<ISymbol>().ToList(), rc);
        for (int groupIndex = 0; groupIndex < factoryGroups.Count; groupIndex++)
        {
            var baseName = rc.FactoryInterfaceName != null
                ? ExtractSimpleName(rc.FactoryInterfaceName)
                : $"I{rc.Name}Factory";
            var ifaceName = GetUniqueName(
                groupIndex == 0 ? baseName : $"{baseName}{groupIndex + 1}",
                ns);
            var group = factoryGroups[groupIndex];
            var factoryMembers = new List<ISymbol>();
            // midl.exe names the factory methods CreateInstance, CreateInstance2, ... (counting the
            // constructors without [method_name]) and their return value "value" unless [return_name].
            int unnamed = 0;
            foreach (var ctor in group.Members.Cast<ConstructorSymbol>())
            {
                var methodName = GetStringAttribute(ctor.Attributes, "method_name");
                if (methodName == null)
                {
                    unnamed++;
                    methodName = unnamed == 1 ? "CreateInstance" : $"CreateInstance{unnamed}";
                }
                var factoryMethod = new MethodSymbol(methodName, null)
                {
                    ReturnType = new TypeRef(rc.Name) { ResolvedSymbol = rc },
                    ContractName = group.ContractName,
                    ContractVersion = group.ContractVersion,
                    ReturnNameOverride = GetStringAttribute(ctor.Attributes, "return_name") ?? "value",
                };
                factoryMethod.Parameters.AddRange(ctor.Parameters);
                factoryMembers.Add(factoryMethod);
            }
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                factoryMembers,
                group.ContractName,
                group.ContractVersion);
            if (groupIndex == 0 && rc.FactoryInterfaceGuid != null)
                iface.Uuid = rc.FactoryInterfaceGuid;
            if (groupIndex == 0)
                rc.FactoryInterface = iface;
            rc.FactoryInterfaces.Add(iface);
            rc.ImplementedInterfaces.Add(iface);
        }

        // Rule C: Default activation
        if (hasDefaultCtor)
        {
            rc.HasDefaultActivation = true;
        }

        // Rule D: Static interfaces, partitioned by contract version.
        var staticGroups = GroupMembersByContract(staticMembers, rc);
        for (int groupIndex = 0; groupIndex < staticGroups.Count; groupIndex++)
        {
            var baseName = rc.StaticsInterfaceName != null
                ? ExtractSimpleName(rc.StaticsInterfaceName)
                : $"I{rc.Name}Statics";
            var ifaceName = GetUniqueName(
                groupIndex == 0 ? baseName : $"{baseName}{groupIndex + 1}",
                ns);
            var group = staticGroups[groupIndex];
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                group.Members,
                group.ContractName,
                group.ContractVersion);
            if (groupIndex == 0 && rc.StaticsInterfaceGuid != null)
                iface.Uuid = rc.StaticsInterfaceGuid;
            rc.StaticsInterfaces.Add(iface);
            rc.ImplementedInterfaces.Add(iface);
        }

        // Rule E: Protected interfaces, partitioned by contract version.
        var protectedGroups = GroupMembersByContract(protectedMembers, rc);
        for (int groupIndex = 0; groupIndex < protectedGroups.Count; groupIndex++)
        {
            var baseName = $"I{rc.Name}Protected";
            var ifaceName = GetUniqueName(
                groupIndex == 0 ? baseName : $"{baseName}{groupIndex + 1}",
                ns);
            var group = protectedGroups[groupIndex];
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                group.Members,
                group.ContractName,
                group.ContractVersion);
            if (groupIndex == 0)
                rc.ProtectedInterface = iface;
            rc.ProtectedInterfaces.Add(iface);
            rc.ImplementedInterfaces.Add(iface);
        }

        // Rule F: Overridable interfaces, partitioned by contract version.
        var overridesGroups = GroupMembersByContract(overridableMembers, rc);
        for (int groupIndex = 0; groupIndex < overridesGroups.Count; groupIndex++)
        {
            var baseName = $"I{rc.Name}Overrides";
            var ifaceName = GetUniqueName(
                groupIndex == 0 ? baseName : $"{baseName}{groupIndex + 1}",
                ns);
            var group = overridesGroups[groupIndex];
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                group.Members,
                group.ContractName,
                group.ContractVersion);
            if (groupIndex == 0)
                rc.OverridesInterface = iface;
            rc.OverridesInterfaces.Add(iface);
            rc.ImplementedInterfaces.Add(iface);
        }

        // Rule G: Default interface selection — first instance interface becomes default
        if (rc.DefaultInterface == null && rc.ImplementedInterfaces.Count > 0)
        {
            // Find the first non-factory, non-statics interface
            rc.DefaultInterface = rc.ImplementedInterfaces.FirstOrDefault(
                i => !rc.FactoryInterfaces.Contains(i) && !rc.StaticsInterfaces.Contains(i));
        }

        // Rule H-ext: Handle [default_interface] attribute on empty classes
        if (rc.IsDefaultInterface && rc.DefaultInterface == null)
        {
            var ifaceName = GetUniqueName($"I{rc.Name}", ns);
            var iface = CreateExclusiveInterface(
                ifaceName,
                ns,
                rc.Name,
                new List<ISymbol>(),
                rc.ContractName,
                rc.ContractVersion);
            rc.DefaultInterface = iface;
            rc.ImplementedInterfaces.Add(iface);
        }
    }

    private static InterfaceSymbol CreateExclusiveInterface(
        string name,
        NamespaceSymbol? ns,
        string className,
        List<ISymbol> members,
        string? contractName,
        int? contractVersion)
    {
        var iface = new InterfaceSymbol(name, ns)
        {
            IsExclusive = true,
            ContractName = contractName,
            ContractVersion = contractVersion,
        };
        // Store the FQN for ExclusiveTo attribute (original midl uses FQN)
        var fqn = ns != null && ns.FullName.Length > 0 ? $"{ns.FullName}.{className}" : className;
        iface.ExclusiveToClass = fqn;
        iface.IsSynthesized = true;

        foreach (var member in members)
        {
            iface.AddMember(member);
        }

        // Add to namespace so it's discoverable
        ns?.AddMember(iface);

        return iface;
    }

    private static List<(
        string? ContractName,
        string? ContractIdentity,
        int? ContractVersion,
        List<ISymbol> Members)>
        GroupMembersByContract(List<ISymbol> members, RuntimeClassSymbol runtimeClass)
    {
        var groups = new List<(
            string? ContractName,
            string? ContractIdentity,
            int? ContractVersion,
            List<ISymbol> Members)>();
        foreach (var member in members)
        {
            var (contractName, contractVersion) = GetMemberContract(member);
            contractName ??= runtimeClass.ContractName;
            contractVersion ??= runtimeClass.ContractVersion;
            var contractIdentity = GetContractIdentity(contractName, runtimeClass);

            var groupIndex = groups.FindIndex(group =>
                string.Equals(group.ContractIdentity, contractIdentity, StringComparison.OrdinalIgnoreCase)
                && group.ContractVersion == contractVersion);
            if (groupIndex < 0)
            {
                groups.Add((
                    contractName,
                    contractIdentity,
                    contractVersion,
                    new List<ISymbol> { member }));
            }
            else
            {
                groups[groupIndex].Members.Add(member);
            }
        }

        return groups;
    }

    private static string? GetContractIdentity(
        string? contractName,
        RuntimeClassSymbol runtimeClass)
    {
        if (contractName == null || contractName.Contains('.'))
            return contractName;

        return runtimeClass.ContainingSymbol is NamespaceSymbol { FullName.Length: > 0 } ns
            ? $"{ns.FullName}.{contractName}"
            : contractName;
    }

    private static (string? ContractName, int? ContractVersion) GetMemberContract(ISymbol member)
    {
        return member switch
        {
            MethodSymbol method => (method.ContractName, method.ContractVersion),
            PropertySymbol property => (property.ContractName, property.ContractVersion),
            EventSymbol evt => (evt.ContractName, evt.ContractVersion),
            ConstructorSymbol constructor => (constructor.ContractName, constructor.ContractVersion),
            _ => (null, null),
        };
    }

    /// <summary>
    /// Rule H: If the candidate name collides with an existing type in the namespace,
    /// append 2, 3, 4... until unique.
    /// </summary>
    private static string GetUniqueName(string candidate, NamespaceSymbol? ns)
    {
        if (ns == null) return candidate;

        var existingNames = new HashSet<string>(
            ns.Members.Select(m => m.Name), StringComparer.Ordinal);

        if (!existingNames.Contains(candidate))
            return candidate;

        for (int i = 2; ; i++)
        {
            var numbered = $"{candidate}{i}";
            if (!existingNames.Contains(numbered))
                return numbered;
        }
    }

    /// <summary>
    /// Extracts the simple (unqualified) name from a possibly fully-qualified name.
    /// E.g. "Namespace.IFooFactory" → "IFooFactory".
    /// </summary>
    private static string ExtractSimpleName(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot >= 0 ? qualifiedName.Substring(lastDot + 1) : qualifiedName;
    }
}
