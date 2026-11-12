// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Midl.Analysis;

#pragma warning disable CS0169 // Field is never used (scaffolded for future use)

namespace Midl.CodeGen.WinMd;

/// <summary>
/// Encodes and emits WinRT custom attributes to the metadata tables.
/// </summary>
internal class CustomAttributeEmitter
{
    private readonly WinMdEmitter _emitter;
    private MetadataBuilder Metadata => _emitter.Metadata;

    // Lazy-initialized attribute constructor MemberRefs
    private MemberReferenceHandle? _flagsCtorRef;
    private MemberReferenceHandle? _activatableCtorRef;
    private MemberReferenceHandle? _activatableContractCtorRef;
    private MemberReferenceHandle? _defaultCtorRef;
    private MemberReferenceHandle? _guidCtorRef;
    private MemberReferenceHandle? _versionCtorRef;
    private MemberReferenceHandle? _contractVersionCtorRef;
    private MemberReferenceHandle? _contractVersionStringCtorRef;
    private MemberReferenceHandle? _exclusiveToCtorRef;
    private MemberReferenceHandle? _activatableFactoryCtorRef;
    private MemberReferenceHandle? _activatableFactoryContractCtorRef;
    private MemberReferenceHandle? _staticCtorRef;
    private MemberReferenceHandle? _staticContractCtorRef;
    private MemberReferenceHandle? _composableCtorRef;
    private MemberReferenceHandle? _threadingCtorRef;
    private MemberReferenceHandle? _marshalingCtorRef;
    private MemberReferenceHandle? _deprecatedCtorRef;
    private MemberReferenceHandle? _contractVersionNamedCtorRef;
    private MemberReferenceHandle? _overloadCtorRef;
    private MemberReferenceHandle? _defaultOverloadCtorRef;
    private MemberReferenceHandle? _overridableCtorRef;
    private MemberReferenceHandle? _protectedCtorRef;
    private MemberReferenceHandle? _experimentalCtorRef;
    private MemberReferenceHandle? _noExceptionCtorRef;
    private MemberReferenceHandle? _remoteAsyncCtorRef;
    private MemberReferenceHandle? _attributeUsageCtorRef;
    private MemberReferenceHandle? _apiContractCtorRef;
    private TypeReferenceHandle? _systemTypeRef;
    private readonly Dictionary<(AssemblyReferenceHandle Assembly, string Namespace, string Name), TypeReferenceHandle>
        _typeReferences = new();

    public CustomAttributeEmitter(WinMdEmitter emitter)
    {
        _emitter = emitter;
    }

    public void AddFlagsAttribute(EntityHandle parent)
    {
        _flagsCtorRef ??= CreateMscorlibCtorRef("System", "FlagsAttribute", Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _flagsCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddActivatableAttribute(
        EntityHandle parent,
        uint version = 1,
        string? contractName = null)
    {
        MemberReferenceHandle constructor;
        if (contractName == null)
        {
            _activatableCtorRef ??= CreateWfmCtorRef("ActivatableAttribute",
                new Action<SignatureTypeEncoder>[] { e => e.UInt32() });
            constructor = _activatableCtorRef.Value;
        }
        else
        {
            _activatableContractCtorRef ??= CreateWfmCtorRef("ActivatableAttribute",
                new Action<SignatureTypeEncoder>[] { e => e.UInt32(), e => e.String() });
            constructor = _activatableContractCtorRef.Value;
        }

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteUInt32(version);
        if (contractName != null)
            blob.WriteSerializedString(contractName);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, constructor, Metadata.GetOrAddBlob(blob));
    }

    public void AddDefaultAttribute(EntityHandle parent)
    {
        _defaultCtorRef ??= CreateWfmCtorRef("DefaultAttribute", Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _defaultCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddGuidAttribute(EntityHandle parent, Guid guid)
    {
        _guidCtorRef ??= CreateWfmCtorRef("GuidAttribute", new Action<SignatureTypeEncoder>[]
        {
            e => e.UInt32(), e => e.UInt16(), e => e.UInt16(),
            e => e.Byte(), e => e.Byte(), e => e.Byte(), e => e.Byte(),
            e => e.Byte(), e => e.Byte(), e => e.Byte(), e => e.Byte(),
        });

        var bytes = guid.ToByteArray();
        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteUInt32(BitConverter.ToUInt32(bytes, 0));
        blob.WriteUInt16(BitConverter.ToUInt16(bytes, 4));
        blob.WriteUInt16(BitConverter.ToUInt16(bytes, 6));
        for (int i = 8; i < 16; i++)
            blob.WriteByte(bytes[i]);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _guidCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddVersionAttribute(EntityHandle parent, uint version = 1)
    {
        _versionCtorRef ??= CreateWfmCtorRef("VersionAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.UInt32() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteUInt32(version);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _versionCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddContractVersionAttribute(EntityHandle parent, uint version)
    {
        _contractVersionCtorRef ??= CreateWfmCtorRef("ContractVersionAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.UInt32() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteUInt32(version);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _contractVersionCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddContractVersionAttribute(EntityHandle parent, string contractName, uint version)
    {
        _contractVersionNamedCtorRef ??= CreateWfmCtorRef("ContractVersionAttribute",
            new Action<SignatureTypeEncoder>[]
            {
                e => e.Type(GetSystemTypeRef(), isValueType: false),
                e => e.UInt32()
            });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(contractName);
        blob.WriteUInt32(version);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _contractVersionNamedCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddContractVersionAttributeByName(EntityHandle parent, string contractName, uint version)
    {
        _contractVersionStringCtorRef ??= CreateWfmCtorRef("ContractVersionAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.String(), e => e.UInt32() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(contractName);
        blob.WriteUInt32(version);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _contractVersionStringCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddExclusiveToAttribute(EntityHandle parent, string runtimeClassName)
    {
        _exclusiveToCtorRef ??= CreateWfmCtorRef("ExclusiveToAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.Type(GetSystemTypeRef(), isValueType: false) });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(runtimeClassName);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _exclusiveToCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddActivatableAttribute(
        EntityHandle parent,
        string factoryTypeName,
        uint version,
        string? contractName = null)
    {
        MemberReferenceHandle constructor;
        if (contractName == null)
        {
            _activatableFactoryCtorRef ??= CreateWfmCtorRef("ActivatableAttribute",
                new Action<SignatureTypeEncoder>[]
                {
                    e => e.Type(GetSystemTypeRef(), isValueType: false),
                    e => e.UInt32()
                });
            constructor = _activatableFactoryCtorRef.Value;
        }
        else
        {
            _activatableFactoryContractCtorRef ??= CreateWfmCtorRef("ActivatableAttribute",
                new Action<SignatureTypeEncoder>[]
                {
                    e => e.Type(GetSystemTypeRef(), isValueType: false),
                    e => e.UInt32(),
                    e => e.String()
                });
            constructor = _activatableFactoryContractCtorRef.Value;
        }

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(factoryTypeName);
        blob.WriteUInt32(version);
        if (contractName != null)
            blob.WriteSerializedString(contractName);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, constructor, Metadata.GetOrAddBlob(blob));
    }

    public void AddStaticAttribute(
        EntityHandle parent,
        string staticTypeName,
        uint version,
        string? contractName = null)
    {
        MemberReferenceHandle constructor;
        if (contractName == null)
        {
            _staticCtorRef ??= CreateWfmCtorRef("StaticAttribute",
                new Action<SignatureTypeEncoder>[]
                {
                    e => e.Type(GetSystemTypeRef(), isValueType: false),
                    e => e.UInt32()
                });
            constructor = _staticCtorRef.Value;
        }
        else
        {
            _staticContractCtorRef ??= CreateWfmCtorRef("StaticAttribute",
                new Action<SignatureTypeEncoder>[]
                {
                    e => e.Type(GetSystemTypeRef(), isValueType: false),
                    e => e.UInt32(),
                    e => e.String()
                });
            constructor = _staticContractCtorRef.Value;
        }

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(staticTypeName);
        blob.WriteUInt32(version);
        if (contractName != null)
            blob.WriteSerializedString(contractName);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, constructor, Metadata.GetOrAddBlob(blob));
    }

    public void AddComposableAttribute(EntityHandle parent, string factoryTypeName, int access, uint version)
    {
        _composableCtorRef ??= CreateWfmCtorRef("ComposableAttribute",
            new Action<SignatureTypeEncoder>[]
            {
                e => e.Type(GetSystemTypeRef(), isValueType: false),
                e => e.Int32(),
                e => e.UInt32(),
                e => e.String()
            });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(factoryTypeName);
        blob.WriteInt32(access);
        blob.WriteUInt32(version);
        blob.WriteSerializedString("");
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _composableCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddThreadingAttribute(EntityHandle parent, int threadingModel)
    {
        var threadingModelType = GetOrAddTypeReference(
            _emitter.WfmRef,
            MetadataConstants.WindowsFoundationMetadataNamespace,
            "ThreadingModel");
        _threadingCtorRef ??= CreateWfmCtorRef("ThreadingAttribute",
            new Action<SignatureTypeEncoder>[]
            {
                e => e.Type(threadingModelType, isValueType: true)
            });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteInt32(threadingModel);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _threadingCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddMarshalingBehaviorAttribute(EntityHandle parent, int marshalingType)
    {
        var marshalingTypeRef = GetOrAddTypeReference(
            _emitter.WfmRef,
            MetadataConstants.WindowsFoundationMetadataNamespace,
            "MarshalingType");
        _marshalingCtorRef ??= CreateWfmCtorRef("MarshalingBehaviorAttribute",
            new Action<SignatureTypeEncoder>[]
            {
                e => e.Type(marshalingTypeRef, isValueType: true)
            });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteInt32(marshalingType);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _marshalingCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddDeprecatedAttribute(EntityHandle parent, string message, int deprecationType, uint version)
    {
        _deprecatedCtorRef ??= CreateWfmCtorRef("DeprecatedAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.String(), e => e.Int32(), e => e.UInt32() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(message);
        blob.WriteInt32(deprecationType);
        blob.WriteUInt32(version);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _deprecatedCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddOverloadAttribute(EntityHandle parent, string methodName)
    {
        _overloadCtorRef ??= CreateWfmCtorRef("OverloadAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.String() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteSerializedString(methodName);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _overloadCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddDefaultOverloadAttribute(EntityHandle parent)
    {
        _defaultOverloadCtorRef ??= CreateWfmCtorRef("DefaultOverloadAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _defaultOverloadCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddOverridableAttribute(EntityHandle parent)
    {
        _overridableCtorRef ??= CreateWfmCtorRef("OverridableAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _overridableCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddProtectedAttribute(EntityHandle parent)
    {
        _protectedCtorRef ??= CreateWfmCtorRef("ProtectedAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _protectedCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddExperimentalAttribute(EntityHandle parent)
    {
        _experimentalCtorRef ??= CreateWfmCtorRef("ExperimentalAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _experimentalCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddNoExceptionAttribute(EntityHandle parent)
    {
        _noExceptionCtorRef ??= CreateWfmCtorRef("NoExceptionAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _noExceptionCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    private MemberReferenceHandle? _readOnlyArrayCtorRef;
    private MemberReferenceHandle? _writeOnlyArrayCtorRef;

    public void AddReadOnlyArrayAttribute(EntityHandle parent)
    {
        _readOnlyArrayCtorRef ??= CreateWfmCtorRef("ReadOnlyArrayAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _readOnlyArrayCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddWriteOnlyArrayAttribute(EntityHandle parent)
    {
        _writeOnlyArrayCtorRef ??= CreateWfmCtorRef("WriteOnlyArrayAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _writeOnlyArrayCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    public void AddRemoteAsyncAttribute(EntityHandle parent)
    {
        _remoteAsyncCtorRef ??= CreateWfmCtorRef("RemoteAsyncAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _remoteAsyncCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    /// <summary>
    /// Emits [Windows.Foundation.Metadata.AttributeUsageAttribute(targets)] on an attribute type definition.
    /// The blob stores WinRT AttributeTargets flags (not CLR System.AttributeTargets).
    /// </summary>
    public void AddAttributeUsageAttribute(EntityHandle parent, int winrtTargetFlags)
    {
        _attributeUsageCtorRef ??= CreateWfmCtorRef("AttributeUsageAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.Int32() });

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteInt32(winrtTargetFlags);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _attributeUsageCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    /// <summary>
    /// Emits [Windows.Foundation.Metadata.ApiContractAttribute] on an apicontract type definition.
    /// </summary>
    public void AddApiContractAttribute(EntityHandle parent)
    {
        _apiContractCtorRef ??= CreateWfmCtorRef("ApiContractAttribute",
            Array.Empty<Action<SignatureTypeEncoder>>());
        var blob = EncodeParameterlessAttribute();
        Metadata.AddCustomAttribute(parent, _apiContractCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }

    /// <summary>
    /// Mapping from IDL attribute names to WinMD attribute type names.
    /// Only includes attributes that are NOT handled by explicit Add*Attribute methods.
    /// </summary>
    private static readonly Dictionary<string, string> IdlToWinMdAttributeName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["webhosthidden"] = "WebHostHiddenAttribute",
            ["contentproperty"] = "ContentPropertyAttribute",
            ["inputproperty"] = "InputPropertyAttribute",
            ["allowforweb"] = "AllowForWebAttribute",
            ["bindable"] = "BindableAttribute",
            ["create_instance"] = "CreateInstanceAttribute",
            ["internal"] = "InternalAttribute",
        };

    // Cache for generic attribute constructor MemberRefs keyed by (WinMD type name, arg count)
    private readonly Dictionary<(string typeName, int argCount), MemberReferenceHandle> _genericCtorCache = new();

    /// <summary>
    /// Emits a custom attribute that is not handled by the explicit well-known attribute methods.
    /// For user-defined attribute types, references the TypeDefinition in the same WinMD.
    /// Otherwise falls back to the Windows.Foundation.Metadata namespace.
    /// </summary>
    public void AddGenericCustomAttribute(EntityHandle parent, AttributeData attrData)
    {
        if (!IdlToWinMdAttributeName.TryGetValue(attrData.Name, out var winmdTypeName))
        {
            // Fallback: capitalize first letter, append "Attribute" suffix
            winmdTypeName = char.ToUpperInvariant(attrData.Name[0]) + attrData.Name[1..];
            if (!winmdTypeName.EndsWith("Attribute", StringComparison.Ordinal))
                winmdTypeName += "Attribute";
        }

        var paramEncoders = attrData.Arguments.Select<AttributeArgValue, Action<SignatureTypeEncoder>>(arg =>
            arg.Kind switch
            {
                ArgKind.String or ArgKind.Identifier or ArgKind.TypeName => e => e.String(),
                ArgKind.Integer => e => e.Int32(),
                ArgKind.Float => e => e.Double(),
                ArgKind.Guid => e => e.String(),
                _ => e => e.String(),
            }).ToArray();

        var cacheKey = (winmdTypeName, attrData.Arguments.Count);
        if (!_genericCtorCache.TryGetValue(cacheKey, out var ctorRef))
        {
            // Check if this is a user-defined attribute type (TypeDef in same WinMD)
            // Use case-insensitive matching since IDL names may differ in casing from type names
            var typeDefHandle = _emitter.TypeDefHandles
                .FirstOrDefault(kv => kv.Key.EndsWith("." + winmdTypeName, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(kv.Key, winmdTypeName, StringComparison.OrdinalIgnoreCase));

            if (typeDefHandle.Key != null)
            {
                ctorRef = CreateCtorMemberRefForParent(typeDefHandle.Value, paramEncoders);
            }
            else
            {
                ctorRef = CreateWfmCtorRef(winmdTypeName, paramEncoders);
            }
            _genericCtorCache[cacheKey] = ctorRef;
        }

        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        foreach (var arg in attrData.Arguments)
        {
            switch (arg.Kind)
            {
                case ArgKind.String:
                case ArgKind.Identifier:
                case ArgKind.TypeName:
                    blob.WriteSerializedString(arg.StringValue ?? "");
                    break;
                case ArgKind.Integer:
                    blob.WriteInt32((int)arg.IntValue);
                    break;
                case ArgKind.Float:
                    blob.WriteDouble(arg.FloatValue);
                    break;
                case ArgKind.Guid:
                    blob.WriteSerializedString(arg.GuidValue.ToString());
                    break;
                default:
                    blob.WriteSerializedString(arg.StringValue ?? "");
                    break;
            }
        }
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, ctorRef, Metadata.GetOrAddBlob(blob));
    }

#if FEATURE_STAGING
    private MemberReferenceHandle? _featureCtorRef;

    public void AddFeatureAttribute(EntityHandle parent, int stage, bool validInAllBranches)
    {
        _featureCtorRef ??= CreateWfmCtorRef("FeatureAttribute",
            new Action<SignatureTypeEncoder>[] { e => e.Int32(), e => e.Boolean() });
        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteInt32(stage);
        blob.WriteBoolean(validInAllBranches);
        blob.WriteUInt16(0x0000); // named args
        Metadata.AddCustomAttribute(parent, _featureCtorRef.Value, Metadata.GetOrAddBlob(blob));
    }
#endif

    private TypeReferenceHandle GetSystemTypeRef()
    {
        _systemTypeRef ??= GetOrAddTypeReference(_emitter.MscorlibRef, "System", "Type");
        return _systemTypeRef.Value;
    }

    private MemberReferenceHandle CreateMscorlibCtorRef(
        string ns, string typeName, Action<SignatureTypeEncoder>[] paramEncoders)
    {
        var typeRef = GetOrAddTypeReference(_emitter.MscorlibRef, ns, typeName);
        return CreateCtorMemberRef(typeRef, paramEncoders);
    }

    private MemberReferenceHandle CreateWfmCtorRef(
        string typeName, Action<SignatureTypeEncoder>[] paramEncoders)
    {
        var typeRef = GetOrAddTypeReference(
            _emitter.WfmRef,
            MetadataConstants.WindowsFoundationMetadataNamespace,
            typeName);
        return CreateCtorMemberRef(typeRef, paramEncoders);
    }

    private TypeReferenceHandle GetOrAddTypeReference(
        AssemblyReferenceHandle assembly,
        string ns,
        string name)
    {
        var key = (assembly, ns, name);
        if (!_typeReferences.TryGetValue(key, out var typeRef))
        {
            typeRef = Metadata.AddTypeReference(
                assembly,
                Metadata.GetOrAddString(ns),
                Metadata.GetOrAddString(name));
            _typeReferences[key] = typeRef;
        }

        return typeRef;
    }

    private MemberReferenceHandle CreateCtorMemberRef(
        TypeReferenceHandle typeRef, Action<SignatureTypeEncoder>[] paramEncoders)
    {
        return CreateCtorMemberRefForParent(typeRef, paramEncoders);
    }

    private MemberReferenceHandle CreateCtorMemberRefForParent(
        EntityHandle parent, Action<SignatureTypeEncoder>[] paramEncoders)
    {
        var sig = new BlobBuilder();
        new BlobEncoder(sig)
            .MethodSignature(isInstanceMethod: true)
            .Parameters(paramEncoders.Length,
                ret => ret.Void(),
                p =>
                {
                    foreach (var encode in paramEncoders)
                        encode(p.AddParameter().Type());
                });
        return Metadata.AddMemberReference(parent, Metadata.GetOrAddString(".ctor"), Metadata.GetOrAddBlob(sig));
    }

    private static BlobBuilder EncodeParameterlessAttribute()
    {
        var blob = new BlobBuilder();
        blob.WriteUInt16(0x0001); // prolog
        blob.WriteUInt16(0x0000); // named args
        return blob;
    }
}
