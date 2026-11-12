// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Midl.Analysis;
using Midl.Analysis.Diagnostics;
using Midl.CodeGen.WinMd;

namespace Midl.CodeGen.Tests;

/// <summary>
/// Thorough TDD/regression coverage for real-world classic-MIDL runtimeclass
/// WinMD parity fixes:
///
///   Bug 1: first base-list entry that is an interface → Interfaces, not BaseType
///   Bug 2: external TypeRefs are CLR-projected (EventHandler → System.EventHandler, etc.)
///   Bug 3: runtimeclass members are flattened from the transitive closure of its
///          required external interfaces
///   Bug 4: parameterized constructors emit one versioned [Activatable], not a pair
///   [Default] marker is applied to an explicitly-declared default interface
///   Overload retention: signature-based dedupe preserves overloads
///   External TypeRef fallback: unresolved external types in flattened method
///          signatures encode as proper TypeRefs, not Object
/// </summary>
public class WinMdParityRealWorldTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private string MakeTempWinMd(string source)
    {
        var compilation = CompilationHelper.Compile(source);
        Assert.All(compilation.Diagnostics,
            d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
        var bytes = new WinMdEmitter().EmitToBytes(compilation);
        var path = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.winmd");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    private static byte[] CompileAndEmit(string source, string[]? references = null)
    {
        var compilation = references is null
            ? CompilationHelper.Compile(source)
            : CompilationHelper.Compile(source, references);
        Assert.All(compilation.Diagnostics,
            d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
        return new WinMdEmitter().EmitToBytes(compilation);
    }

    private static MetadataReader Read(byte[] bytes, out PEReader pe)
    {
        pe = new PEReader(new MemoryStream(bytes));
        return pe.GetMetadataReader();
    }

    private static TypeDefinition FindTypeDef(MetadataReader reader, string name)
    {
        foreach (var h in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(h);
            if (reader.GetString(td.Name) == name) return td;
        }
        throw new Xunit.Sdk.XunitException($"TypeDef '{name}' not found");
    }

    private static string FullName(MetadataReader reader, EntityHandle h)
    {
        if (h.IsNil) return "";
        switch (h.Kind)
        {
            case HandleKind.TypeReference:
                var tr = reader.GetTypeReference((TypeReferenceHandle)h);
                var ns = reader.GetString(tr.Namespace);
                var n = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
            case HandleKind.TypeDefinition:
                var td = reader.GetTypeDefinition((TypeDefinitionHandle)h);
                var tns = reader.GetString(td.Namespace);
                var tn = reader.GetString(td.Name);
                return string.IsNullOrEmpty(tns) ? tn : $"{tns}.{tn}";
            default:
                return "";
        }
    }

    // ────────── Bug 1: base-list reclassification ──────────

    [Fact]
    public void BaseList_InterfaceOnly_GoesToInterfaces()
    {
        // `runtimeclass C : IBar` → IBar is an implemented interface.
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IBar { void DoIt(); };
                runtimeclass C : IBar { };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");

        // Base should NOT be IBar.
        Assert.DoesNotContain("IBar", FullName(reader, c.BaseType));

        // IBar must appear as InterfaceImpl.
        var impls = c.GetInterfaceImplementations()
            .Select(h => FullName(reader, reader.GetInterfaceImplementation(h).Interface))
            .ToList();
        Assert.Contains(impls, s => s.EndsWith(".IBar"));
    }

    [Fact]
    public void BaseList_InterfaceFirstThenInterface_BothFlattenToInterfaces()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IA { void A(); };
                interface IB { void B(); };
                runtimeclass C : IA, IB { };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");

        Assert.DoesNotContain("IA", FullName(reader, c.BaseType));
        var impls = c.GetInterfaceImplementations()
            .Select(h => FullName(reader, reader.GetInterfaceImplementation(h).Interface))
            .ToList();
        Assert.Contains(impls, s => s.EndsWith(".IA"));
        Assert.Contains(impls, s => s.EndsWith(".IB"));
    }

    [Fact]
    public void BaseList_UnsealedRuntimeClassBase_StaysAsBaseType()
    {
        // When the first entry is a runtimeclass (not an interface), it's a real base.
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IMarker { void M(); };
                unsealed runtimeclass Base : IMarker { };
                runtimeclass Derived : Base, IMarker { };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var derived = FindTypeDef(reader, "Derived");

        Assert.EndsWith(".Base", FullName(reader, derived.BaseType));
    }

    // ────────── Bug 4: Activatable duplicate ──────────

    [Fact]
    public void ParameterizedConstructor_EmitsExactlyOneActivatableAttribute()
    {
        // Regression: winmidl used to emit both [Activatable(1)] and
        // [Activatable(factory, version)]. midl.exe emits only the latter.
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contractversion(1)] apicontract FooContract {}
                interface IMarker { void M(); };
                [contract(N.FooContract, 1)]
                runtimeclass C : IMarker
                {
                    C(Int32 x);
                };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");

        int activatableCount = 0;
        foreach (var ah in c.GetCustomAttributes())
        {
            var a = reader.GetCustomAttribute(ah);
            var ctorParent = a.Constructor.Kind == HandleKind.MemberReference
                ? reader.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent
                : default;
            var name = FullName(reader, ctorParent);
            if (name.EndsWith(".ActivatableAttribute")) activatableCount++;
        }

        Assert.Equal(1, activatableCount);
    }

    [Fact]
    public void DefaultConstructor_EmitsOneUnversionedActivatable()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IMarker { void M(); };
                runtimeclass C : IMarker { C(); };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");

        int activatableCount = 0;
        foreach (var ah in c.GetCustomAttributes())
        {
            var a = reader.GetCustomAttribute(ah);
            var parent = a.Constructor.Kind == HandleKind.MemberReference
                ? reader.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent
                : default;
            if (FullName(reader, parent).EndsWith(".ActivatableAttribute")) activatableCount++;
        }

        Assert.Equal(1, activatableCount);
    }

    // ────────── [Default] marker on explicit interface ──────────

    [Fact]
    public void ExplicitDefaultInterface_GetsDefaultAttribute()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IA { void A(); };
                interface IB { void B(); };
                runtimeclass C : [default] IB, IA
                {
                    void C_method();
                };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");

        // Find InterfaceImpl for IB and check it has [Default]
        InterfaceImplementation? ibImpl = null;
        foreach (var ih in c.GetInterfaceImplementations())
        {
            var impl = reader.GetInterfaceImplementation(ih);
            if (FullName(reader, impl.Interface).EndsWith(".IB")) { ibImpl = impl; break; }
        }
        Assert.NotNull(ibImpl);

        // CustomAttributes on an InterfaceImpl aren't directly enumerable; check
        // via GetCustomAttributes(EntityHandle).
        // Simplest: scan all attributes, find ones whose Parent == this InterfaceImpl,
        // and look for DefaultAttribute.
        bool hasDefault = false;
        foreach (var ah in reader.CustomAttributes)
        {
            var a = reader.GetCustomAttribute(ah);
            if (a.Parent.Kind != HandleKind.InterfaceImplementation) continue;
            var parentImpl = reader.GetInterfaceImplementation((InterfaceImplementationHandle)a.Parent);
            if (!FullName(reader, parentImpl.Interface).EndsWith(".IB")) continue;
            var ctorParent = a.Constructor.Kind == HandleKind.MemberReference
                ? reader.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent
                : default;
            if (FullName(reader, ctorParent).EndsWith(".DefaultAttribute"))
            {
                hasDefault = true;
                break;
            }
        }
        Assert.True(hasDefault, "Explicit [default] interface should receive DefaultAttribute");
    }

    // ────────── Overload retention ──────────

    [Fact]
    public void DirectOverloads_DifferentArities_AllPreserved()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass C
                {
                    void Do();
                    void Do(String s);
                    void Do(String s, Int32 i);
                };
            }
            """);
        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");
        int n = c.GetMethods().Select(h => reader.GetString(reader.GetMethodDefinition(h).Name))
            .Count(s => s == "Do");
        Assert.Equal(3, n);
    }

    // ────────── Bug 3 + external TypeRef resolution (via temp-winmd round-trip) ──────────

    [Fact]
    public void FlattensExternalInterfaceMembers_OntoRuntimeClass()
    {
        // Compile one IDL to a .winmd, then reference it from a second compilation.
        // The second compilation's runtimeclass should have the referenced interface's
        // members "flattened" (emitted as stubs on the class's MethodList).
        var refWinMd = MakeTempWinMd("""
            namespace Ext
            {
                interface IExt
                {
                    void Alpha();
                    String Beta(Int32 n);
                };
            }
            """);

        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass C : Ext.IExt
                {
                    void Local();
                };
            }
            """, [refWinMd]);

        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");
        var methodNames = c.GetMethods()
            .Select(h => reader.GetString(reader.GetMethodDefinition(h).Name))
            .ToList();

        Assert.Contains("Local", methodNames);      // direct member
        Assert.Contains("Alpha", methodNames);      // flattened from external IExt
        Assert.Contains("Beta", methodNames);       // flattened from external IExt
    }

    [Fact]
    public void FlattensTransitiveRequiredInterfaces()
    {
        var refWinMd = MakeTempWinMd("""
            namespace Ext
            {
                interface IBase { void BaseMethod(); };
                [uuid(11111111-2222-3333-4444-555555555555)]
                interface IChild requires IBase { void ChildMethod(); };
            }
            """);

        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass C : Ext.IChild { };
            }
            """, [refWinMd]);

        var reader = Read(bytes, out var pe); using var _ = pe;
        var c = FindTypeDef(reader, "C");
        var names = c.GetMethods()
            .Select(h => reader.GetString(reader.GetMethodDefinition(h).Name))
            .ToList();

        Assert.Contains("ChildMethod", names);   // from IChild directly
        Assert.Contains("BaseMethod", names);    // via IChild requires IBase
    }

    [Fact]
    public void FlattenedExternalMembers_EncodeRealParamTypes_NotObject()
    {
        // When we flatten an external interface's methods onto the class, the param
        // and return types must encode as proper TypeRefs — not fall through to Object.
        var refWinMd = MakeTempWinMd("""
            namespace Ext
            {
                enum Color { Red, Green, Blue };
                interface IPainter
                {
                    Ext.Color GetColor();
                    void SetName(String name);
                };
            }
            """);

        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Canvas : Ext.IPainter { };
            }
            """, [refWinMd]);

        var reader = Read(bytes, out var pe); using var _ = pe;
        var canvas = FindTypeDef(reader, "Canvas");

        var getColor = canvas.GetMethods()
            .Select(h => reader.GetMethodDefinition(h))
            .Single(m => reader.GetString(m.Name) == "GetColor");
        var setName = canvas.GetMethods()
            .Select(h => reader.GetMethodDefinition(h))
            .Single(m => reader.GetString(m.Name) == "SetName");

        var sigGet = getColor.DecodeSignature(new FqSignatureProvider(reader), null);
        var sigSet = setName.DecodeSignature(new FqSignatureProvider(reader), null);

        // Return type of GetColor should be Ext.Color — NOT Object.
        Assert.Equal("Ext.Color", sigGet.ReturnType);
        Assert.NotEqual("Object", sigGet.ReturnType);

        // Parameter of SetName should be String — not Object.
        Assert.Single(sigSet.ParameterTypes);
        Assert.Equal("String", sigSet.ParameterTypes[0]);
    }

    // ────────── Flattened members of interfaces from other WinMDs keep their ABI ──────────

    // Shaped like Microsoft.UI.Xaml.Markup.IXamlMetadataProvider: TypeName (which the CLR
    // metadata reader projects to System.Type), a struct array, overloads, a property, an event.
    // As in midl.exe, [overload("Find")] is the shared (MethodDef) name and FindByType/FindByName
    // are the unique names written to OverloadAttribute.
    private const string ProviderReference = """
        namespace Windows.UI.Xaml.Interop
        {
            enum TypeKind { Primitive, Metadata, Custom };
            struct TypeName { String Name; TypeKind Kind; };
        }
        namespace Ext
        {
            struct Definition { String Uri; String Namespace; };
            delegate void ChangedHandler(Int32 value);
            interface IProvider
            {
                [overload("Find"), default_overload] String FindByType(Windows.UI.Xaml.Interop.TypeName type);
                [overload("Find")] String FindByName(String fullName);
                Definition[] GetDefinitions();
                Int32 Count { get; set; };
                event ChangedHandler Changed;
            };
        }
        """;

    private const string ProviderHost = """
        namespace N
        {
            runtimeclass Host : Ext.IProvider { };
        }
        """;

    private static MetadataReader ReadRaw(byte[] bytes, out PEReader pe)
    {
        // Raw metadata: the default reader options would project TypeName to System.Type and hide a regression.
        pe = new PEReader(new MemoryStream(bytes));
        return pe.GetMetadataReader(MetadataReaderOptions.None);
    }

    [Fact]
    public void FlattenedExternalMembers_KeepRawWinRTNamesAndValueTypes()
    {
        var bytes = CompileAndEmit(ProviderHost, [MakeTempWinMd(ProviderReference)]);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var provider = new FqSignatureProvider(reader, markValueTypes: true);
        var methods = FindTypeDef(reader, "Host").GetMethods().Select(reader.GetMethodDefinition).ToList();

        var findParams = methods.Where(m => reader.GetString(m.Name) == "Find")
            .Select(m => m.DecodeSignature(provider, null).ParameterTypes.Single()).ToList();
        Assert.Equal(new[] { "String", "valuetype Windows.UI.Xaml.Interop.TypeName" }, findParams.Order(StringComparer.Ordinal));

        var definitions = methods.Single(m => reader.GetString(m.Name) == "GetDefinitions");
        Assert.Equal("valuetype Ext.Definition[]", definitions.DecodeSignature(provider, null).ReturnType);

        var typeRefs = reader.TypeReferences.Select(h => FullName(reader, h)).ToList();
        Assert.DoesNotContain("System.Type", typeRefs);
    }

    [Fact]
    public void FlattenedExternalMembers_ImplementTheExternalInterfaceThroughMethodImpls()
    {
        var bytes = CompileAndEmit(ProviderHost, [MakeTempWinMd(ProviderReference)]);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var host = FindTypeDef(reader, "Host");

        var impls = host.GetMethodImplementations().Select(reader.GetMethodImplementation).ToList();
        Assert.Equal(
            new[] { "Find", "Find", "GetDefinitions", "add_Changed", "get_Count", "put_Count", "remove_Changed" },
            impls.Select(mi => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)mi.MethodBody).Name)).Order(StringComparer.Ordinal));
        Assert.All(impls, mi =>
        {
            Assert.Equal(HandleKind.MemberReference, mi.MethodDeclaration.Kind);
            var body = reader.GetMethodDefinition((MethodDefinitionHandle)mi.MethodBody);
            var decl = reader.GetMemberReference((MemberReferenceHandle)mi.MethodDeclaration);
            Assert.Equal("Ext.IProvider", FullName(reader, decl.Parent));
            Assert.Equal(reader.GetString(body.Name), reader.GetString(decl.Name));
            Assert.Equal(reader.GetBlobBytes(body.Signature), reader.GetBlobBytes(decl.Signature));
        });
    }

    [Fact]
    public void FlattenedExternalMembers_KeepOverloadAttributes()
    {
        var bytes = CompileAndEmit(ProviderHost, [MakeTempWinMd(ProviderReference)]);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var provider = new FqSignatureProvider(reader);

        var overloads = FindTypeDef(reader, "Host").GetMethods().Select(reader.GetMethodDefinition)
            .Where(m => reader.GetString(m.Name) == "Find")
            .ToDictionary(m => m.DecodeSignature(provider, null).ParameterTypes.Single(), m => AttributeSummary(reader, m));

        Assert.Equal(new[] { "DefaultOverloadAttribute", "OverloadAttribute(FindByType)" }, overloads["Windows.UI.Xaml.Interop.TypeName"]);
        Assert.Equal(new[] { "OverloadAttribute(FindByName)" }, overloads["String"]);
    }

    private static List<string> MethodImplSummary(MetadataReader reader, TypeDefinition type)
        => type.GetMethodImplementations().Select(reader.GetMethodImplementation).Select(mi =>
        {
            var body = reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)mi.MethodBody).Name);
            var decl = reader.GetMemberReference((MemberReferenceHandle)mi.MethodDeclaration);
            return $"{body} => {FullName(reader, decl.Parent)}::{reader.GetString(decl.Name)}";
        }).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void FlattenedMembers_OfInterfacesInTheSameIdl_GetMethodImpls()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IGreeter
                {
                    String Greet(String name);
                    Int32 Count { get; };
                };
                runtimeclass Greeter : IGreeter { Greeter(); };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;

        Assert.Equal(new[] { "Greet => N.IGreeter::Greet", "get_Count => N.IGreeter::get_Count" },
            MethodImplSummary(reader, FindTypeDef(reader, "Greeter")));
    }

    [Fact]
    public void RequiredInterfaces_AreListedAndFlattened_EvenWhenBuiltIn()
    {
        // IClosable is also a member-less built-in placeholder; the referenced WinMD's definition must win.
        var reference = MakeTempWinMd("""
            namespace Windows.Foundation
            {
                interface IClosable { void Close(); };
            }
            namespace Ext
            {
                interface IStream requires Windows.Foundation.IClosable { Int32 Read(); };
            }
            """);
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Stream : Ext.IStream { Stream(); };
            }
            """, [reference]);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var stream = FindTypeDef(reader, "Stream");

        var interfaces = stream.GetInterfaceImplementations()
            .Select(h => FullName(reader, reader.GetInterfaceImplementation(h).Interface)).ToList();
        Assert.Contains("Ext.IStream", interfaces);
        Assert.Contains("Windows.Foundation.IClosable", interfaces);
        Assert.Equal(new[] { "Close => Windows.Foundation.IClosable::Close", "Read => Ext.IStream::Read" },
            MethodImplSummary(reader, stream));
    }

    [Fact]
    public void FlattenedExternalMembers_WithGenericTypes_KeepASingleAritySuffix()
    {
        var reference = MakeTempWinMd("""
            namespace Ext
            {
                interface IFlushable { Windows.Foundation.IAsyncOperation<Boolean> FlushAsync(); };
            }
            """);
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Sink : Ext.IFlushable { Sink(); };
            }
            """, [reference]);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;

        var names = reader.TypeReferences.Select(h => reader.GetString(reader.GetTypeReference(h).Name)).ToList();
        Assert.Contains("IAsyncOperation`1", names);
        Assert.DoesNotContain(names, n => n.EndsWith("`1`1", StringComparison.Ordinal));
        var flush = FindTypeDef(reader, "Sink").GetMethods().Select(reader.GetMethodDefinition)
            .Single(m => reader.GetString(m.Name) == "FlushAsync");
        Assert.Equal("Windows.Foundation.IAsyncOperation`1<Boolean>", flush.DecodeSignature(new FqSignatureProvider(reader), null).ReturnType);
    }

    private static string ContractSummary(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var attr = reader.GetCustomAttribute(h);
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "ContractVersionAttribute")
                continue;
            var blob = reader.GetBlobReader(attr.Value);
            blob.ReadUInt16();
            var contract = blob.RemainingBytes > 4 ? blob.ReadSerializedString() : "";
            return $"{contract} v{blob.ReadUInt32() >> 16}";
        }
        return "(none)";
    }

    [Fact]
    public void FlattenedMembers_TakeTheRuntimeClassContract()
    {
        // midl.exe stamps stubs, properties and events that come from an implemented interface with the
        // class's contract, not the interface's (here v3); members declared in the class keep their own.
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contractversion(3)]
                apicontract LabContract {};

                [contract(LabContract, 1)]
                delegate void Handler(Int32 x);

                [contract(LabContract, 3)]
                interface INewer
                {
                    String Hello();
                    Int32 Count { get; };
                    event Handler Changed;
                };

                [contract(LabContract, 2)]
                runtimeclass Widget : INewer
                {
                    Widget();
                    Int32 Size { get; };
                    [contract(LabContract, 3)]
                    void Later();
                };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var widget = FindTypeDef(reader, "Widget");

        var methods = widget.GetMethods().Select(reader.GetMethodDefinition)
            .ToDictionary(m => reader.GetString(m.Name), m => ContractSummary(reader, m.GetCustomAttributes()));
        foreach (var name in new[] { ".ctor", "get_Size", "Hello", "get_Count", "add_Changed", "remove_Changed" })
            Assert.Equal("N.LabContract v2", methods[name]);
        Assert.Equal("N.LabContract v3", methods["Later"]);
        foreach (var p in widget.GetProperties().Select(reader.GetPropertyDefinition))
            Assert.Equal("N.LabContract v2", ContractSummary(reader, p.GetCustomAttributes()));
        var changed = reader.GetEventDefinition(Assert.Single(widget.GetEvents()));
        Assert.Equal("N.LabContract v2", ContractSummary(reader, changed.GetCustomAttributes()));
    }

    private static Guid GuidOf(MetadataReader reader, TypeDefinition type)
    {
        foreach (var h in type.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(h);
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "GuidAttribute")
                continue;
            var blob = reader.GetBlobReader(attr.Value);
            blob.ReadUInt16();
            return new Guid(blob.ReadUInt32(), blob.ReadUInt16(), blob.ReadUInt16(), blob.ReadByte(), blob.ReadByte(),
                blob.ReadByte(), blob.ReadByte(), blob.ReadByte(), blob.ReadByte(), blob.ReadByte(), blob.ReadByte());
        }
        throw new Xunit.Sdk.XunitException("no GuidAttribute");
    }

    [Fact]
    public void OverloadSets_GetMidlAbiNames_AndIidsFromThem()
    {
        // midl.exe: methods of one interface that share a name get ABI names Show, Show2, ...
        // (or their [method_name]); OverloadAttribute holds the ABI name and the IID is computed from it.
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contractversion(2)]
                apicontract C {};

                [contract(C, 1)]
                runtimeclass W
                {
                    W();
                    void Show(Int32 x);
                    void Show(Int32 x, Int32 y);
                    [method_name("ShowAll")] void Show(Int32 x, Int32 y, Int32 z);
                    static void Make(Int32 x);
                    static void Make(Int32 x, Int32 y);
                    [contract(C, 2)]
                    {
                        void Show(String s, String t, String u, String v);
                    }
                };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;

        Assert.Equal(new Guid("54c63275-791e-5e08-8062-bcea39813912"), GuidOf(reader, FindTypeDef(reader, "IW")));
        Assert.Equal(new Guid("e404b4a5-fa7b-5805-9c01-259460f7d1af"), GuidOf(reader, FindTypeDef(reader, "IW2")));
        Assert.Equal(new Guid("6a824486-50df-56b1-a2e3-a1006fce8d87"), GuidOf(reader, FindTypeDef(reader, "IWStatics")));
        var stubs = FindTypeDef(reader, "W").GetMethods().Select(reader.GetMethodDefinition)
            .Where(m => reader.GetString(m.Name) is "Show" or "Make")
            .Select(m => AttributeSummary(reader, m).SingleOrDefault(a => a.StartsWith("OverloadAttribute", StringComparison.Ordinal)) ?? "(none)")
            .ToList();
        Assert.Equal(new[] { "OverloadAttribute(Show)", "OverloadAttribute(Show2)", "OverloadAttribute(ShowAll)", "(none)",
            "OverloadAttribute(Make)", "OverloadAttribute(Make2)" }, stubs);
    }

    [Fact]
    public void OverloadSets_CountOnlyMethodsWithoutMethodName()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [default_interface]
                runtimeclass W
                {
                    void Show(Int32 x);
                    [method_name("ShowTwo")] void Show(Int32 x, Int32 y);
                    void Show(Int32 x, Int32 y, Int32 z);
                };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var iface = FindTypeDef(reader, "IW");

        Assert.Equal(new Guid("316d4bd3-49ce-51f5-9349-cce2b2b00734"), GuidOf(reader, iface));
        Assert.Equal(new[] { "OverloadAttribute(Show)", "OverloadAttribute(ShowTwo)", "OverloadAttribute(Show2)" },
            iface.GetMethods().Select(reader.GetMethodDefinition).Select(m => AttributeSummary(reader, m).Single()));
    }

    [Fact]
    public void FactoryMethods_GetMidlNames_ReturnNames_AndIid()
    {
        // midl.exe: CreateInstance, CreateInstance2, ... counting the constructors without
        // [method_name]; the return value is "value" unless [return_name]; no OverloadAttribute.
        var bytes = CompileAndEmit("""
            namespace N
            {
                [default_interface]
                runtimeclass W
                {
                    W(Int32 x);
                    W(Int32 x, Int32 y);
                    [method_name("CreateWithName")] W(String name, Int32 a, Int32 b);
                    [return_name("instance")] W(Int32 a, Int32 b, Int32 c, Int32 d);
                };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;
        var factory = FindTypeDef(reader, "IWFactory");

        Assert.Equal(new Guid("3ab28aaf-6a51-5ff2-84a7-dcc6c781c074"), GuidOf(reader, FindTypeDef(reader, "IW")));
        Assert.Equal(new Guid("6229565f-e301-59ee-bcf1-c43f0aefe658"), GuidOf(reader, factory));
        var methods = factory.GetMethods().Select(reader.GetMethodDefinition).ToList();
        Assert.Equal(new[] { "CreateInstance", "CreateInstance2", "CreateWithName", "CreateInstance3" },
            methods.Select(m => reader.GetString(m.Name)));
        Assert.Equal(new[] { "value", "value", "value", "instance" },
            methods.Select(m => reader.GetString(reader.GetParameter(m.GetParameters().First()).Name)));
        Assert.All(methods, m => Assert.Empty(AttributeSummary(reader, m)));
    }

    [Fact]
    public void GettersAndEventAdders_HaveMidlReturnValueRows()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                delegate void Handler(Int32 x);
                runtimeclass Widget
                {
                    Int32 Count { get; };
                    static Int32 Total { get; };
                    event Handler Changed;
                };
            }
            """);
        var reader = ReadRaw(bytes, out var pe); using var _ = pe;

        foreach (var typeName in new[] { "Widget", "IWidget", "IWidgetStatics" })
        {
            foreach (var m in FindTypeDef(reader, typeName).GetMethods().Select(reader.GetMethodDefinition))
            {
                var name = reader.GetString(m.Name);
                var expected = name.StartsWith("get_", StringComparison.Ordinal) ? "value"
                    : name.StartsWith("add_", StringComparison.Ordinal) ? "token" : null;
                var returnRow = m.GetParameters().Select(reader.GetParameter).Where(p => p.SequenceNumber == 0)
                    .Select(p => reader.GetString(p.Name)).SingleOrDefault();
                Assert.Equal(expected, returnRow);
            }
        }
    }

    private static List<string> AttributeSummary(MetadataReader reader, MethodDefinition method)
    {
        var result = new List<string>();
        foreach (var h in method.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(h);
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            var name = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name);
            var blob = reader.GetBlobReader(attr.Value);
            blob.ReadUInt16();
            result.Add(name == "OverloadAttribute" ? $"{name}({blob.ReadSerializedString()})" : name);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // ────────── CLR projection of external TypeRefs ──────────

    [Fact]
    public void EventRegistrationToken_IsStoredAsWindowsFoundationRawType()
    {
        // Build a class that uses an event — this causes EventRegistrationToken to
        // appear in add_/remove_ accessor signatures.
        var bytes = CompileAndEmit("""
            namespace N
            {
                delegate void MyHandler(Int32 x);
                runtimeclass C
                {
                    event MyHandler Changed;
                };
            }
            """);
        // Read raw (no WinRT projection): cppwinrt/mdmerge consume the raw metadata, which
        // must hold Windows.Foundation.EventRegistrationToken. Reading with projection on
        // would rewrite it to the mscorlib name and hide a regression.
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        var reader = pe.GetMetadataReader(MetadataReaderOptions.None);

        bool found = false;
        foreach (var h in reader.TypeReferences)
        {
            var tr = reader.GetTypeReference(h);
            if (reader.GetString(tr.Name) != "EventRegistrationToken") continue;
            found = true;
            Assert.Equal("Windows.Foundation", reader.GetString(tr.Namespace));
        }
        Assert.True(found, "EventRegistrationToken TypeRef should exist in metadata");
    }

    [Theory]
    [InlineData("Windows.Foundation.DateTime", "Windows.Foundation.DateTime")]
    [InlineData("Windows.Foundation.TimeSpan", "Windows.Foundation.TimeSpan")]
    [InlineData("Windows.Foundation.Collections.IVector<Int32>", "Windows.Foundation.Collections.IVector`1")]
    [InlineData("Windows.Foundation.Collections.IVectorView<Int32>", "Windows.Foundation.Collections.IVectorView`1")]
    [InlineData("Windows.Foundation.Collections.IMap<String, Int32>", "Windows.Foundation.Collections.IMap`2")]
    [InlineData("Windows.Foundation.Collections.IMapView<String, Int32>", "Windows.Foundation.Collections.IMapView`2")]
    public void ProjectedTypes_AreStoredRawNotClrProjected(string idlType, string expectedRawFullName)
    {
        // Projected WinRT types (DateTime, IVector, IMap, ...) must be stored raw; the CLR
        // projection is a read-time transform, never stored. Read raw so a regression that
        // stores the projected form is not masked.
        var bytes = CompileAndEmit($$"""
            namespace N
            {
                runtimeclass C
                {
                    {{idlType}} Get();
                };
            }
            """);
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        var reader = pe.GetMetadataReader(MetadataReaderOptions.None);

        var rawNames = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Select(tr =>
            {
                var ns = reader.GetString(tr.Namespace);
                var nm = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
            })
            .ToList();

        Assert.Contains(expectedRawFullName, rawNames);
        // The CLR-projected forms must NOT appear in the raw metadata.
        Assert.DoesNotContain("System.DateTimeOffset", rawNames);
        Assert.DoesNotContain("System.Collections.Generic.IList`1", rawNames);
        Assert.DoesNotContain("System.Collections.Generic.IReadOnlyList`1", rawNames);
        Assert.DoesNotContain("System.Collections.Generic.IDictionary`2", rawNames);
        Assert.DoesNotContain("System.Collections.Generic.IReadOnlyDictionary`2", rawNames);
    }

    // ────────── Helpers ──────────

    private sealed class FqSignatureProvider : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _reader;
        private readonly bool _markValueTypes;
        public FqSignatureProvider(MetadataReader r, bool markValueTypes = false)
        {
            _reader = r;
            _markValueTypes = markValueTypes;
        }

        private string Mark(string name, byte kind)
            => _markValueTypes && kind == (byte)SignatureTypeKind.ValueType ? "valuetype " + name : name;

        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Void => "Void",
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
            _ => code.ToString(),
        };
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte kind)
        {
            var td = r.GetTypeDefinition(h);
            var ns = r.GetString(td.Namespace);
            var n = r.GetString(td.Name);
            return Mark(string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}", kind);
        }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte kind)
        {
            var tr = r.GetTypeReference(h);
            var ns = r.GetString(tr.Namespace);
            var n = r.GetString(tr.Name);
            return Mark(string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}", kind);
        }
        public string GetSZArrayType(string e) => $"{e}[]";
        public string GetArrayType(string e, ArrayShape s) => $"{e}[]";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a)
            => $"{g}<{string.Join(",", a)}>";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetPinnedType(string e) => e;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericMethodParameter(object? c, int i) => $"!!{i}";
        public string GetGenericTypeParameter(object? c, int i) => $"!{i}";
        public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte _)
            => r.GetTypeSpecification(h).DecodeSignature(this, c);
    }

    // ────────── HRESULT return type in /winrt compat mode ──────────

    [Fact]
    public void HResult_ReturnType_Encodes_As_WindowsFoundationHResult_Projected()
    {
        // In /winrt compat mode, methods may declare HRESULT return types. These must
        // be encoded in WinMD as Windows.Foundation.HResult (value struct), which the
        // CLR projection in System.Reflection.Metadata projects to System.Exception
        // when the WinMD is read. This matches midl.exe output byte-for-byte
        // semantically (both produce System.Exception after projection applied).
        var compilation = CompilationHelper.Compile("""
            namespace N
            {
                [uuid("00000000-0000-0000-0000-000000000001")]
                interface IFoo
                {
                    HRESULT DoIt();
                    HRESULT DoItWith(Int32 x);
                };
            }
            """, referencePaths: null, mode: CompilationMode.Compat);
        Assert.All(compilation.Diagnostics,
            d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
        var bytes = new WinMdEmitter().EmitToBytes(compilation);
        var reader = Read(bytes, out var pe); using var _ = pe;

        var iface = FindTypeDef(reader, "IFoo");
        var methods = iface.GetMethods().Select(h => reader.GetMethodDefinition(h)).ToList();
        Assert.Equal(2, methods.Count);

        foreach (var m in methods)
        {
            var sig = m.DecodeSignature(new FqSignatureProvider(reader), null);
            // After CLR projection, Windows.Foundation.HResult → System.Exception.
            Assert.Equal("System.Exception", sig.ReturnType);
        }

        // And the raw TypeRef must NOT be a bare "HRESULT" name — the whole point of
        // the fix is that we emit a proper external TypeRef (projected to Exception).
        foreach (var trh in reader.TypeReferences)
        {
            var tr = reader.GetTypeReference(trh);
            var n = reader.GetString(tr.Name);
            Assert.NotEqual("HRESULT", n);
        }
    }

    [Fact]
    public void HResult_In_NonCompat_Is_Undefined()
    {
        // Sanity check: in strict mode HRESULT is not a recognized type alias.
        var comp = CompilationHelper.Compile("""
            namespace N
            {
                [uuid("00000000-0000-0000-0000-000000000002")]
                interface IFoo { HRESULT DoIt(); };
            }
            """);
        Assert.Contains(comp.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error);
    }
}
