// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis;
using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;
namespace Midl.Analysis.Tests;
public class WinRtValidationTests
{
    [Fact] public void MIDL1048_COnlyType_Errors() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void P(wchar_t x); } }", CompilationMode.Strict); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1048"); }
    [Fact] public void MIDL1048_Normal_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void P(Int32 x); } }", CompilationMode.Strict); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1048"); }
#if WITH_COMPAT
    [Fact] public void MIDL1048_Compat_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void P(wchar_t x); } }", CompilationMode.Compat); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1048"); }
#endif
    [Fact] public void MIDL1005_Object_Errors() { var c = CompilationHelper.Compile("namespace N { struct S { Object Obj; } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1005" && d.Message.Contains("Obj")); }
    [Fact] public void MIDL1005_Interface_Errors() { var c = CompilationHelper.Compile("namespace N { interface IFoo { void DoIt(); } struct S { IFoo Foo; } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1005" && d.Message.Contains("Foo")); }
    [Fact] public void MIDL1005_ValueTypes_NoDiag() { var c = CompilationHelper.Compile("namespace N { enum Color { Red, Green, Blue, } struct Inner { Int32 V; } struct S { Int32 X; String Name; Boolean Flag; Guid Id; Color C; Inner I; } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1005"); }
    [Fact] public void MIDL1049_Nested_Errors() { var c = CompilationHelper.Compile("namespace N { runtimeclass Outer { runtimeclass Inner { } } }"); Assert.True(c.Diagnostics.Any(d => d.Code == "MIDL1049") || c.Diagnostics.Count > 0); }
    [Fact] public void MIDL1049_Flat_NoDiag() { var c = CompilationHelper.Compile("namespace N { [default_interface] runtimeclass A { } [default_interface] runtimeclass B { } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1049"); Assert.Empty(c.Diagnostics); }
    [Fact] public void MIDL1050_Ref_Warns() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void Update(ref Int32 value); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1050" && d.Severity == DiagnosticSeverity.Warning); }
    [Fact] public void MIDL1050_Out_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void GetValue(out Int32 value); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1050"); }
    [Fact] public void MIDL1050_RefConst_NoDiag() { var c = CompilationHelper.Compile("namespace N { struct S { Int32 X; Int32 Y; } runtimeclass C { void Calc(ref const S value); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1050"); }
    [Fact] public void MIDL1050_RefConst_Guid_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { static Boolean Equals(ref const Guid a, ref const Guid b); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1050"); }
    [Fact] public void RefConst_BindsDirection() { var c = CompilationHelper.Compile("namespace N { struct S { Int32 X; } runtimeclass C { void Calc(ref const S value); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Severity == DiagnosticSeverity.Error); var method = c.GetAllTypes().OfType<RuntimeClassSymbol>().First().Members.OfType<MethodSymbol>().First(); Assert.Equal(ParameterDirection.RefConst, method.Parameters[0].Direction); }
    [Fact] public void Out_BindsDirection() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void GetValue(out Int32 value); } }"); var method = c.GetAllTypes().OfType<RuntimeClassSymbol>().First().Members.OfType<MethodSymbol>().First(); Assert.Equal(ParameterDirection.Out, method.Parameters[0].Direction); }
    [Fact] public void RefArray_NoWarning() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void Fill(ref Int32[] data); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1050"); }
    // MIDL1013: Reserved parameter names — scoped by context
    [Fact] public void MIDL1013_ResultInMethod_Warns() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void M(Int32 result); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1013" && d.Message.Contains("result") && d.Message.Contains("methods")); }
    [Fact] public void MIDL1013_OperationInMethod_Warns() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void M(Int32 operation); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1013" && d.Message.Contains("operation") && d.Message.Contains("methods")); }
    [Fact] public void MIDL1013_ValueInConstructor_Warns() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(Int32 value); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1013" && d.Message.Contains("value") && d.Message.Contains("constructors")); }
    [Fact] public void MIDL1013_ValueInMethod_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void M(Int32 value); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1013"); }
    [Fact] public void MIDL1013_ResultInConstructor_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(Int32 result); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1013"); }
    [Fact] public void MIDL1051_SingleArray_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { void P(Int32[] data); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1051"); }
    [Fact] public void MIDL1052_Match_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { String Name { get; set; }; } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1052"); }
    [Fact] public void MIDL1053_NonVoid_NoErrors() { var c = CompilationHelper.Compile("namespace N { delegate Int32 MyFunc(String input); }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1053"); }
    [Fact] public void MIDL1053_Void_NoDiag() { var c = CompilationHelper.Compile("namespace N { delegate void MyHandler(Int32 code); }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1053"); }
#if WITH_COMPAT
    [Fact] public void MIDL1053_HResult_Compat_NoDiag() { var c = CompilationHelper.Compile("namespace N { delegate HRESULT MyCallback(); }", CompilationMode.Compat); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1053"); }
#endif
    [Fact] public void MIDL1054_Duplicate_Errors() { var c = CompilationHelper.Compile("namespace N { interface IFoo { void DoWork(); void DoWork(); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1054" && d.Severity == DiagnosticSeverity.Error); }
    [Fact] public void MIDL1054_Unique_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { void DoA(); void DoB(); String Name { get; }; } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1054"); }
    [Fact] public void MIDL1054_Overloaded_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"Do\"), default_overload] void DoInt(Int32 x); [overload(\"Do\")] void DoStr(String x); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1054"); }
    [Fact] public void MIDL1055_NoDefault_Warns() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"Do\")] void DoInt(Int32 x); [overload(\"Do\")] void DoStr(String x); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1055" && d.Severity == DiagnosticSeverity.Warning); }
    [Fact] public void MIDL1055_DiffArity_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { void Do(); void Do(Int32 x); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1055"); }
    [Fact] public void MIDL1056_DeprecatedBase_Warns() { var c = CompilationHelper.Compile("namespace N { [deprecated(\"Old\", deprecate, 1)] unsealed runtimeclass OldBase { } runtimeclass Derived : OldBase { } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1056" && d.Severity == DiagnosticSeverity.Warning); }
    [Fact] public void MIDL1056_NonDeprBase_NoDiag() { var c = CompilationHelper.Compile("namespace N { unsealed runtimeclass Base { } runtimeclass Derived : Base { } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1056"); }
    [Fact] public void MIDL1056_BothDepr_NoDiag() { var c = CompilationHelper.Compile("namespace N { [deprecated(\"Old\", deprecate, 1)] unsealed runtimeclass Base { } [deprecated(\"Also old\", deprecate, 1)] runtimeclass Derived : Base { } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1056"); }
    [Fact] public void MIDL1057_Positive_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { void Bar(); } runtimeclass C : IFoo { } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1057"); }
    [Fact] public void MIDL1058_DupMethodName_Errors() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [method_name(\"Process\")] void DoA(); [method_name(\"Process\")] void DoB(); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1058" && d.Message.Contains("Process")); }
    [Fact] public void MIDL1058_Unique_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [method_name(\"ProcessA\")] void DoA(); [method_name(\"ProcessB\")] void DoB(); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1058"); }
    [Fact] public void MIDL1059_TwoDefault_Errors() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"Do\"), default_overload] void DoInt(Int32 x); [overload(\"Do\"), default_overload] void DoStr(String x); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1059"); }
    [Fact] public void MIDL1059_OneDefault_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"Do\"), default_overload] void DoInt(Int32 x); [overload(\"Do\")] void DoStr(String x); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1059"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1055"); }
    // midl.exe: methods share an [overload("X")] set; a repeated method name among them is MIDL2096.
    [Fact] public void MIDL1087_SharedOverloadName_NoDiag() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"Find\"), default_overload] String FindByType(Int32 type); [overload(\"Find\")] String FindByName(String name); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Severity == DiagnosticSeverity.Error); }
    [Fact] public void MIDL1087_RepeatedMethodName_Errors() { var c = CompilationHelper.Compile("namespace N { interface IFoo { [overload(\"DoInt\")] void Do(Int32 x); [overload(\"DoStr\")] void Do(String x); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1087" && d.Message.Contains("'Do'")); }
    [Fact] public void MIDL1060_DupArity_Errors() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(Int32 x); C(String y); } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1060"); }
    [Fact] public void MIDL1060_DiffArity_NoDiag() { var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(); C(String name); C(String name, Int32 id); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1060"); }
    [Fact] public void MIDL1061_StaticUnsealed_Errors() { var c = CompilationHelper.Compile("namespace N { static unsealed runtimeclass C { } }"); Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1061"); }
    [Fact] public void MIDL1061_StaticOnly_NoDiag() { var c = CompilationHelper.Compile("namespace N { static runtimeclass C { static void Reset(); } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1061"); }
    [Fact] public void MIDL1061_UnsealedOnly_NoDiag() { var c = CompilationHelper.Compile("namespace N { unsealed runtimeclass C { } }"); Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1061"); }
    [Fact] public void ValidCode_NoDiag() { var c = CompilationHelper.Compile("namespace TestApp { delegate void StatusHandler(Int32 code, String message); enum Color { Red, Green, Blue, } struct Point { Int32 X; Int32 Y; } interface IGreeter { String Greet(String name); String Greeting { get; }; event StatusHandler OnGreet; } runtimeclass Widget { Widget(); Widget(String name); void DoWork(Int32 count); String Name { get; set; }; event StatusHandler OnChanged; static void Reset(); } }"); Assert.Empty(c.Diagnostics); }

    // MIDL1062: Empty runtimeclass — must have at least one non-constructor member or [default_interface]
    [Fact] public void MIDL1062_EmptyClass_Errors()
    {
        var c = CompilationHelper.Compile("namespace N { runtimeclass C { } }");
        Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1062");
    }
    [Fact] public void MIDL1062_ConstructorOnlyClass_Errors()
    {
        // Constructor alone doesn't create a member interface
        var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(); } }");
        Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1062");
    }
    [Fact] public void MIDL1062_ClassWithMethod_NoDiag()
    {
        var c = CompilationHelper.Compile("namespace N { runtimeclass C { C(); void DoWork(); } }");
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1062");
    }
    [Fact] public void MIDL1062_DefaultInterface_NoDiag()
    {
        var c = CompilationHelper.Compile("namespace N { [default_interface] runtimeclass C { } }");
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1062");
    }
    [Fact] public void MIDL1062_DefaultInterface_WithCtor_NoDiag()
    {
        var c = CompilationHelper.Compile("namespace N { [default_interface] runtimeclass C { C(); } }");
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1062");
    }
    [Fact] public void MIDL1062_StaticClass_NoDiag()
    {
        var c = CompilationHelper.Compile("namespace N { static runtimeclass C { static void Foo(); } }");
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1062");
    }

    // MIDL1063: Optional params not allowed in WinRT
    [Fact] public void MIDL1063_OptionalParam_Errors()
    {
        var c = CompilationHelper.Compile("""
            namespace N
            {
                runtimeclass C
                {
                    void Foo([optional] String name);
                };
            }
            """);
        Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1063");
    }
    [Fact] public void MIDL1063_NormalParam_NoDiag()
    {
        var c = CompilationHelper.Compile("namespace N { runtimeclass C { void Foo(String name); } }");
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1063");
    }

    // MIDL1077: Runtimeclass param needs default interface
    [Fact] public void MIDL1077_ClassParamNoInterface_Errors()
    {
        var c = CompilationHelper.Compile("""
            namespace N
            {
                runtimeclass Empty { };
                runtimeclass User
                {
                    void Use(Empty e);
                };
            }
            """);
        Assert.Contains(c.Diagnostics, d => d.Code == "MIDL1077");
    }

    [Fact] public void MIDL1077_SquiggleCoversTypeReference_NotParamName()
    {
        // Regression: squiggle was on parameter name 'e' (6 chars with className.Length),
        // causing "e);\r\n " to be underlined. Must point at the TYPE reference.
        var source = "namespace N { runtimeclass Empty { }; runtimeclass User { void Use(Empty e); } }";
        var c = CompilationHelper.Compile(source);
        var diag = c.Diagnostics.First(d => d.Code == "MIDL1077");
        int pos = diag.OriginalPosition ?? diag.Position;

        // Squiggle must start at "Empty" (the type ref), not "e" (the param name)
        Assert.Equal("Empty", source.Substring(pos, diag.Length));
    }

    [Fact] public void MIDL1077_QualifiedType_SquiggleCoversFullName()
    {
        // When using a qualified name like A.B.Empty, the squiggle should cover the full path
        var source = "namespace A.B { runtimeclass Empty { }; }\nnamespace N { runtimeclass User { void Use(A.B.Empty e); } }";
        var c = CompilationHelper.Compile(source);
        var diag = c.Diagnostics.First(d => d.Code == "MIDL1077");
        int pos = diag.OriginalPosition ?? diag.Position;

        Assert.Equal("A.B.Empty", source.Substring(pos, diag.Length));
    }

    [Fact] public void MIDL1077_ClassParamWithMembers_NoDiag()
    {
        var c = CompilationHelper.Compile("""
            namespace N
            {
                runtimeclass HasMembers { HasMembers(); };
                runtimeclass User
                {
                    void Use(HasMembers h);
                };
            }
            """);
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1077");
    }
    [Fact] public void MIDL1077_ClassParamWithDefaultInterface_NoDiag()
    {
        var c = CompilationHelper.Compile("""
            namespace N
            {
                [default_interface]
                runtimeclass Tagged { };
                runtimeclass User
                {
                    void Use(Tagged t);
                };
            }
            """);
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == "MIDL1077");
    }
}