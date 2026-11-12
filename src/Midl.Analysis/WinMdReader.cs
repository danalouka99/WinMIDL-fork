// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Midl.Analysis.Symbols;
using Midl.Syntax;

namespace Midl.Analysis;

/// <summary>
/// Reads WinRT types from .winmd files and converts them to MIDL symbols.
/// </summary>
public class WinMdReader
{
    private const int TdWindowsRuntime = 0x4000;

    /// <summary>Read all public WinRT types from a .winmd file path.</summary>
    public IReadOnlyList<ISymbol> ReadTypes(string winmdPath)
    {
        var bytes = File.ReadAllBytes(winmdPath);
        return ReadTypes(bytes, winmdPath);
    }

    /// <summary>Read all public WinRT types from raw bytes.</summary>
    public IReadOnlyList<ISymbol> ReadTypes(byte[] winmdBytes)
    {
        return ReadTypes(winmdBytes, null);
    }

    private IReadOnlyList<ISymbol> ReadTypes(byte[] winmdBytes, string? sourcePath)
    {
        var symbols = new List<ISymbol>();

        using var peReader = new PEReader(new MemoryStream(winmdBytes));
        // Raw metadata: the default options apply the WinRT->CLR projection, which renames
        // referenced types in signatures (TypeName -> System.Type, IVector`1 -> IList`1, ...).
        // Those CLR names must never reach WinMIDL's output.
        var mdReader = peReader.GetMetadataReader(MetadataReaderOptions.None);

        foreach (var typeDefHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeDefHandle);

            // Skip <Module>
            var name = mdReader.GetString(typeDef.Name);
            if (name == "<Module>") continue;

            // Index all WinRT-flagged types regardless of CLR visibility
            var attrs = typeDef.Attributes;
            if (((int)attrs & TdWindowsRuntime) == 0) continue;

            var ns = mdReader.GetString(typeDef.Namespace);
            var symbol = ReadTypeDefAsSymbol(mdReader, typeDef, typeDefHandle, name, ns, sourcePath);
            if (symbol != null)
                symbols.Add(symbol);
        }

        return symbols;
    }

    /// <summary>Read a single TypeDef from an already-open MetadataReader (used by lazy loading).</summary>
    internal static ISymbol? ReadSingleType(MetadataReader mdReader, TypeDefinitionHandle handle, string? sourcePath)
    {
        var typeDef = mdReader.GetTypeDefinition(handle);
        var name = mdReader.GetString(typeDef.Name);
        if (name == "<Module>") return null;

        // Process all WinRT-flagged types regardless of CLR visibility
        var attrs = typeDef.Attributes;
        if (((int)attrs & TdWindowsRuntime) == 0) return null;

        var ns = mdReader.GetString(typeDef.Namespace);
        return ReadTypeDefAsSymbol(mdReader, typeDef, handle, name, ns, sourcePath);
    }

    internal static ISymbol? ReadTypeDefAsSymbol(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var baseTypeHandle = typeDef.BaseType;
        var baseTypeName = GetBaseTypeName(mdReader, baseTypeHandle);

        return baseTypeName switch
        {
            "System.Enum" => ReadEnum(mdReader, typeDef, handle, name, ns, sourcePath),
            "System.ValueType" =>
                HasCustomAttribute(mdReader, handle, "Windows.Foundation.Metadata", "ApiContractAttribute")
                    ? ReadApiContract(mdReader, typeDef, handle, name, ns, sourcePath)
                    : (ISymbol)ReadStruct(mdReader, typeDef, handle, name, ns, sourcePath),
            "System.MulticastDelegate" => ReadDelegate(mdReader, typeDef, handle, name, ns, sourcePath),
            "System.Attribute" => ReadAttributeType(mdReader, typeDef, name, ns, sourcePath),
            _ when (typeDef.Attributes & TypeAttributes.Interface) != 0
                => ReadInterface(mdReader, typeDef, handle, name, ns, sourcePath),
            _ => ReadRuntimeClass(mdReader, typeDef, handle, name, ns, sourcePath),
        };
    }

    private static ApiContractSymbol ReadApiContract(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        return new ApiContractSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };
    }

    internal static string? GetBaseTypeName(MetadataReader mdReader, EntityHandle baseTypeHandle)
    {
        if (baseTypeHandle.IsNil) return null;

        return baseTypeHandle.Kind switch
        {
            HandleKind.TypeReference => GetTypeRefFullName(mdReader, (TypeReferenceHandle)baseTypeHandle),
            HandleKind.TypeDefinition => GetTypeDefFullName(mdReader, (TypeDefinitionHandle)baseTypeHandle),
            _ => null,
        };
    }

    internal static string GetTypeRefFullName(MetadataReader mdReader, TypeReferenceHandle handle)
    {
        var typeRef = mdReader.GetTypeReference(handle);
        var ns = mdReader.GetString(typeRef.Namespace);
        var name = mdReader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    internal static string GetTypeDefFullName(MetadataReader mdReader, TypeDefinitionHandle handle)
    {
        var typeDef = mdReader.GetTypeDefinition(handle);
        var ns = mdReader.GetString(typeDef.Namespace);
        var name = mdReader.GetString(typeDef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    #region Enum

    private static EnumSymbol ReadEnum(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        // Create a placeholder namespace parent for FullName
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new EnumSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        // Read fields
        foreach (var fieldHandle in typeDef.GetFields())
        {
            var field = mdReader.GetFieldDefinition(fieldHandle);
            var fieldName = mdReader.GetString(field.Name);
            if (fieldName == "value__")
            {
                // Determine underlying type from the value__ field signature
                symbol.UnderlyingType = DecodeFieldUnderlyingType(mdReader, field);
                continue;
            }

            var member = new EnumMemberSymbol(fieldName, symbol)
            {
                IsExternal = true,
                SourceWinMdPath = sourcePath,
            };
            symbol.AddMember(member);
        }

        // Check for [Flags]
        symbol.IsFlags = HasCustomAttribute(mdReader, handle, "System", "FlagsAttribute");

        return symbol;
    }

    private static string DecodeFieldUnderlyingType(MetadataReader mdReader, FieldDefinition field)
    {
        var blob = mdReader.GetBlobReader(field.Signature);
        // Field signature: FIELD (0x06) followed by type
        var header = blob.ReadSignatureHeader();
        var typeCode = blob.ReadCompressedInteger();
        return MapElementTypeToMidl(typeCode);
    }

    #endregion

    #region Struct

    private static StructSymbol ReadStruct(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new StructSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        foreach (var fieldHandle in typeDef.GetFields())
        {
            var field = mdReader.GetFieldDefinition(fieldHandle);
            var fieldName = mdReader.GetString(field.Name);

            var typeName = DecodeFieldType(mdReader, field);
            var fieldSymbol = new FieldSymbol(fieldName, symbol)
            {
                Type = ParseTypeRef(typeName),
                IsExternal = true,
                SourceWinMdPath = sourcePath,
            };
            symbol.AddField(fieldSymbol);
        }

        return symbol;
    }

    private static string DecodeFieldType(MetadataReader mdReader, FieldDefinition field)
    {
        var blob = mdReader.GetBlobReader(field.Signature);
        var header = blob.ReadSignatureHeader();
        return DecodeType(mdReader, ref blob);
    }

    #endregion

    #region Interface

    private static InterfaceSymbol ReadInterface(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new InterfaceSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        // Read GUID
        symbol.Uuid = ReadGuidAttribute(mdReader, handle);

        // Read required interfaces (InterfaceImpl rows)
        foreach (var ifaceImplHandle in typeDef.GetInterfaceImplementations())
        {
            var ifaceImpl = mdReader.GetInterfaceImplementation(ifaceImplHandle);
            var ifaceName = GetEntityName(mdReader, ifaceImpl.Interface);
            if (!string.IsNullOrEmpty(ifaceName))
            {
                var ifaceTypeRef = ParseTypeRef(ifaceName);
                symbol.RequiredInterfaces.Add(ifaceTypeRef);
            }
        }

        // Read methods
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = mdReader.GetMethodDefinition(methodHandle);
            var methodName = mdReader.GetString(method.Name);

            // Skip accessors — they will be represented by properties/events
            if (methodName.StartsWith("get_") || methodName.StartsWith("put_") ||
                methodName.StartsWith("add_") || methodName.StartsWith("remove_"))
                continue;

            var methodSymbol = ReadMethod(mdReader, method, methodName, symbol, sourcePath);
            symbol.AddMember(methodSymbol);
        }

        // Read properties
        foreach (var propHandle in typeDef.GetProperties())
        {
            var prop = mdReader.GetPropertyDefinition(propHandle);
            var propSymbol = ReadProperty(mdReader, prop, typeDef, symbol, sourcePath);
            symbol.AddMember(propSymbol);
        }

        // Read events
        foreach (var eventHandle in typeDef.GetEvents())
        {
            var evt = mdReader.GetEventDefinition(eventHandle);
            var evtSymbol = ReadEvent(mdReader, evt, symbol, sourcePath);
            symbol.AddMember(evtSymbol);
        }

        // Read custom attributes (Version, Deprecated, ExclusiveTo, etc.)
        ApplyCustomAttributes(mdReader, handle, symbol);

        return symbol;
    }

    #endregion

    #region Delegate

    private static DelegateSymbol ReadDelegate(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new DelegateSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        // Find the Invoke method
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = mdReader.GetMethodDefinition(methodHandle);
            var methodName = mdReader.GetString(method.Name);
            if (methodName != "Invoke") continue;

            var sig = method.DecodeSignature(new MidlSignatureTypeProvider(mdReader), null);

            symbol.ReturnType = ParseTypeRef(sig.ReturnType);

            foreach (var paramHandle in method.GetParameters())
            {
                var param = mdReader.GetParameter(paramHandle);
                if (param.SequenceNumber == 0) continue; // return type pseudo-param
                var paramName = mdReader.GetString(param.Name);
                var paramType = param.SequenceNumber <= sig.ParameterTypes.Length
                    ? sig.ParameterTypes[param.SequenceNumber - 1]
                    : "Object";
                var paramSymbol = new ParameterSymbol(paramName, symbol)
                {
                    Type = ParseTypeRef(paramType),
                    IsExternal = true,
                    SourceWinMdPath = sourcePath,
                };
                symbol.Parameters.Add(paramSymbol);
            }
            break;
        }

        // Read GUID
        symbol.Uuid = ReadGuidAttribute(mdReader, handle);

        return symbol;
    }

    #endregion

    #region RuntimeClass

    private static RuntimeClassSymbol ReadRuntimeClass(
        MetadataReader mdReader, TypeDefinition typeDef, TypeDefinitionHandle handle,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new RuntimeClassSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
            // A class without the Sealed flag is unsealed (composable)
            IsUnsealed = (typeDef.Attributes & TypeAttributes.Sealed) == 0,
        };

        // Read implemented interfaces
        foreach (var ifaceImplHandle in typeDef.GetInterfaceImplementations())
        {
            var ifaceImpl = mdReader.GetInterfaceImplementation(ifaceImplHandle);
            var ifaceName = GetEntityName(mdReader, ifaceImpl.Interface);
            if (!string.IsNullOrEmpty(ifaceName))
            {
                var ifaceTypeRef = ParseTypeRef(ifaceName);

                // Read InterfaceImpl custom attributes (Default, Protected, Overridable)
                foreach (var attrHandle in ifaceImpl.GetCustomAttributes())
                {
                    var attr = mdReader.GetCustomAttribute(attrHandle);
                    var attrName = GetAttributeTypeName(mdReader, attr.Constructor);
                    switch (attrName)
                    {
                        case "DefaultAttribute":
                            ifaceTypeRef.IsDefaultInterface = true;
                            break;
                        case "ProtectedAttribute":
                            ifaceTypeRef.IsProtectedInterface = true;
                            break;
                        case "OverridableAttribute":
                            ifaceTypeRef.IsOverridableInterface = true;
                            break;
                    }
                }

                symbol.Interfaces.Add(ifaceTypeRef);
            }
        }

        // Read GUID
        symbol.Uuid = ReadGuidAttribute(mdReader, handle);

        // Read custom attributes (Version, Deprecated, Threading, Marshaling, etc.)
        ApplyCustomAttributes(mdReader, handle, symbol);

        return symbol;
    }

    #endregion

    #region Member helpers

    private static MethodSymbol ReadMethod(
        MetadataReader mdReader, MethodDefinition method, string name,
        ISymbol parent, string? sourcePath)
    {
        var sig = method.DecodeSignature(new MidlSignatureTypeProvider(mdReader), null);
        var symbol = new MethodSymbol(name, parent)
        {
            ReturnType = ParseTypeRef(sig.ReturnType),
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        foreach (var paramHandle in method.GetParameters())
        {
            var param = mdReader.GetParameter(paramHandle);
            if (param.SequenceNumber == 0) continue;
            var paramName = mdReader.GetString(param.Name);
            var paramType = param.SequenceNumber <= sig.ParameterTypes.Length
                ? sig.ParameterTypes[param.SequenceNumber - 1]
                : "Object";

            // Detect byref parameters from signature (trailing '&')
            bool isByRef = paramType.EndsWith('&');
            if (isByRef)
                paramType = paramType[..^1];

            var direction = ParameterDirection.In;
            if ((param.Attributes & ParameterAttributes.Out) != 0)
                direction = ParameterDirection.Out;
            else if (isByRef)
                direction = ParameterDirection.RefConst;

            symbol.Parameters.Add(new ParameterSymbol(paramName, symbol)
            {
                Type = ParseTypeRef(paramType),
                Direction = direction,
                IsExternal = true,
                SourceWinMdPath = sourcePath,
            });
        }

        // Overload metadata, so members flattened onto a runtime class keep it (as midl.exe does).
        // Same convention as [overload("...")] in IDL: OverloadGroup holds the OverloadAttribute value.
        foreach (var attrHandle in method.GetCustomAttributes())
        {
            var attr = mdReader.GetCustomAttribute(attrHandle);
            switch (GetAttributeTypeName(mdReader, attr.Constructor))
            {
                case "OverloadAttribute":
                    symbol.OverloadGroup = ReadFirstStringArgument(mdReader, attr) ?? symbol.OverloadGroup;
                    break;
                case "DefaultOverloadAttribute":
                    symbol.IsDefaultOverload = true;
                    break;
            }
        }

        return symbol;
    }

    private static string? ReadFirstStringArgument(MetadataReader mdReader, CustomAttribute attr)
    {
        var blob = mdReader.GetBlobReader(attr.Value);
        if (blob.Length < 3 || blob.ReadUInt16() != 1) return null; // prolog
        try { return blob.ReadSerializedString(); }
        catch (BadImageFormatException) { return null; }
    }

    private static PropertySymbol ReadProperty(
        MetadataReader mdReader, PropertyDefinition prop,
        TypeDefinition containingType, ISymbol parent, string? sourcePath)
    {
        var propName = mdReader.GetString(prop.Name);
        var propSig = prop.DecodeSignature(new MidlSignatureTypeProvider(mdReader), null);

        var symbol = new PropertySymbol(propName, parent)
        {
            Type = ParseTypeRef(propSig.ReturnType),
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        // Determine getter/setter by checking for accessor methods
        var accessors = prop.GetAccessors();
        symbol.HasGetter = !accessors.Getter.IsNil;
        symbol.HasSetter = !accessors.Setter.IsNil;

        return symbol;
    }

    private static EventSymbol ReadEvent(
        MetadataReader mdReader, EventDefinition evt,
        ISymbol parent, string? sourcePath)
    {
        var eventName = mdReader.GetString(evt.Name);
        var delegateTypeName = GetEntityName(mdReader, evt.Type);

        return new EventSymbol(eventName, parent)
        {
            DelegateType = ParseTypeRef(delegateTypeName ?? "Object"),
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };
    }

    #endregion

    #region Attribute

    private static AttributeTypeSymbol ReadAttributeType(
        MetadataReader mdReader, TypeDefinition typeDef,
        string name, string ns, string? sourcePath)
    {
        var nsSymbol = CreateNamespaceChain(ns);
        var symbol = new AttributeTypeSymbol(name, nsSymbol)
        {
            IsExternal = true,
            SourceWinMdPath = sourcePath,
        };

        // Read custom attributes on the attribute type (attribute_name, attributeusage)
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = mdReader.GetCustomAttribute(attrHandle);
            var attrName = GetAttributeTypeName(mdReader, attr.Constructor);
            if (attrName == null) continue;

            var blob = mdReader.GetBlobReader(attr.Value);
            if (blob.Length < 2) continue;
            blob.ReadUInt16(); // prolog

            switch (attrName)
            {
                case "AttributeNameAttribute":
                    // [attribute_name("short_name")] — the display name used in IDL
                    try
                    {
                        if (blob.RemainingBytes >= 4)
                        {
                            var shortName = blob.ReadSerializedString();
                            if (!string.IsNullOrEmpty(shortName))
                                symbol.AttributeNameOverride = shortName;
                        }
                    }
                    catch { }
                    break;

                case "AttributeUsageAttribute":
                    // Metadata is read raw (no WinRT projection), so the value is whatever the WinMD
                    // stores: Windows.Foundation.Metadata.AttributeTargets flags for WinRT attributes,
                    // System.AttributeTargets flags for System.AttributeUsageAttribute.
                    try
                    {
                        if (blob.RemainingBytes >= 4)
                        {
                            var targets = blob.ReadInt32();
                            if (GetAttributeTypeNamespace(mdReader, attr.Constructor) == "System")
                                ApplyClrAttributeUsageTargets(symbol, targets);
                            else
                                ApplyAttributeUsageTargets(symbol, targets);
                        }
                    }
                    catch { }
                    break;
            }
        }

        // Read fields from the .ctor parameters (attribute ctor params = attribute fields in WinRT)
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = mdReader.GetMethodDefinition(methodHandle);
            var methodName = mdReader.GetString(method.Name);
            if (methodName != ".ctor") continue;

            foreach (var paramHandle in method.GetParameters())
            {
                var param = mdReader.GetParameter(paramHandle);
                if (param.SequenceNumber == 0) continue; // return type pseudo-param
                var paramName = mdReader.GetString(param.Name);

                var sig = method.DecodeSignature(new MidlSignatureTypeProvider(mdReader), null);
                var paramType = param.SequenceNumber <= sig.ParameterTypes.Length
                    ? sig.ParameterTypes[param.SequenceNumber - 1]
                    : "Object";

                var fieldSymbol = new FieldSymbol(paramName, symbol)
                {
                    Type = ParseTypeRef(paramType),
                    IsExternal = true,
                    SourceWinMdPath = sourcePath,
                };
                symbol.AddField(fieldSymbol);
            }
            break; // use the first .ctor
        }

        return symbol;
    }

    /// <summary>
    /// Maps CLR System.AttributeTargets enum flags to MIDL target strings.
    /// Used when the attribute has System.AttributeUsageAttribute (not WinRT).
    /// </summary>
    private static void ApplyClrAttributeUsageTargets(AttributeTypeSymbol symbol, int targets)
    {
        const int ClrClass = 0x0004;        // → runtimeclass
        const int ClrStruct = 0x0008;       // → struct
        const int ClrEnum = 0x0010;         // → enum
        const int ClrDelegate = 0x1000;     // → delegate
        const int ClrInterface = 0x0400;    // → interface
        const int ClrMethod = 0x0040;       // → method
        const int ClrProperty = 0x0100;     // → property
        const int ClrField = 0x0200;        // → field
        const int ClrEvent = 0x0800;        // → event
        const int ClrParameter = 0x0080;    // → parameter

        if (targets == -1 || targets == 0x7FFF) return; // All — don't restrict

        if ((targets & ClrClass) != 0) symbol.ValidTargets.Add("target_runtimeclass");
        if ((targets & ClrStruct) != 0) symbol.ValidTargets.Add("target_struct");
        if ((targets & ClrEnum) != 0) symbol.ValidTargets.Add("target_enum");
        if ((targets & ClrDelegate) != 0) symbol.ValidTargets.Add("target_delegate");
        if ((targets & ClrInterface) != 0) symbol.ValidTargets.Add("target_interface");
        if ((targets & ClrMethod) != 0) symbol.ValidTargets.Add("target_method");
        if ((targets & ClrProperty) != 0) symbol.ValidTargets.Add("target_property");
        if ((targets & ClrField) != 0) symbol.ValidTargets.Add("target_field");
        if ((targets & ClrEvent) != 0) symbol.ValidTargets.Add("target_event");
        if ((targets & ClrParameter) != 0) symbol.ValidTargets.Add("target_parameter");
    }

    /// <summary>
    /// Maps WinRT AttributeTargets enum flags to MIDL target strings.
    /// </summary>
    private static void ApplyAttributeUsageTargets(AttributeTypeSymbol symbol, int targets)
    {
        // WinRT AttributeTargets flags (from Windows.Foundation.Metadata.AttributeTargets)
        const int TargetDelegate = 1;
        const int TargetEnum = 2;
        const int TargetEvent = 4;
        const int TargetField = 8;
        const int TargetInterface = 16;
        const int TargetMethod = 64;
        const int TargetParameter = 128;
        const int TargetProperty = 256;
        const int TargetRuntimeClass = 512;
        const int TargetStruct = 1024;
        const int TargetApiContract = 8192;

        if (targets == -1) return; // All: don't restrict

        if ((targets & TargetDelegate) != 0) symbol.ValidTargets.Add("target_delegate");
        if ((targets & TargetEnum) != 0) symbol.ValidTargets.Add("target_enum");
        if ((targets & TargetEvent) != 0) symbol.ValidTargets.Add("target_event");
        if ((targets & TargetField) != 0) symbol.ValidTargets.Add("target_field");
        if ((targets & TargetInterface) != 0) symbol.ValidTargets.Add("target_interface");
        if ((targets & TargetMethod) != 0) symbol.ValidTargets.Add("target_method");
        if ((targets & TargetParameter) != 0) symbol.ValidTargets.Add("target_parameter");
        if ((targets & TargetProperty) != 0) symbol.ValidTargets.Add("target_property");
        if ((targets & TargetRuntimeClass) != 0) symbol.ValidTargets.Add("target_runtimeclass");
        if ((targets & TargetStruct) != 0) symbol.ValidTargets.Add("target_struct");
        if ((targets & TargetApiContract) != 0) symbol.ValidTargets.Add("target_apicontract");
    }

    #endregion

    #region Type decoding

    private static string DecodeType(MetadataReader mdReader, ref BlobReader blob)
    {
        int typeCode = blob.ReadCompressedInteger();
        return typeCode switch
        {
            // TypeDef/TypeRef encoded
            0x11 or 0x12 => DecodeTypeDefOrRefEncoded(mdReader, ref blob),
            _ => MapElementTypeToMidl(typeCode),
        };
    }

    private static string DecodeTypeDefOrRefEncoded(MetadataReader mdReader, ref BlobReader blob)
    {
        int coded = blob.ReadCompressedInteger();
        int tag = coded & 0x3;
        int index = coded >> 2;

        if (tag == 0) // TypeDef
        {
            var handle = MetadataTokens.TypeDefinitionHandle(index);
            return GetTypeDefFullName(mdReader, handle);
        }
        else if (tag == 1) // TypeRef
        {
            var handle = MetadataTokens.TypeReferenceHandle(index);
            return GetTypeRefFullName(mdReader, handle);
        }

        return "Object";
    }

    private static string MapElementTypeToMidl(int elementType) => elementType switch
    {
        0x01 => "void",
        0x02 => "Boolean",
        0x03 => "Char",
        0x04 => "Int8",
        0x05 => "UInt8",
        0x06 => "Int16",
        0x07 => "UInt16",
        0x08 => "Int32",
        0x09 => "UInt32",
        0x0A => "Int64",
        0x0B => "UInt64",
        0x0C => "Single",
        0x0D => "Double",
        0x0E => "String",
        0x1C => "Object",
        _ => "Object",
    };

    internal static string GetEntityName(MetadataReader mdReader, EntityHandle handle)
    {
        if (handle.IsNil) return string.Empty;
        return handle.Kind switch
        {
            HandleKind.TypeReference => GetTypeRefFullName(mdReader, (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => GetTypeDefFullName(mdReader, (TypeDefinitionHandle)handle),
            HandleKind.TypeSpecification => DecodeTypeSpec(mdReader, (TypeSpecificationHandle)handle),
            _ => string.Empty,
        };
    }

    private static string DecodeTypeSpec(MetadataReader mdReader, TypeSpecificationHandle handle)
    {
        var spec = mdReader.GetTypeSpecification(handle);
        return spec.DecodeSignature(new MidlSignatureTypeProvider(mdReader), null);
    }

    #endregion

    #region Attribute helpers

    private static Guid? ReadGuidAttribute(MetadataReader mdReader, TypeDefinitionHandle handle)
    {
        var typeDef = mdReader.GetTypeDefinition(handle);
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = mdReader.GetCustomAttribute(attrHandle);
            var ctorName = GetAttributeTypeName(mdReader, attr.Constructor);
            if (ctorName != "GuidAttribute") continue;

            var blob = mdReader.GetBlobReader(attr.Value);
            if (blob.Length < 2 + 16) continue;
            blob.ReadUInt16(); // prolog

            uint a = blob.ReadUInt32();
            ushort b = blob.ReadUInt16();
            ushort c = blob.ReadUInt16();
            byte d = blob.ReadByte();
            byte e = blob.ReadByte();
            byte f = blob.ReadByte();
            byte g = blob.ReadByte();
            byte h = blob.ReadByte();
            byte i = blob.ReadByte();
            byte j = blob.ReadByte();
            byte k = blob.ReadByte();

            return new Guid(a, b, c, d, e, f, g, h, i, j, k);
        }
        return null;
    }

    /// <summary>
    /// Reads well-known custom attributes from a type definition and applies them to a symbol.
    /// Handles: VersionAttribute, ContractVersionAttribute, DeprecatedAttribute,
    /// ExclusiveToAttribute, ThreadingAttribute, MarshalingBehaviorAttribute.
    /// </summary>
    private static void ApplyCustomAttributes(MetadataReader mdReader, TypeDefinitionHandle handle, ISymbol symbol)
    {
        var typeDef = mdReader.GetTypeDefinition(handle);
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = mdReader.GetCustomAttribute(attrHandle);
            var attrName = GetAttributeTypeName(mdReader, attr.Constructor);
            if (attrName == null) continue;

            // Some attributes only need presence detection, no blob decoding
            if (attrName == "ComposableAttribute")
            {
                if (symbol is RuntimeClassSymbol rcComp)
                {
                    rcComp.IsUnsealed = true;
                    MidlTrace.Binder($"ComposableAttribute → {symbol.Name}.IsUnsealed = true");
                }
                continue;
            }

            var blob = mdReader.GetBlobReader(attr.Value);
            if (blob.Length < 2) continue;
            blob.ReadUInt16(); // prolog

            switch (attrName)
            {
                case "VersionAttribute":
                    if (blob.RemainingBytes >= 4)
                    {
                        var version = (int)blob.ReadUInt32();
                        if (symbol is RuntimeClassSymbol rc) rc.Version = version;
                        else if (symbol is InterfaceSymbol iface) iface.Version = version;
                        else if (symbol is DelegateSymbol del) del.Version = version;
                    }
                    break;

                case "ContractVersionAttribute":
                    // ContractVersionAttribute can have (String contractName, UInt32 version)
                    // or just (UInt32 version) — try to read string first
                    try
                    {
                        if (blob.RemainingBytes >= 4)
                        {
                            // Try to read as (string, uint32)
                            var savedOffset = blob.Offset;
                            string? contractName = TryReadSerializedString(ref blob);
                            if (contractName != null && blob.RemainingBytes >= 4)
                            {
                                var contractVer = (int)blob.ReadUInt32();
                                if (symbol is RuntimeClassSymbol rc2) { rc2.ContractName = contractName; rc2.ContractVersion = contractVer; }
                                else if (symbol is InterfaceSymbol iface2) { iface2.ContractName = contractName; iface2.ContractVersion = contractVer; }
                            }
                            else
                            {
                                // Rewind and read as just uint32
                                blob.Reset();
                                blob.ReadUInt16(); // re-read prolog
                                var contractVer = (int)blob.ReadUInt32();
                                if (symbol is RuntimeClassSymbol rc3) rc3.ContractVersion = contractVer;
                                else if (symbol is InterfaceSymbol iface3) iface3.ContractVersion = contractVer;
                            }
                        }
                    }
                    catch { /* best-effort attribute decoding */ }
                    break;

                case "DeprecatedAttribute":
                    try
                    {
                        var message = TryReadSerializedString(ref blob);
                        if (message != null)
                        {
                            if (symbol is RuntimeClassSymbol rc4) rc4.DeprecatedMessage = message;
                            else if (symbol is InterfaceSymbol iface4) iface4.DeprecatedMessage = message;
                            else if (symbol is DelegateSymbol del2) del2.DeprecatedMessage = message;
                        }
                    }
                    catch { /* best-effort */ }
                    break;

                case "ExclusiveToAttribute":
                    try
                    {
                        // The argument is a System.Type, encoded as a serialized string of the type name
                        var typeName = TryReadSerializedString(ref blob);
                        if (typeName != null && symbol is InterfaceSymbol iface5)
                        {
                            iface5.IsExclusive = true;
                            iface5.ExclusiveToClass = typeName;
                        }
                    }
                    catch { /* best-effort */ }
                    break;

                case "ThreadingAttribute":
                    if (blob.RemainingBytes >= 4)
                    {
                        var threadingValue = blob.ReadInt32();
                        // Windows.Foundation.Metadata.ThreadingModel: STA=1, MTA=2, Both=3
                        var threadingModel = threadingValue switch
                        {
                            1 => "STA",
                            2 => "MTA",
                            3 => "Both",
                            _ => null
                        };
                        if (threadingModel != null && symbol is RuntimeClassSymbol rc5)
                            rc5.ThreadingModel = threadingModel;
                    }
                    break;

                case "MarshalingBehaviorAttribute":
                    if (blob.RemainingBytes >= 4)
                    {
                        var marshalingValue = blob.ReadInt32();
                        // Windows.Foundation.Metadata.MarshalingType: None=1, Agile=2, Standard=3
                        // But emitter uses: none=0, standard=1, agile=2 — keep compatible with emitter
                        var marshalingBehavior = marshalingValue switch
                        {
                            0 => "None",
                            1 => "Standard",
                            2 => "Agile",
                            _ => null
                        };
                        if (marshalingBehavior != null && symbol is RuntimeClassSymbol rc6)
                            rc6.MarshalingBehavior = marshalingBehavior;
                    }
                    break;

#if FEATURE_STAGING
                case "FeatureAttribute":
                    // FeatureAttribute(FeatureStage stage, bool validInAllBranches)
                    if (blob.RemainingBytes >= 5)
                    {
                        var stageValue = blob.ReadInt32();
                        var validInAllBranches = blob.ReadBoolean();
                        var featureLabel = $"__feature_{stageValue}_{(validInAllBranches ? "1" : "0")}";
                        if (symbol is RuntimeClassSymbol rcF) rcF.FeatureName = featureLabel;
                        else if (symbol is InterfaceSymbol ifaceF) ifaceF.FeatureName = featureLabel;
                        else if (symbol is EnumSymbol enF) enF.FeatureName = featureLabel;
                        else if (symbol is StructSymbol stF) stF.FeatureName = featureLabel;
                        else if (symbol is DelegateSymbol delF) delF.FeatureName = featureLabel;
                    }
                    break;
#endif
            }
        }
    }

    /// <summary>Tries to read a serialized string from a custom attribute blob.</summary>
    private static string? TryReadSerializedString(ref BlobReader blob)
    {
        if (blob.RemainingBytes == 0) return null;

        // 0xFF = null string, otherwise length-prefixed UTF-8
        var firstByte = blob.ReadByte();
        if (firstByte == 0xFF) return null;

        // The first byte is the start of a compressed length
        int length;
        if ((firstByte & 0x80) == 0)
        {
            length = firstByte;
        }
        else if ((firstByte & 0xC0) == 0x80)
        {
            if (blob.RemainingBytes < 1) return null;
            length = ((firstByte & 0x3F) << 8) | blob.ReadByte();
        }
        else
        {
            if (blob.RemainingBytes < 3) return null;
            length = ((firstByte & 0x1F) << 24) | (blob.ReadByte() << 16) | (blob.ReadByte() << 8) | blob.ReadByte();
        }

        if (blob.RemainingBytes < length) return null;
        var bytes = blob.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static bool HasCustomAttribute(MetadataReader mdReader, TypeDefinitionHandle handle, string ns, string attrTypeName)
    {
        var typeDef = mdReader.GetTypeDefinition(handle);
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = mdReader.GetCustomAttribute(attrHandle);
            var name = GetAttributeTypeName(mdReader, attr.Constructor);
            if (name == attrTypeName) return true;
        }
        return false;
    }

    private static string? GetAttributeTypeName(MetadataReader mdReader, EntityHandle ctorHandle)
    {
        if (ctorHandle.Kind == HandleKind.MemberReference)
        {
            var memberRef = mdReader.GetMemberReference((MemberReferenceHandle)ctorHandle);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = mdReader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                return mdReader.GetString(typeRef.Name);
            }
        }
        return null;
    }

    private static string? GetAttributeTypeNamespace(MetadataReader mdReader, EntityHandle ctorHandle)
    {
        if (ctorHandle.Kind == HandleKind.MemberReference)
        {
            var memberRef = mdReader.GetMemberReference((MemberReferenceHandle)ctorHandle);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = mdReader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                return mdReader.GetString(typeRef.Namespace);
            }
        }
        return null;
    }

    #endregion

    #region Namespace helpers

    private static NamespaceSymbol CreateNamespaceChain(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return new NamespaceSymbol("");

        var parts = ns.Split('.');
        var root = new NamespaceSymbol(parts[0]);
        var current = root;
        for (int i = 1; i < parts.Length; i++)
        {
            current = current.GetOrCreateChildNamespace(parts[i]);
        }
        return current;
    }

    private static bool IsBuiltInType(string name) => name switch
    {
        "Boolean" or "Char" or "Int16" or "Int32" or "Int64"
            or "UInt8" or "UInt16" or "UInt32" or "UInt64"
            or "Single" or "Double" or "String" or "Guid"
            or "Object" or "void" => true,
        _ => false,
    };

    /// <summary>
    /// Parses a decoded signature type name (which may contain generic instantiation syntax)
    /// into a structured TypeRef with type arguments.
    /// </summary>
    public static TypeRef ParseTypeRef(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return new TypeRef("Object");

        bool isArray = typeName.EndsWith("[]");
        if (isArray)
            typeName = typeName[..^2];

        // MidlSignatureTypeProvider marks ELEMENT_TYPE_VALUETYPE references.
        bool isValueType = typeName.StartsWith(ValueTypePrefix, StringComparison.Ordinal);
        if (isValueType)
            typeName = typeName[ValueTypePrefix.Length..];

        int angleBracket = typeName.IndexOf('<');
        if (angleBracket > 0 && typeName.EndsWith('>'))
        {
            var baseName = typeName[..angleBracket];
            var argsString = typeName[(angleBracket + 1)..^1];
            var typeArgs = ParseTypeArguments(argsString);
            return new TypeRef(baseName, typeArgs, isArray) { IsValueType = isValueType };
        }

        return new TypeRef(typeName, isArray: isArray) { IsValueType = isValueType };
    }

    private const string ValueTypePrefix = "valuetype ";

    private static List<TypeRef> ParseTypeArguments(string argsString)
    {
        var result = new List<TypeRef>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsString.Length; i++)
        {
            switch (argsString[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    var arg = argsString[start..i].Trim();
                    if (arg.Length > 0) result.Add(ParseTypeRef(arg));
                    start = i + 1;
                    break;
            }
        }
        var lastArg = argsString[start..].Trim();
        if (lastArg.Length > 0) result.Add(ParseTypeRef(lastArg));
        return result;
    }

    #endregion

    /// <summary>
    /// ISignatureTypeProvider that decodes types to string names.
    /// </summary>
    private sealed class MidlSignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _reader;

        public MidlSignatureTypeProvider(MetadataReader reader) => _reader = reader;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "Boolean",
            PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.SByte => "Int8",
            PrimitiveTypeCode.Byte => "UInt8",
            PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.UInt16 => "UInt16",
            PrimitiveTypeCode.Int32 => "Int32",
            PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.Int64 => "Int64",
            PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.Double => "Double",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.Object => "Object",
            PrimitiveTypeCode.IntPtr => "Int64",
            PrimitiveTypeCode.UIntPtr => "UInt64",
            _ => "Object",
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            var ns = reader.GetString(td.Namespace);
            var name = reader.GetString(td.Name);
            return Mark(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}", rawTypeKind);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            var ns = reader.GetString(tr.Namespace);
            var name = reader.GetString(tr.Name);
            return Mark(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}", rawTypeKind);
        }

        // ParseTypeRef turns the marker into TypeRef.IsValueType.
        private static string Mark(string fullName, byte rawTypeKind)
            => rawTypeKind == (byte)SignatureTypeKind.ValueType ? ValueTypePrefix + fullName : fullName;

        public string GetSZArrayType(string elementType) => $"{elementType}[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[]";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => $"{genericType}<{string.Join(", ", typeArguments)}>";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "Object";
        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var spec = reader.GetTypeSpecification(handle);
            return spec.DecodeSignature(this, genericContext);
        }
    }
}
