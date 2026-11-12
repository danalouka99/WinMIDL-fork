// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis;
using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;

namespace Midl.Analysis.Tests;

public class VersionPropagationTests
{
    [Fact]
    public void ContractPropagatedToSynthesizedInterface()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(3)]
                apicontract TestContract { }

                [contract(TestContract, 1)]
                runtimeclass Widget
                {
                    void DoSomething();
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var ns = compilation.GlobalNamespace.ChildNamespaces["TestApp"];
        var widget = ns.Members.OfType<RuntimeClassSymbol>().Single();

        Assert.NotNull(widget.DefaultInterface);
        Assert.Equal("TestContract", widget.DefaultInterface.ContractName);
        Assert.Equal((1 << 16), widget.DefaultInterface.ContractVersion);
    }

    [Fact]
    public void MemberInheritsClassContract()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(3)]
                apicontract TestContract { }

                [contract(TestContract, 1)]
                runtimeclass Widget
                {
                    void DoSomething();
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var ns = compilation.GlobalNamespace.ChildNamespaces["TestApp"];
        var widget = ns.Members.OfType<RuntimeClassSymbol>().Single();

        var method = widget.Members.OfType<MethodSymbol>().Single();
        Assert.Equal("TestContract", method.ContractName);
        Assert.Equal((1 << 16), method.ContractVersion);
    }

    [Fact]
    public void InternalContractVisibilityResolvesRelativeNamespace()
    {
        var compilation = CompilationHelper.Compile("""
            namespace Review.A
            {
                [internal, contractversion(1)]
                apicontract C {}

                [contract(C, 1)]
                struct PayloadA
                {
                    Int32 Value;
                }
            }

            namespace Review.B
            {
                [contractversion(1)]
                apicontract C {}

                [contract(C, 1)]
                struct PayloadB
                {
                    Int32 Value;
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var review = compilation.GlobalNamespace.ChildNamespaces["Review"];
        var payloadA = review.ChildNamespaces["A"].Members.OfType<StructSymbol>().Single();
        var payloadB = review.ChildNamespaces["B"].Members.OfType<StructSymbol>().Single();

        Assert.Contains(payloadA.Attributes,
            attribute => attribute.Name.Equals("internal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(payloadB.Attributes,
            attribute => attribute.Name.Equals("internal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VersionedBlockOverridesClassContract()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(3)]
                apicontract TestContract { }

                [contract(TestContract, 1)]
                runtimeclass Widget
                {
                    void MethodA();

                    [contract(TestContract, 2)] {
                        void MethodB();
                    }
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var ns = compilation.GlobalNamespace.ChildNamespaces["TestApp"];
        var widget = ns.Members.OfType<RuntimeClassSymbol>().Single();

        var methodA = widget.Members.OfType<MethodSymbol>().First(m => m.Name == "MethodA");
        Assert.Equal("TestContract", methodA.ContractName);
        Assert.Equal((1 << 16), methodA.ContractVersion);

        var methodB = widget.Members.OfType<MethodSymbol>().First(m => m.Name == "MethodB");
        Assert.Equal("TestContract", methodB.ContractName);
        Assert.Equal((2 << 16), methodB.ContractVersion);
    }

    [Fact]
    public void ImplementedInterfaces_MayBeOlderThanTheClassOrInAnotherContract()
    {
        // midl.exe accepts all three and gives the class's members from them the class's contract.
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(3)]
                apicontract ContractA { }

                [contractversion(3)]
                apicontract ContractB { }

                [contract(ContractA, 1)]
                interface IEarly { void Early(); }

                [contract(ContractA, 1)]
                [exclusiveto(Widget)]
                interface IWidgetEarly { void WidgetEarly(); }

                [contract(ContractB, 1)]
                interface IOther { void Other(); }

                [contract(ContractA, 2)]
                runtimeclass Widget : [default] IWidgetEarly, IEarly, IOther
                {
                }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics, d => d.Code is "MIDL1014" or "MIDL1015");
    }

    [Fact]
    public void EnumMemberVersionValidation()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(5)]
                apicontract TestContract { }

                [contract(TestContract, 3)]
                enum Colors
                {
                    [contract(TestContract, 2)]
                    Red = 0,
                    Green = 1,
                }
            }
            """);

        var diag = Assert.Single(compilation.Diagnostics, d => d.Code == "MIDL1017");
        Assert.Contains("Red", diag.Message);
        Assert.Contains("version 2", diag.Message);
        Assert.Contains("version 3", diag.Message);
    }

    [Fact]
    public void ValidVersionsProduceNoDiagnostic()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contractversion(5)]
                apicontract TestContract { }

                [contract(TestContract, 1)]
                interface IWidget
                {
                    void Foo();
                }

                [contract(TestContract, 1)]
                runtimeclass Widget : IWidget
                {
                    void DoSomething();
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void UndefinedContractDiagnostic()
    {
        var compilation = CompilationHelper.Compile("""
            namespace TestApp
            {
                [contract(NonExistent, 1)]
                runtimeclass Widget
                {
                    void DoSomething();
                }
            }
            """);

        var diag = Assert.Single(compilation.Diagnostics, d => d.Code == "MIDL1016");
        Assert.Contains("NonExistent", diag.Message);
    }
}
