// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Midl.Analysis;
using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;
using Midl.CodeGen.WinMd;

namespace Midl.CodeGen.Tests;

/// <summary>
/// Tests that custom attributes NOT handled by explicit well-known
/// attribute calls are still emitted to the WinMD output.
/// </summary>
public class WinMdCustomAttributeEmissionTests
{
    private static byte[] CompileAndEmit(string source)
    {
        var compilation = CompilationHelper.Compile(source);
        var errors = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, $"Compilation errors: {string.Join(", ", errors.Select(e => e.Message))}");
        var emitter = new WinMdEmitter();
        return emitter.EmitToBytes(compilation);
    }

    private static MetadataReader CreateReader(byte[] bytes, out PEReader peReader)
    {
        peReader = new PEReader(new MemoryStream(bytes));
        return peReader.GetMetadataReader();
    }

    private static TypeDefinitionHandle FindTypeDef(MetadataReader reader, string name)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(handle);
            if (reader.GetString(td.Name) == name)
                return handle;
        }
        throw new Exception($"TypeDef '{name}' not found");
    }

    private static bool HasCustomAttribute(MetadataReader reader, EntityHandle parent, string attributeTypeName)
    {
        foreach (var attrHandle in reader.GetCustomAttributes(parent))
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind == HandleKind.TypeReference)
                {
                    var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    var fullName = $"{reader.GetString(typeRef.Namespace)}.{reader.GetString(typeRef.Name)}";
                    if (fullName == attributeTypeName)
                        return true;
                }
                else if (memberRef.Parent.Kind == HandleKind.TypeDefinition)
                {
                    var typeDef = reader.GetTypeDefinition((TypeDefinitionHandle)memberRef.Parent);
                    var fullName = $"{reader.GetString(typeDef.Namespace)}.{reader.GetString(typeDef.Name)}";
                    if (fullName == attributeTypeName)
                        return true;
                }
            }
        }
        return false;
    }

    private static MethodDefinitionHandle FindMethod(MetadataReader reader, TypeDefinitionHandle typeDef, string methodName)
    {
        var td = reader.GetTypeDefinition(typeDef);
        foreach (var mh in td.GetMethods())
        {
            var md = reader.GetMethodDefinition(mh);
            if (reader.GetString(md.Name) == methodName)
                return mh;
        }
        throw new Exception($"Method '{methodName}' not found");
    }

    [Fact]
    public void Emit_ComposableAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [exclusiveto(Foo)]
                interface IFooFactory
                {
                    Foo Create();
                }
                [composable(IFooFactory, public, 1)]
                unsealed runtimeclass Foo
                {
                    overridable void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Foo");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.ComposableAttribute"),
                "ComposableAttribute should be emitted on Foo");
        }
    }

    [Fact]
    public void Emit_MethodName_IsWrittenAsOverloadAttribute()
    {
        // midl.exe writes [method_name("X")] as OverloadAttribute("X"); Windows.Foundation.Metadata has
        // no MethodNameAttribute.
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [method_name("DoWorkInternal")]
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "DoWork");
            Assert.Equal("DoWorkInternal", OverloadValue(reader, methodHandle));
            Assert.False(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.MethodNameAttribute"));
        }
    }

    private static string? OverloadValue(MetadataReader reader, MethodDefinitionHandle method)
    {
        foreach (var attrHandle in reader.GetCustomAttributes(method))
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;
            var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
            if (reader.GetString(typeRef.Name) != "OverloadAttribute") continue;
            var blobReader = reader.GetBlobReader(attr.Value);
            blobReader.ReadUInt16(); // prolog
            return blobReader.ReadSerializedString();
        }
        return null;
    }

    [Fact]
    public void Emit_ReturnName_NamesTheReturnValueParam()
    {
        // midl.exe uses [return_name] for the return value's Param row and writes no attribute:
        // Windows.Foundation.Metadata has no ReturnNameAttribute.
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [return_name("result2")]
                    Int32 GetValue();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "GetValue");
            Assert.Equal("result2", ReturnParamName(reader, methodHandle));
            Assert.False(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.ReturnNameAttribute"));
        }
    }

    private static string? ReturnParamName(MetadataReader reader, MethodDefinitionHandle method)
    {
        foreach (var h in reader.GetMethodDefinition(method).GetParameters())
        {
            var p = reader.GetParameter(h);
            if (p.SequenceNumber == 0) return reader.GetString(p.Name);
        }
        return null;
    }

    [Fact]
    public void Emit_NoExceptionAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [noexcept]
                    void FastWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "FastWork");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.NoExceptionAttribute"),
                "NoExceptionAttribute should be emitted on FastWork");
        }
    }

    [Fact]
    public void Emit_RemoteAsyncAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [remote_async]
                    Windows.Foundation.IAsyncAction StartAsync();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "StartAsync");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.RemoteAsyncAttribute"),
                "RemoteAsyncAttribute should be emitted on StartAsync");
        }
    }

    [Fact]
    public void Emit_BindableAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [bindable]
                runtimeclass Widget
                {
                    Widget();
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.BindableAttribute"),
                "BindableAttribute should be emitted on Widget");
        }
    }

    [Fact]
    public void Emit_ContentPropertyAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contentproperty("Content")]
                runtimeclass Widget
                {
                    Widget();
                    Object Content;
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.ContentPropertyAttribute"),
                "ContentPropertyAttribute should be emitted on Widget");
        }
    }

    [Fact]
    public void Emit_WebHostHiddenAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [webhosthidden]
                runtimeclass Widget
                {
                    Widget();
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.WebHostHiddenAttribute"),
                "WebHostHiddenAttribute should be emitted on Widget");
        }
    }

    [Fact]
    public void Emit_WebHostHiddenAttribute_OnInterface()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [webhosthidden]
                interface IWidget
                {
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            Assert.True(HasCustomAttribute(reader, ifaceHandle,
                "Windows.Foundation.Metadata.WebHostHiddenAttribute"),
                "WebHostHiddenAttribute should be emitted on interface");
        }
    }

    [Fact]
    public void Emit_ContentPropertyAttribute_HasStringArgument()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contentproperty("MyContent")]
                runtimeclass Widget
                {
                    Widget();
                    Object MyContent;
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");

            // Find the ContentPropertyAttribute and verify its blob contains the string arg
            foreach (var attrHandle in reader.GetCustomAttributes(classHandle))
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;

                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;

                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                var name = reader.GetString(typeRef.Name);
                if (name != "ContentPropertyAttribute") continue;

                // Read the blob value — the string "MyContent" should be in there
                var blobReader = reader.GetBlobReader(attr.Value);
                var prolog = blobReader.ReadUInt16(); // 0x0001
                Assert.Equal(0x0001, prolog);

                var strValue = blobReader.ReadSerializedString();
                Assert.Equal("MyContent", strValue);
                return;
            }

            Assert.Fail("ContentPropertyAttribute not found on Widget");
        }
    }

    [Fact]
    public void Emit_AllowForWebAttribute_InWinMd()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [allowforweb]
                runtimeclass Widget
                {
                    Widget();
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.AllowForWebAttribute"),
                "AllowForWebAttribute should be emitted on Widget");
        }
    }

    [Fact]
    public void Emit_ComposableAttribute_WithProtectedAccess()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [exclusiveto(Foo)]
                interface IFooFactory
                {
                    Foo Create();
                }
                [composable(IFooFactory, protected, 1)]
                unsealed runtimeclass Foo
                {
                    overridable void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Foo");
            Assert.True(HasCustomAttribute(reader, classHandle,
                "Windows.Foundation.Metadata.ComposableAttribute"),
                "ComposableAttribute should be emitted with protected access");

            // Verify the access argument in the blob
            foreach (var attrHandle in reader.GetCustomAttributes(classHandle))
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                if (reader.GetString(typeRef.Name) != "ComposableAttribute") continue;

                var blobReader = reader.GetBlobReader(attr.Value);
                blobReader.ReadUInt16(); // prolog
                blobReader.ReadSerializedString(); // factory type name
                var access = blobReader.ReadInt32();
                Assert.Equal(1, access);
                return;
            }
            Assert.Fail("ComposableAttribute not found on Foo");
        }
    }

    [Fact]
    public void Emit_MethodName_OnClassMethod_IsWrittenOnInterfaceAndStub()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Widget
                {
                    [method_name("DoWorkInternal")]
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            Assert.Equal("DoWorkInternal", OverloadValue(reader, FindMethod(reader, FindTypeDef(reader, "IWidget"), "DoWork")));
            Assert.Equal("DoWorkInternal", OverloadValue(reader, FindMethod(reader, FindTypeDef(reader, "Widget"), "DoWork")));
        }
    }

    [Fact]
    public void Emit_ReturnName_NamesTheClassStubReturnValueParam()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Widget
                {
                    [return_name("alt")]
                    Int32 GetValue();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            Assert.Equal("alt", ReturnParamName(reader, FindMethod(reader, FindTypeDef(reader, "IWidget"), "GetValue")));
            Assert.Equal("alt", ReturnParamName(reader, FindMethod(reader, FindTypeDef(reader, "Widget"), "GetValue")));
        }
    }

    [Fact]
    public void Emit_RemoteAsync_OnAsyncOperationMethod()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [remote_async]
                    Windows.Foundation.IAsyncOperation<Int32> ComputeAsync();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "ComputeAsync");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.RemoteAsyncAttribute"),
                "RemoteAsyncAttribute should be emitted on ComputeAsync returning IAsyncOperation<Int32>");
        }
    }

    [Fact]
    public void Emit_RemoteAsync_WhenTrueAsyncEnabled()
    {
        var compilation = CompilationHelper.Compile("""
            namespace N
            {
                interface IWidget
                {
                    Windows.Foundation.IAsyncOperation<Int32> ComputeAsync();
                }
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        var bytes = new WinMdEmitter(enableTrueAsync: true).EmitToBytes(compilation);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "ComputeAsync");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.RemoteAsyncAttribute"));
        }
    }

    [Fact]
    public void Emit_AsyncReturnWithoutTrueAsync_DoesNotEmitRemoteAsync()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    Windows.Foundation.IAsyncOperation<Int32> ComputeAsync();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "ComputeAsync");
            Assert.False(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.RemoteAsyncAttribute"));
        }
    }

    [Fact]
    public void Emit_MultipleAttributes_OnSingleMethod()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [noexcept]
                    [method_name("Fast")]
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "DoWork");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.NoExceptionAttribute"),
                "NoExceptionAttribute should be emitted on DoWork");
            Assert.Equal("Fast", OverloadValue(reader, methodHandle));
        }
    }

    [Fact]
    public void Emit_ClassWithNoAttributes_NoSpuriousCustomAttributes()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                runtimeclass Widget
                {
                    Widget();
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            // Only well-known structural attributes should be present
            // (e.g., ActivatableAttribute, ThreadingAttribute, MarshalingBehaviorAttribute, VersionAttribute)
            var unexpectedAttributes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attrHandle in reader.GetCustomAttributes(classHandle))
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                var name = reader.GetString(typeRef.Name);

                // These are expected structural attributes
                var expected = new HashSet<string>
                {
                    "ActivatableAttribute",
                    "ThreadingAttribute",
                    "MarshalingBehaviorAttribute",
                    "VersionAttribute",
                    "ContractVersionAttribute",
                };
                if (!expected.Contains(name))
                    unexpectedAttributes.Add(name);
            }

            Assert.Empty(unexpectedAttributes);
        }
    }

    [Fact]
    public void Emit_NoExceptionAndDeprecated_OnInterfaceMethod()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [noexcept]
                    [deprecated("Use DoWorkV2", deprecate, 1)]
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "DoWork");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.NoExceptionAttribute"),
                "NoExceptionAttribute should be emitted");
            Assert.True(HasCustomAttribute(reader, methodHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should be emitted on methods");
        }
    }

    [Fact]
    public void Emit_DeprecatedAttribute_OnPropertyGetter()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                interface IWidget
                {
                    [deprecated("Use NewName", deprecate, 1)]
                    String Name { get; };
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var getterHandle = FindMethod(reader, ifaceHandle, "get_Name");
            Assert.True(HasCustomAttribute(reader, getterHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should be emitted on property getter");
        }
    }

    [Fact]
    public void WellKnown_Attributes_Not_Duplicated()
    {
        // Attributes that ARE handled by explicit calls (e.g., version, threading)
        // should NOT be emitted twice
        var bytes = CompileAndEmit("""
            namespace N
            {
                [threading(both)]
                runtimeclass Widget
                {
                    Widget();
                    void DoWork();
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var classHandle = FindTypeDef(reader, "Widget");
            int threadingCount = 0;
            foreach (var attrHandle in reader.GetCustomAttributes(classHandle))
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                if (reader.GetString(typeRef.Name) == "ThreadingAttribute")
                    threadingCount++;
            }
            Assert.Equal(1, threadingCount);
        }
    }

    private static FieldDefinitionHandle FindField(MetadataReader reader, TypeDefinitionHandle typeDef, string fieldName)
    {
        var td = reader.GetTypeDefinition(typeDef);
        foreach (var fh in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fh);
            if (reader.GetString(fd.Name) == fieldName)
                return fh;
        }
        throw new Exception($"Field '{fieldName}' not found");
    }

    private static ParameterHandle FindParameter(MetadataReader reader, MethodDefinitionHandle methodDef, string paramName)
    {
        var md = reader.GetMethodDefinition(methodDef);
        foreach (var ph in md.GetParameters())
        {
            var pd = reader.GetParameter(ph);
            if (reader.GetString(pd.Name) == paramName)
                return ph;
        }
        throw new Exception($"Parameter '{paramName}' not found");
    }

    [Fact]
    public void Emit_DeprecatedOnEnumMember()
    {
        var bytes = CompileAndEmit("""
            namespace N {
                enum Status {
                    [deprecated("Use Active", deprecate, "C", 2)]
                    Running = 0,
                    Active = 1,
                }
            }
        """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var enumHandle = FindTypeDef(reader, "Status");
            var fieldHandle = FindField(reader, enumHandle, "Running");
            Assert.True(HasCustomAttribute(reader, fieldHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should be emitted on enum member 'Running'");

            // Active should NOT have the attribute
            var activeHandle = FindField(reader, enumHandle, "Active");
            Assert.False(HasCustomAttribute(reader, activeHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should NOT be on 'Active'");
        }
    }

    [Fact]
    public void Emit_DeprecatedOnStructField()
    {
        var bytes = CompileAndEmit("""
            namespace N {
                struct Point {
                    [deprecated("Use NewX", deprecate, "C", 2)]
                    Int32 X;
                    Int32 Y;
                };
            }
        """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var structHandle = FindTypeDef(reader, "Point");
            var fieldHandle = FindField(reader, structHandle, "X");
            Assert.True(HasCustomAttribute(reader, fieldHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should be emitted on struct field 'X'");

            var yHandle = FindField(reader, structHandle, "Y");
            Assert.False(HasCustomAttribute(reader, yHandle,
                "Windows.Foundation.Metadata.DeprecatedAttribute"),
                "DeprecatedAttribute should NOT be on 'Y'");
        }
    }

    [Fact]
    public void Emit_CustomAttributeOnParameter()
    {
        var source = """
            namespace N {
                [attributeusage(target_parameter)]
                [attributename("paramattr")]
                attribute ParamAttrAttribute
                {
                }

                interface IWidget
                {
                    void DoWork([paramattr] String value);
                }
            }
        """;
        var compilation = CompilationHelper.Compile(source);
        var errors = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, $"Compilation errors: {string.Join(", ", errors.Select(e => e.Message))}");

        // Check parameter attributes are populated in the symbol
        var ns = compilation.GlobalNamespace.ChildNamespaces["N"];
        var iface = ns.Members.OfType<Midl.Analysis.Symbols.InterfaceSymbol>().First();
        var method = iface.Members.OfType<Midl.Analysis.Symbols.MethodSymbol>().First();
        var param = method.Parameters.First(p => p.Name == "value");
        Assert.True(param.Attributes.Count > 0,
            $"Parameter 'value' should have attributes. Parameters: {string.Join(", ", method.Parameters.Select(p => $"{p.Name}(attrs={p.Attributes.Count})"))}");

        var bytes = CompileAndEmit(source);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var ifaceHandle = FindTypeDef(reader, "IWidget");
            var methodHandle = FindMethod(reader, ifaceHandle, "DoWork");
            var paramHandle = FindParameter(reader, methodHandle, "value");
            Assert.True(HasCustomAttribute(reader, paramHandle,
                "N.ParamAttrAttribute"),
                "Custom attribute should be emitted on parameter 'value'");
        }
    }

    [Fact]
    public void Emit_ContractEnum_HasContractVersionNotVersion()
    {
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contractversion(1)]
                apicontract MyContract {}

                [contract(MyContract, 1)]
                enum Color
                {
                    Red,
                    Green
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var enumHandle = FindTypeDef(reader, "Color");
            Assert.True(HasCustomAttribute(reader, enumHandle,
                "Windows.Foundation.Metadata.ContractVersionAttribute"),
                "ContractVersionAttribute should be emitted on a [contract] enum");
            Assert.False(HasCustomAttribute(reader, enumHandle,
                "Windows.Foundation.Metadata.VersionAttribute"),
                "VersionAttribute should not be emitted on a [contract]-only enum");
        }
    }

    [Fact]
    public void Emit_VersionAndContractEnum_HasBoth()
    {
        // Ground truth from midl.exe: an enum with both [version] and [contract]
        // emits BOTH [Version] and [ContractVersion].
        var bytes = CompileAndEmit("""
            namespace N
            {
                [contractversion(1)]
                apicontract MyContract {}

                [version(5)]
                [contract(MyContract, 1)]
                enum Color
                {
                    Red,
                    Green
                }
            }
            """);

        var reader = CreateReader(bytes, out var pe);
        using (pe)
        {
            var enumHandle = FindTypeDef(reader, "Color");
            Assert.True(HasCustomAttribute(reader, enumHandle,
                "Windows.Foundation.Metadata.ContractVersionAttribute"),
                "ContractVersionAttribute should be emitted on a [version]+[contract] enum");
            Assert.True(HasCustomAttribute(reader, enumHandle,
                "Windows.Foundation.Metadata.VersionAttribute"),
                "VersionAttribute should also be emitted on a [version]+[contract] enum");
        }
    }
}
