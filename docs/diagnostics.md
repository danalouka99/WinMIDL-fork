# MIDL Compiler Diagnostic Reference

This document lists all diagnostics emitted by the MIDL compiler, organized by category.

## Quick Reference

| Code | Severity | Summary |
|------|----------|---------|
| [MIDL1001](#midl1001) | Error | Undefined type |
| [MIDL1002](#midl1002) | Error | Duplicate definition |
| [MIDL1003](#midl1003) | Error | Namespace required |
| [MIDL1004](#midl1004) | Error | Invalid base class |
| [MIDL1005](#midl1005) | Error | Struct field not a value type |
| [MIDL1006](#midl1006) | Error | Invalid enum underlying type |
| [MIDL1007](#midl1007) | Error | Instance member in static class |
| [MIDL1008](#midl1008) | Error | \[flags\] enum must be UInt32 |
| [MIDL1009](#midl1009) | Error | Empty struct |
| [MIDL1010](#midl1010) | Error | Case-insensitive type name collision |
| [MIDL1011](#midl1011) | Warning | Same-arity method overload |
| [MIDL1012](#midl1012) | Error | Write-only property |
| [MIDL1013](#midl1013) | Warning | Reserved parameter name |
| [MIDL1014](#midl1014) | Error | Interface version too early |
| [MIDL1015](#midl1015) | Error | Contract family mismatch |
| [MIDL1016](#midl1016) | Error | Undefined contract |
| [MIDL1017](#midl1017) | Error | Enum member version too early |
| [MIDL1020](#midl1020) | Error | Invalid threading model |
| [MIDL1021](#midl1021) | Error | Invalid marshaling behavior |
| [MIDL1022](#midl1022) | Error | Invalid composable access |
| [MIDL1023](#midl1023) | Error | Activatable factory not \[exclusiveto\] |
| [MIDL1024](#midl1024) | Error | Static interface not \[exclusiveto\] |
| [MIDL1025](#midl1025) | Error | Composable and activatable conflict |
| [MIDL1026](#midl1026) | Error | Invalid deprecation type |
| [MIDL1027](#midl1027) | Warning | Threading on non-activatable class |
| [MIDL1028](#midl1028) | Error | \[attributeusage\] on non-attribute |
| [MIDL1029](#midl1029) | Error | Attribute wrong target |
| [MIDL1030](#midl1030) | Error | Attribute argument count mismatch |
| [MIDL1031](#midl1031) | Error | Partial conflicting modifiers |
| [MIDL1032](#midl1032) | Error | Partial conflicting base class |
| [MIDL1033](#midl1033) | Error | Protected/overridable on sealed class |
| [MIDL1034](#midl1034) | Error | Undefined required interface |
| [MIDL1035](#midl1035) | Error | Base type is not a runtimeclass |
| [MIDL1036](#midl1036) | Error | Base class is sealed |
| [MIDL1038](#midl1038) | Error | Runtimeclass in requires clause |
| [MIDL1039](#midl1039) | Error | Constructor in interface |
| [MIDL1040](#midl1040) | Error | Static member in interface |
| [MIDL1041](#midl1041) | Error | Property or event in struct |
| [MIDL1042](#midl1042) | Error | Event with non-delegate type |
| [MIDL1043](#midl1043) | Error | Void parameter type |
| [MIDL1044](#midl1044) | Warning | Duplicate method name without \[overload\] |
| [MIDL1045](#midl1045) | Error | Circular struct reference |
| [MIDL1046](#midl1046) | Error | Duplicate enum member |
| [MIDL1047](#midl1047) | Error | Import file not found |
| [MIDL1048](#midl1048) | Error | C-only type in strict mode |
| [MIDL1049](#midl1049) | Error | Nested type declaration |
| [MIDL1050](#midl1050) | Warning | Bidirectional (ref) parameter |
| [MIDL1051](#midl1051) | Error | Array of arrays |
| [MIDL1052](#midl1052) | Error | Property type inconsistency |
| [MIDL1053](#midl1053) | Error | Delegate non-void return |
| [MIDL1054](#midl1054) | Warning | Duplicate interface member name |
| [MIDL1055](#midl1055) | Warning | Same-arity overload without [default_overload] |
| [MIDL1056](#midl1056) | Warning | Deprecated base class inheritance |
| [MIDL1057](#midl1057) | Info | Interface method not implemented |
| [MIDL1058](#midl1058) | Error | Duplicate [method_name] value |
| [MIDL1059](#midl1059) | Error | Multiple [default_overload] on same arity |
| [MIDL1060](#midl1060) | Error | Constructor arity conflict per version |
| [MIDL1061](#midl1061) | Error | Static + unsealed conflict |
| [MIDL1062](#midl1062) | Error | Empty runtimeclass without interface |
| [MIDL1063](#midl1063) | Error | Optional parameter not allowed |
| [MIDL1064](#midl1064) | Error | Attribute argument type mismatch |
| [MIDL1065](#midl1065) | Warning | Unknown attribute |
| [MIDL1066](#midl1066) | Error | Attribute argument out of range |
| [MIDL1067](#midl1067) | Error | Enum value must be integer |
| [MIDL1068](#midl1068) | Error | Enum value out of range |
| [MIDL1069](#midl1069) | Error | ExclusiveTo violation |
| [MIDL1070](#midl1070) | Warning | Missing default interface |
| [MIDL1071](#midl1071) | Warning | WinMD namespace mismatch |
| [MIDL1072](#midl1072) | Error | Protected on individual accessor |
| [MIDL1073](#midl1073) | Error | Protected member on interface |
| [MIDL1075](#midl1075) | Error | Contract arg not apicontract |
| [MIDL1076](#midl1076) | Error | Feature arg not feature\_name |
| [MIDL1077](#midl1077) | Error | Parameter class needs default interface |
| [MIDL1078](#midl1078) | Error | Circular interface requires |
| [MIDL1079](#midl1079) | Error | Duplicate parameter name |
| [MIDL1080](#midl1080) | Error | Versioned block conflict |
| [MIDL1081](#midl1081) | Error | UUID wrong format |
| [MIDL1082](#midl1082) | Error | \[default\_interface\] on non-class |
| [MIDL1083](#midl1083) | Error | Void property type |
| [MIDL1084](#midl1084) | Error | Static class instance member |
| [MIDL1085](#midl1085) | Error | Duplicate property name |
| [MIDL1086](#midl1086) | Error | Duplicate \[uuid\] attribute |
| [MIDL1087](#midl1087) | Error | Duplicate \[overload\] method name |
| [MIDL1088](#midl1088) | Error | \[marshaling\_behavior\] on non-class |
| [MIDL1089](#midl1089) | Error | Field in runtimeclass |
| [MIDL1090](#midl1090) | Error | \[threading\] on non-class |
| [MIDL1091](#midl1091) | Error | Constructor with return type |
| [MIDL1092](#midl1092) | Error | \[exclusiveto\] self-reference |
| [MIDL1093](#midl1093) | Error | Contract version exceeds max |
| [MIDL1094](#midl1094) | Error | \[uuid\] on non-interface |
| [MIDL1095](#midl1095) | Error | \[activatable\] on non-class |
| [MIDL1096](#midl1096) | Error | \[deprecated\] wrong argument count |
| [MIDL1097](#midl1097) | Error | \[overload\] on property or event |
| [MIDL1098](#midl1098) | Error | Empty enum |
| [MIDL1099](#midl1099) | Error | Member name same as containing type |
| [MIDL1100](#midl1100) | Error | Static modifier on non-class |
| [MIDL1101](#midl1101) | Error | Generic type requires arguments |
| [MIDL1102](#midl1102) | Error | @ref target not found |
| [MIDL1103](#midl1103) | Error | Invalid feature stage |
| [MIDL1104](#midl1104) | Error | Non-experimental references experimental |
| [MIDL1105](#midl1105) | Warning | Cross-feature-gate reference |
| [MIDL2001](#midl2001) | Error | Classic MIDL construct (strict mode) |
| [MIDL2002](#midl2002) | Error | HRESULT return type (strict mode) |
| [MIDL2003](#midl2003) | Error | Classic MIDL attribute (strict mode) |
| [MIDL2004](#midl2004) | Error | Pointer type (strict mode) |
| [MIDL2005](#midl2005) | Error | Classic MIDL type name (strict mode) |
| [MIDL2006](#midl2006) | Warning | #include in strict mode |
| [MIDL3001](#midl3001) | Info | Interface naming convention |
| [MIDL3002](#midl3002) | Info | Empty runtimeclass |
| [MIDL3003](#midl3003) | Info | Empty interface |
| [MIDL3005](#midl3005) | Info | Runtimeclass has no constructor |
| [MIDL3006](#midl3006) | Info | Event handler naming convention |
| [MIDL3007](#midl3007) | Info | Property setter without getter |
| [MIDL3008](#midl3008) | Info | Method name clashes with type name |
| [MIDL3009](#midl3009) | Info | Namespace casing convention |
| [MIDL3010](#midl3010) | Info | Attribute naming convention |
| [MIDL3011](#midl3011) | Info | Delegate naming convention |
| [MIDL3012](#midl3012) | Info | Event args naming convention |
| [MIDL3013](#midl3013) | Info | Async method naming convention |
| [MIDL3014](#midl3014) | Info | Type name PascalCase |
| [MIDL3015](#midl3015) | Info | Member name PascalCase |
| [MIDL3016](#midl3016) | Info | Property can use shorthand |

---


### MIDL1048
**C-only type in strict mode** — Types like `wchar_t`, `handle_t`, `error_status_t` are not valid WinRT types in strict MIDL3 mode.

### MIDL1049
**Nested type declaration** — WinRT does not support nested type declarations (e.g., a runtimeclass inside another runtimeclass).

### MIDL1050
**Bidirectional parameter** — The `ref` (bidirectional) parameter direction is not recommended in WinRT. Use `in` or `out` instead.

### MIDL1051
**Array of arrays** — WinRT does not support jagged arrays (`T[][]`). Use a single-dimensional array instead.

### MIDL1052
**Property type inconsistency** — A property's getter return type must match its setter parameter type.

### MIDL1053
**Delegate non-void return** — WinRT delegates must return `void`. In compat mode, `HRESULT` is also allowed.

### MIDL1054
**Duplicate interface member name** — Interface members must have unique names. Use `[overload]` to disambiguate methods with the same name.

### MIDL1055
**Same-arity overload needs [default_overload]** — When methods of an overload set (the same `[overload]` value, or the same name without `[overload]`) have the same parameter count, exactly one must have `[default_overload]` (midl.exe MIDL4015).

### MIDL1056
**Deprecated base class** — Consider marking the derived class as deprecated when the base class is deprecated.

### MIDL1057
**Interface method info** — Informational diagnostic noting interface method declarations on implemented interfaces.

### MIDL1058
**Duplicate method_name** — Each `[method_name("X")]` value must be unique within an interface.

### MIDL1059
**Multiple default_overload** — For same-arity methods of an overload set, exactly one must have `[default_overload]`, not more (midl.exe MIDL4015).

### MIDL1060
**Constructor arity conflict** — Cannot have two constructors with the same parameter count on the same contract version.

### MIDL1061
**Static + unsealed conflict** — A runtimeclass cannot be both `static` and `unsealed`.

---

### MIDL1062

**Empty runtimeclass without interface**

```
error MIDL1062: Runtimeclass 'Widget' must implement at least one interface or have [default_interface].
```

A runtimeclass has no interface implementations and no `[default_interface]` attribute.

**Fix:** Add an interface implementation or use `[default_interface]`:

```idl
// Option 1: implement an interface
runtimeclass Widget : IWidget { }

// Option 2: use [default_interface] to auto-generate one
[default_interface]
runtimeclass Widget
{
    void DoWork();
}
```

---

### MIDL1063

**Optional parameter not allowed**

```
error MIDL1063: Parameter 'count' in 'DoWork' cannot be optional. WinRT does not support optional, default, or vararg parameters.
```

WinRT does not support optional, default-valued, or variadic parameters.

**Fix:** Create method overloads instead:

```idl
void DoWork();
[method_name("DoWorkWithCount")]
void DoWork(Int32 count);
```

---

### MIDL1064
**Attribute argument type mismatch** — An attribute argument has the wrong type for its corresponding field. For example, passing a string literal where an Int32 is expected.

**Fix:** Use the correct literal type: integers for Int32/UInt32 fields, strings for String fields, etc.

---

### MIDL1065
**Unknown attribute** — An attribute name is not recognized as a built-in attribute or a user-defined custom attribute.

**Fix:** Declare the attribute with the `attribute` keyword, or check the spelling:
```idl
[attributeusage(target_runtimeclass)]
attribute MyAttr { Int32 Level; };
```

---

### MIDL1066
**Attribute argument out of range** — A numeric attribute argument is outside the valid range for the field type (e.g., 256 for UInt8).

---

### MIDL1067
**Enum value must be integer** — Enum member values must be integer literals. Float or string literals are not valid.

**Fix:** Use an integer: `A = 42` or `A = 0xFF`

---

### MIDL1068
**Enum value out of range** — An enum member value exceeds the range of the underlying type (Int32 for standard enums, UInt32 for `[flags]` enums).

---

### MIDL1069
**ExclusiveTo violation** — An interface marked `[exclusiveto(ClassA)]` cannot be implemented by a different class.

---

### MIDL1070
**Missing default interface** — A runtimeclass implements multiple interfaces but none is marked `[default]`. The first interface will be used as default.

---

### MIDL1071
**WinMD namespace mismatch** — The explicit WinMD output filename doesn't match the root namespace of the types it contains.

---

### MIDL1072
**Protected on individual accessor** — Individual property accessors cannot be marked `protected` in MIDL3. Apply `protected` to the whole property.

```idl
// Wrong:
String Name { protected set; get; };

// Right:
protected String Name { get; set; };
```

---

### MIDL1073
**Protected member on interface** — Interface members are always public. The `protected` modifier is only valid on `unsealed runtimeclass` members.

---

### MIDL1075

**Contract arg not apicontract**

```
error MIDL1075: [contract] on 'Widget': 'NotAContract' is not declared as an apicontract.
```

The type referenced in a `[contract]` attribute is not declared as an `apicontract`.

**Fix:** Declare the type as an `apicontract`:

```idl
apicontract MyContract { }

[contract(MyContract, 1.0)]
runtimeclass Widget { }
```

---

### MIDL1076

**Feature arg not feature\_name**

```
error MIDL1076: [feature] on 'Widget': 'NotAFeature' is not declared via feature_name.
```

The name referenced in a `[feature]` attribute is not declared as a `feature_name`.

**Fix:** Declare the feature with `feature_name` before using it in `[feature]`.

---

### MIDL1077

**Parameter class needs default interface**

```
error MIDL1077: Parameter 'widget' in 'Process': runtimeclass 'Widget' has no default interface and cannot be used as a parameter type.
```

A runtimeclass used as a parameter type must have a default interface.

**Fix:** Add a default interface to the runtimeclass:

```idl
[default_interface]
runtimeclass Widget { void DoWork(); }
```

---

### MIDL1078

**Circular interface requires**

```
error MIDL1078: Interface 'IFoo' has a circular 'requires' dependency: IFoo → IBar → IFoo.
```

Two or more interfaces form a cycle through their `requires` clauses.

**Fix:** Break the cycle by removing one of the `requires` dependencies:

```idl
// Wrong:
interface IFoo requires IBar { }
interface IBar requires IFoo { }

// Right:
interface IFoo { }
interface IBar requires IFoo { }
```

---

### MIDL1079

**Duplicate parameter name**

```
error MIDL1079: Duplicate parameter name 'value' in 'SetValues'.
```

A method has two parameters with the same name.

**Fix:** Rename one of the parameters:

```idl
void SetValues(Int32 first, Int32 second);
```

---

### MIDL1080

**Versioned block conflict**

```
error MIDL1080: Versioned block version 1.0 is lower than containing type's contract version 2.0.
```

A versioned block specifies a version lower than the enclosing type's contract version.

**Fix:** Ensure the versioned block version is at least as high as the containing type's version.

---

### MIDL1081

**UUID wrong format**

```
error MIDL1081: Invalid UUID format 'not-a-uuid': expected 8-4-4-4-12 hexadecimal digits.
```

A `[uuid]` attribute value does not match the expected UUID format.

**Fix:** Use the standard 8-4-4-4-12 hexadecimal format:

```idl
[uuid(01234567-89ab-cdef-0123-456789abcdef)]
interface IWidget { }
```

---

### MIDL1082

**\[default\_interface\] on non-class**

```
error MIDL1082: [default_interface] is only valid on runtimeclass, not on 'IWidget'.
```

The `[default_interface]` attribute can only be applied to runtimeclass declarations.

**Fix:** Move `[default_interface]` to a runtimeclass, or remove it from the interface/struct.

---

### MIDL1083

**Void property type**

```
error MIDL1083: Property 'Value' cannot have void type.
```

A property is declared with `void` type. Properties must have a non-void type.

**Fix:** Use an actual type for the property:

```idl
Int32 Value { get; };
```

---

### MIDL1084

**Static class instance member**

```
error MIDL1084: Static runtimeclass 'Helpers' cannot have instance member 'DoWork'.
```

A static runtimeclass contains a non-static member.

**Fix:** Add the `static` keyword to the member, or remove `static` from the class declaration.

---

### MIDL1085

**Duplicate property name**

```
error MIDL1085: Duplicate property 'Name' in 'Widget'.
```

A type declares two properties with the same name.

**Fix:** Remove the duplicate property or rename one of them.

---

### MIDL1086

**Duplicate \[uuid\] attribute**

```
error MIDL1086: Type 'IWidget' has multiple [uuid] attributes.
```

A type has more than one `[uuid]` attribute applied.

**Fix:** Remove duplicate `[uuid]` attributes, keeping only one.

---

### MIDL1087

**Duplicate \[overload\] method name**

```
error MIDL1087: Duplicate [overload] name 'DoWork' in 'IWidget'.
```

Two `[overload]` methods in the same interface have the same name. As in midl.exe, the `[overload("X")]` value
names the overload set: it is the projected name (written as the MethodDef name) and its methods share it. Each
method's own name is its unique ABI name (written to `OverloadAttribute`). midl.exe reports this case as MIDL2096.

**Fix:** Share the `[overload]` value and give each method a unique name:

```idl
[overload("DoWork"), default_overload]
void DoWorkWithInt(Int32 x);
[overload("DoWork")]
void DoWorkWithString(String s);
```

---

### MIDL1088

**\[marshaling\_behavior\] on non-class**

```
error MIDL1088: [marshaling_behavior] is only valid on runtimeclass, not on 'IWidget'.
```

The `[marshaling_behavior]` attribute can only be applied to runtimeclass declarations.

**Fix:** Move `[marshaling_behavior]` to a runtimeclass declaration.

---

### MIDL1089

**Field in runtimeclass**

```
error MIDL1089: Fields are not allowed in runtimeclass. Use a property for 'Name'.
```

A runtimeclass contains a field declaration. WinRT runtimeclasses use properties, not fields.

**Fix:** Use a property instead:

```idl
// Wrong:
runtimeclass Widget { String Name; }

// Right:
runtimeclass Widget { String Name { get; set; }; }
```

---

### MIDL1090

**\[threading\] on non-class**

```
error MIDL1090: [threading] is only valid on runtimeclass, not on 'IWidget'.
```

The `[threading]` attribute can only be applied to runtimeclass declarations.

**Fix:** Move `[threading]` to a runtimeclass declaration.

---

### MIDL1091

**Constructor with return type**

```
error MIDL1091: Constructor 'Widget' cannot have a return type.
```

A constructor declaration specifies a return type. Constructors implicitly return the class type.

**Fix:** Remove the return type from the constructor:

```idl
// Wrong:
runtimeclass Widget { void Widget(); }

// Right:
runtimeclass Widget { Widget(); }
```

---

### MIDL1092

**\[exclusiveto\] self-reference**

```
error MIDL1092: [exclusiveto] on 'IWidget' must reference a runtimeclass, not the interface itself.
```

An `[exclusiveto]` attribute on an interface points back to the same interface instead of a runtimeclass.

**Fix:** Reference a runtimeclass:

```idl
[exclusiveto(Widget)]
interface IWidgetInternal { }
```

---

### MIDL1093

**Contract version exceeds max**

```
error MIDL1093: Contract version 5 exceeds 'MyContract' contractversion 3.
```

A `[contract]` attribute references a version number higher than the declared `contractversion`.

**Fix:** Either increase the `contractversion`, or lower the version number:

```idl
[contractversion(5)]
apicontract MyContract { }
```

---

### MIDL1094

**\[uuid\] on non-interface**

```
error MIDL1094: [uuid] is only valid on interfaces and delegates, not on 'Widget'.
```

The `[uuid]` attribute can only be applied to interfaces and delegates.

**Fix:** Remove `[uuid]` from runtimeclass, struct, or enum declarations.

---

### MIDL1095

**\[activatable\] on non-class**

```
error MIDL1095: [activatable] is only valid on runtimeclass, not on 'IWidget'.
```

The `[activatable]` attribute can only be applied to runtimeclass declarations.

**Fix:** Move `[activatable]` to a runtimeclass declaration.

---

### MIDL1096

**\[deprecated\] wrong argument count**

```
error MIDL1096: [deprecated] requires 3 or 4 arguments (message, type, [contract,] version), got 1.
```

The `[deprecated]` attribute does not have the correct number of arguments.

**Fix:** Provide all required arguments:

```idl
[deprecated("Use NewMethod instead", deprecate, MyContract, 2.0)]
void OldMethod();
```

---

### MIDL1097

**\[overload\] on property or event**

```
error MIDL1097: [overload] may not be applied to properties or events. Found on 'Name'.
```

The `[overload]` attribute is only valid on methods, not on properties or events.

**Fix:** Remove `[overload]` from the property or event declaration.

---

### MIDL1098

**Empty enum**

```
error MIDL1098: Enum 'Status' must have at least one member.
```

An enum is declared with no members. WinRT enums must have at least one value.

**Fix:** Add at least one member:

```idl
enum Status { None, Active, Inactive }
```

---

### MIDL1099

**Member name same as containing type**

```
error MIDL1099: Method 'Widget' cannot have the same name as containing type 'Widget'. Only constructors can use the type name.
```

A method or property shares its name with the containing type.

**Fix:** Rename the member, or if it's a constructor, remove the return type:

```idl
// Wrong:
runtimeclass Widget { void Widget(); }

// Right (constructor):
runtimeclass Widget { Widget(); }

// Right (method):
runtimeclass Widget { void CreateWidget(); }
```

---

## Semantic Errors (MIDL1001–MIDL1061)

These errors are emitted during semantic analysis (binding phase).

### MIDL1001

**Undefined type**

```
error MIDL1001: Undefined type 'NonExistentType'
```

A type referenced in a declaration could not be found. The type may be misspelled, missing an `import`, or missing a `/reference` to a `.winmd` file.

**Fix:** Check the spelling, add an `import "file.idl"` directive, or add `/reference path.winmd` to reference external types.

---

### MIDL1002

**Duplicate definition**

```
error MIDL1002: Duplicate definition 'Widget'
```

Two types with the same name are defined in the same namespace.

**Fix:** Rename one of the types, or move it to a different namespace.

---

### MIDL1003

**Namespace required**

```
error MIDL1003: Namespace required for WinRT type 'MyClass'
```

All WinRT types must be declared inside a `namespace` block. Types at the global scope are not valid.

**Fix:** Wrap the type declaration in a namespace:

```idl
namespace MyApp
{
    runtimeclass MyClass { }
}
```

---

### MIDL1004

**Invalid base class**

```
error MIDL1004: Invalid base class 'SealedClass' (not unsealed)
```

A runtime class is trying to derive from a class that is not marked as `unsealed`.

**Fix:** Mark the base class as `unsealed runtimeclass`, or remove the inheritance.

---

### MIDL1005

**Struct field not a value type**

```
error MIDL1005: Struct field 'Data' must be a value type, string, or IReference<T>
```

WinRT structs can only contain fields of value types (integers, floats, enums, other structs), `String`, or `Windows.Foundation.IReference<T>`. Reference types (interfaces, runtime classes) are not allowed.

**Fix:** Change the field type to a value type, or use a runtime class instead of a struct.

---

### MIDL1006

**Invalid enum underlying type**

```
error MIDL1006: Enum 'MyEnum' underlying type must be Int32 or UInt32
```

WinRT enums can only have `Int32` (for standard enums) or `UInt32` (for `[flags]` enums) as their underlying type.

**Fix:** Use a standard enum (Int32) or a `[flags]` enum (UInt32).

---

### MIDL1007

**Instance member in static class**

```
error MIDL1007: Static class cannot have instance member 'DoWork'
```

A class declared as `static runtimeclass` cannot have instance members — all members must be `static`.

**Fix:** Add the `static` keyword to the member, or remove `static` from the class.

---

### MIDL1008

**\[flags\] enum must be UInt32**

```
error MIDL1008: [flags] enum 'Permissions' must have UInt32 underlying type
```

Enums marked with `[flags]` must use `UInt32` as their underlying type. This is a WinRT requirement.

**Fix:** This is automatically handled — `[flags]` enums use UInt32, standard enums use Int32.

---

### MIDL1009

**Empty struct**

```
error MIDL1009: Empty struct 'EmptyStruct' — WinRT structs must have at least one field
```

WinRT structs must contain at least one field. Empty structs are not valid.

**Fix:** Add at least one field, or use a runtime class instead.

---

### MIDL1010

**Case-insensitive type name collision**

```
error MIDL1010: Type 'foo' conflicts with 'Foo' (WinRT type names are case-insensitive)
```

WinRT type names are case-insensitive. Two types that differ only by casing are not allowed in the same namespace.

**Fix:** Rename one of the types to have a distinct name.

---

### MIDL1011

**Same-arity method overload**

```
warning MIDL1011: Method 'DoWork' has same arity as 'DoWork' — use [default_overload] to disambiguate
```

Two methods with the same name and the same number of parameters exist on the same interface. Weakly-typed languages (like JavaScript) cannot distinguish between them.

**Fix:** Mark one overload with `[default_overload]`, or rename overloads using `[method_name("...")]`:

```idl
[default_overload]
void DoWork(Int32 x);
[method_name("DoWorkWithLabel")]
void DoWork(String label);
```

---

### MIDL1012

**Write-only property**

```
error MIDL1012: Property 'Value' cannot be write-only — WinRT does not support write-only properties
```

WinRT properties must have at least a getter. Write-only properties (`{ set; }` without `{ get; }`) are not supported.

**Fix:** Add a getter, or change to a method:

```idl
String Value { get; set; };  // read-write property
void SetValue(String v);     // or use a method
```

---

### MIDL1013

**Reserved parameter name**

```
warning MIDL1013: Reserved parameter name 'result' — use [return_name("alt")] to override
```

The parameter name is reserved by the MIDL compiler for auto-generated ABI parameters:

| Reserved name | Used by | Can override? |
|---------------|---------|---------------|
| `value` | Constructor return | Yes, via `[return_name]` |
| `result` | Method return | Yes, via `[return_name]` |
| `operation` | IAsyncXxx return | Yes, via `[return_name]` |
| `baseInterface` | Unsealed constructor | No |
| `innerInterface` | Unsealed constructor | No |

**Fix:** Rename the parameter, or use `[return_name("altName")]` to change the auto-generated name.

---

### MIDL1014

**Interface version too early**

```
error MIDL1014: Interface 'IWidgetFactory' version 1.0 is earlier than containing class version 2.0
```

An interface's contract version is lower than the contract version of the runtimeclass that implements it.

**Fix:** Update the interface's contract version to match or exceed the containing class version.

---

### MIDL1015

**Contract family mismatch**

```
error MIDL1015: Interface 'IFoo' uses contract 'OtherContract' which is not in the contract history of runtimeclass 'Widget'
```

An interface implemented by a runtimeclass uses a different contract family than the runtimeclass itself.

**Fix:** Ensure the interface uses the same contract family as the runtimeclass.

---

### MIDL1016

**Undefined contract**

```
error MIDL1016: Undefined contract 'MissingContract'
```

A `[contract]` attribute references a contract name that has not been declared.

**Fix:** Declare the contract with `apicontract`, or check the spelling:

```idl
namespace MyApp
{
    apicontract MyContract { }

    [contract(MyContract, 1.0)]
    runtimeclass Widget { }
}
```

---

### MIDL1017

**Enum member version too early**

```
error MIDL1017: Enum member 'NewValue' version 1.0 is earlier than containing enum version 2.0
```

An enum member's contract version is lower than the enum's own contract version.

**Fix:** Update the member's contract version to match or exceed the enum's version.

---

### MIDL1020

**Invalid threading model**

```
error MIDL1020: Invalid threading model 'free'. Must be 'both', 'sta', or 'mta'.
```

The `[threading]` attribute value is not one of the allowed values.

**Fix:** Use `both`, `sta`, or `mta`:

```idl
[threading(both)]
runtimeclass Widget { }
```

---

### MIDL1021

**Invalid marshaling behavior**

```
error MIDL1021: Invalid marshaling behavior 'none'.
```

The `[marshaling_behavior]` attribute value is not recognized.

**Fix:** Use a valid marshaling behavior value such as `agile`, `standard`, or `none`.

---

### MIDL1022

**Invalid composable access**

```
error MIDL1022: Invalid composable access 'private'.
```

The `[composable]` attribute has an invalid access modifier.

**Fix:** Use `public` or `protected`:

```idl
[composable(IWidgetFactory, public, 1.0)]
runtimeclass Widget { }
```

---

### MIDL1023

**Activatable factory not \[exclusiveto\]**

```
error MIDL1023: Activatable factory interface 'IWidgetFactory' must be [exclusiveto(Widget)].
```

An interface used as an activatable factory must be marked `[exclusiveto]` to the class that uses it.

**Fix:** Add `[exclusiveto]` to the factory interface:

```idl
[exclusiveto(Widget)]
interface IWidgetFactory
{
    Widget CreateWithName(String name);
}
```

---

### MIDL1024

**Static interface not \[exclusiveto\]**

```
error MIDL1024: Static interface 'IWidgetStatics' must be [exclusiveto(Widget)].
```

An interface used as a static interface must be marked `[exclusiveto]` to the class that exposes it.

**Fix:** Add `[exclusiveto]` to the static interface.

---

### MIDL1025

**Composable and activatable conflict**

```
error MIDL1025: Cannot have both [composable] and [activatable] on the same runtime class.
```

A runtimeclass has both `[composable]` and `[activatable]` attributes. These are mutually exclusive.

**Fix:** Remove one of the attributes. Use `[activatable]` for direct activation, or `[composable]` for classes designed to be subclassed.

---

### MIDL1026

**Invalid deprecation type**

```
error MIDL1026: Invalid deprecation type 'obsolete'.
```

The deprecation type argument in a `[deprecated]` attribute is not valid.

**Fix:** Use a valid deprecation type such as `deprecate` or `remove`:

```idl
[deprecated("Use NewMethod instead", deprecate, MyContract, 2.0)]
void OldMethod();
```

---

### MIDL1027

**Threading on non-activatable class**

```
warning MIDL1027: [threading] is only meaningful on activatable or composable runtime classes.
```

The `[threading]` attribute is applied to a class with no `[activatable]` or `[composable]` attribute, making it ineffective.

**Fix:** Add `[activatable]` or `[composable]` to the class, or remove the threading attribute.

---

### MIDL1028

**\[attributeusage\] on non-attribute**

```
error MIDL1028: [attributeusage] can only appear on attribute declarations, not on 'Widget'.
```

The `[attributeusage]` attribute can only be applied to custom attribute type declarations.

**Fix:** Move `[attributeusage]` to an `attribute` declaration:

```idl
[attributeusage(target_runtimeclass)]
attribute MyCustomAttr
{
    Int32 Level;
}
```

---

### MIDL1029

**Attribute wrong target**

```
error MIDL1029: Attribute 'MyAttr' is not valid on method. Valid targets: runtimeclass
```

A custom attribute is applied to a declaration type not in its valid target list.

**Fix:** Apply the attribute to a valid target, or update its `[attributeusage]` to include the desired target kind.

---

### MIDL1030

**Attribute argument count mismatch**

```
error MIDL1030: Attribute 'MyAttr' expects 2 arguments but got 1
```

The number of arguments passed to an attribute does not match its field count.

**Fix:** Pass the correct number of arguments:

```idl
attribute MyAttr { Int32 Level; String Tag; };

[MyAttr(1, "info")]  // Must provide both arguments
runtimeclass Widget { }
```

---

### MIDL1031

**Partial conflicting modifiers**

```
error MIDL1031: Partial runtimeclass 'Widget' has conflicting modifiers (sealed vs unsealed)
```

Two partial declarations of the same runtimeclass use conflicting modifiers.

**Fix:** Ensure all partial declarations use the same modifier:

```idl
unsealed runtimeclass Widget { void A(); }
unsealed runtimeclass Widget { void B(); }
```

---

### MIDL1032

**Partial conflicting base class**

```
error MIDL1032: Partial runtimeclass 'Widget' has conflicting base class ('BaseA' vs 'BaseB')
```

Two partial declarations of the same runtimeclass specify different base classes.

**Fix:** Ensure all partial declarations specify the same base class, or only specify it on one:

```idl
unsealed runtimeclass Widget : BaseA { void A(); }
runtimeclass Widget { void B(); }
```

---

### MIDL1033

**Protected or overridable on sealed class**

```
error MIDL1033: 'DoWork' cannot be protected in sealed runtimeclass 'MyClass'. Use 'unsealed runtimeclass' to allow inheritance.
error MIDL1033: 'OnChanged' cannot be overridable in sealed runtimeclass 'MyClass'. Use 'unsealed runtimeclass' to allow inheritance.
```

A member marked `protected` or `overridable` can only appear in an `unsealed runtimeclass`. Sealed classes (the default) cannot participate in inheritance, so these modifiers are invalid.

**Fix:** Mark the containing class as `unsealed runtimeclass`, or remove the `protected`/`overridable` modifier from the member.

---

### MIDL1034

**Undefined required interface**

```
error MIDL1034: Required interface 'IUndefined' is undefined.
```

An interface's `requires` clause references an interface name that cannot be resolved.

**Fix:** Check the spelling, add an `import` directive, or add a `/reference` to a `.winmd` file that defines the interface.

---

### MIDL1035

**Base type is not a runtimeclass**

```
error MIDL1035: Base type 'MyStruct' is not a runtimeclass. Use ':' for implemented interfaces.
```

A runtimeclass specifies a base type in its inheritance list, but the resolved type is a struct, enum, delegate, or other non-runtimeclass type. Only runtimeclasses can be base classes.

**Fix:** Use a runtimeclass as the base, or if you meant to implement an interface, ensure the type is declared as an `interface`.

---

### MIDL1036

**Base class is sealed**

```
error MIDL1036: Cannot derive from sealed runtimeclass 'Base'. The base class must be declared as 'unsealed runtimeclass'.
```

A runtimeclass is trying to derive from another runtimeclass that is not marked as `unsealed`.

**Fix:** Mark the base class as `unsealed runtimeclass`:

```idl
unsealed runtimeclass Base { }
runtimeclass Derived : Base { }
```

---

### MIDL1038

**Runtimeclass in requires clause**

```
error MIDL1038: 'MyClass' is a runtimeclass. Only interfaces are allowed in 'requires' clauses.
```

An interface's `requires` clause references a runtimeclass instead of an interface. The `requires` clause is for specifying interface dependencies only.

**Fix:** Replace the runtimeclass with the interface that the runtimeclass implements, or remove it from the `requires` clause.

---

### MIDL1039

**Constructor in interface**

```
error MIDL1039: Constructors are not allowed in interfaces. Only runtimeclasses can have constructors.
```

An interface declaration contains a constructor. WinRT interfaces cannot have constructors — only `runtimeclass` types can declare constructors.

**Fix:** Move the constructor to a `runtimeclass`, or remove it:

```idl
// Wrong:
interface IFoo { IFoo(); }

// Right:
runtimeclass Foo { Foo(); }
```

---

### MIDL1040

**Static member in interface**

```
error MIDL1040: Static members are not allowed in interfaces.
```

An interface contains a member declared as `static`. WinRT interfaces cannot have static members — static members belong on `runtimeclass` types.

**Fix:** Move the static member to a `runtimeclass`, or remove the `static` keyword:

```idl
// Wrong:
interface IFoo { static void Reset(); }

// Right:
runtimeclass Foo { static void Reset(); }
```

---

### MIDL1041

**Property or event in struct**

```
error MIDL1041: Properties are not allowed in structs. Structs can only contain fields.
error MIDL1041: Events are not allowed in structs.
```

A struct declaration contains a property or event. WinRT structs can only contain fields (plain data members). Properties and events belong on `interface` or `runtimeclass` types.

**Fix:** Use a plain field instead of a property, or move to a `runtimeclass`:

```idl
// Wrong:
struct S { String Name { get; }; }

// Right:
struct S { String Name; }
```

---

### MIDL1042

**Event with non-delegate type**

```
error MIDL1042: Event type 'Int32' is not a delegate type.
```

An event is declared with a type that is not a delegate. WinRT events must use a delegate type (e.g., `EventHandler`, `TypedEventHandler<TSender, TArgs>`, or a custom delegate).

**Fix:** Declare a delegate type and use it for the event:

```idl
// Wrong:
event Int32 Changed;

// Right:
delegate void ChangedHandler(Object sender);
event ChangedHandler Changed;
```

---

### MIDL1043

**Void parameter type**

```
error MIDL1043: 'void' is not valid as a parameter type.
```

A method parameter is declared with type `void`. The `void` keyword is only valid as a return type (meaning the method returns nothing), not as a parameter type.

**Fix:** Use an actual type for the parameter:

```idl
// Wrong:
void M(void x);

// Right:
void M(Int32 x);
```

---

### MIDL1044

**Duplicate method name without \[overload\]**

```
warning MIDL1044: Methods 'Do' have same name but different signatures. Use [overload("name")] and [method_name("name")] to disambiguate.
```

A runtimeclass has multiple methods with the same name but no `[overload]` attribute. WinRT requires overloaded methods to have unique ABI names, which are specified via `[overload]` and `[method_name]` attributes.

**Fix:** Add `[overload]` and `[method_name]` attributes:

```idl
[overload("DoWithInt")]
[method_name("DoWithInt")]
void Do(Int32 x);

[overload("DoWithString")]
[method_name("DoWithString")]
void Do(String x);
```

---

### MIDL1045

**Circular struct reference**

```
error MIDL1045: Circular reference between struct 'A' and struct 'B'.
```

Two or more structs form a circular reference through their fields. WinRT structs are value types and cannot contain circular references (this would require infinite storage).

**Fix:** Break the cycle by using a `runtimeclass` (reference type) for one of the types, or restructure the data:

```idl
// Wrong:
struct A { B b; }
struct B { A a; }

// Right:
struct A { Int32 bId; }
struct B { Int32 aId; }
```

---

### MIDL1046

**Duplicate enum member**

```
error MIDL1046: Duplicate enum member 'A'.
```

An enum contains two members with the same name.

**Fix:** Remove the duplicate or rename one of the members:

```idl
enum E { A, B, C }
```

---

### MIDL1047

**Import file not found**

```
error MIDL1047: Cannot find imported file 'missing.idl'.
```

An `import` directive references a file that cannot be found relative to the current file or any include path (`/I`).

**Fix:** Check the file path spelling, ensure the file exists, or add the directory to include paths with `/I path`:

```idl
import "types.idl";  // Must exist relative to current file or /I paths
```

---

## Additional Semantic Errors (MIDL1100–MIDL1105)

### MIDL1100

**Static modifier on non-class**

```
error MIDL1100: The 'static' modifier is not valid on 'interface' declarations. Only runtimeclass supports modifiers.
```

The `static` modifier is applied to a type that is not a runtimeclass (e.g., an interface or struct).

**Fix:** Remove `static` from the declaration, or change it to a `runtimeclass`:

```idl
// Wrong:
static interface IWidget { }

// Right:
static runtimeclass Helpers { }
```

---

### MIDL1101

**Generic type requires arguments**

```
error MIDL1101: Generic type 'IVector' requires type arguments.
```

A generic WinRT type is used without providing the required type arguments.

**Fix:** Provide type arguments:

```idl
// Wrong:
IVector Items { get; };

// Right:
IVector<String> Items { get; };
```

---

### MIDL1102

**@ref target not found**

```
error MIDL1102: Referenced path 'missing/folder' not found. Check the file path in the @ref directive.
```

A `@ref:` directive references a path that does not exist.

**Fix:** Verify the file or folder path exists and is spelled correctly.

---

### MIDL1103

**Invalid feature stage**

```
error MIDL1103: feature_name 'Feature_MyFeature': 'Beta' is not a valid FeatureStage. Expected AlwaysDisabled, DisabledByDefault, EnabledByDefault, or AlwaysEnabled.
```

A `feature_name` declaration uses an unrecognized feature stage value.

**Fix:** Use one of the valid stages: `AlwaysDisabled`, `DisabledByDefault`, `EnabledByDefault`, or `AlwaysEnabled`.

---

### MIDL1104

**Non-experimental references experimental**

```
error MIDL1104: Type 'Widget' references experimental type 'ExperimentalHelper'. Mark 'Widget' as [experimental] or remove the reference.
```

A non-experimental type references a type marked as `[experimental]`.

**Fix:** Mark the consuming type as `[experimental]` too, or remove the reference:

```idl
[experimental]
runtimeclass Widget { ExperimentalHelper GetHelper(); }
```

---

### MIDL1105

**Cross-feature-gate reference**

```
warning MIDL1105: Type 'Widget' references feature-gated type 'GatedHelper' ([feature(Feature_X)]). Consider adding [feature(Feature_X)] to 'Widget'.
```

A type not behind a feature gate references a feature-gated type.

**Fix:** Add the same `[feature]` attribute to the consuming type:

```idl
[feature(Feature_X)]
runtimeclass Widget { GatedHelper GetHelper(); }
```

---

## Strict Mode Errors (MIDL2001–MIDL2005)

These errors are emitted in **strict MIDL3 mode** (the default). They indicate the use of classic MIDL constructs that are not part of MIDL3 syntax. Disable with `/no_strict` or `/compat`.

### MIDL2001

**Classic MIDL construct**

```
error MIDL2001: 'coclass' is a classic MIDL construct and is not allowed in strict MIDL3 mode.
               Use /compat to enable classic syntax.
```

The following classic constructs trigger this warning:

| Construct | MIDL3 equivalent |
|-----------|-----------------|
| `coclass` | `runtimeclass` with `[activatable]` |
| `library` | `namespace` |
| `module` | `static runtimeclass` |
| `typedef` | (not needed in MIDL3) |
| `dispinterface` | `interface` |
| `cpp_quote` | (not needed in MIDL3) |
| `importlib` | `import` |
| `union` | (use structs or enums) |

**Fix:** Convert to MIDL3 syntax, or use `/compat` mode.

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

### MIDL2002

**HRESULT return type**

```
error MIDL2002: HRESULT return type is not used in MIDL3. Methods should return the actual type directly.
```

In classic MIDL, methods return `HRESULT` and use `[out, retval]` parameters for the actual return value. In MIDL3, methods return the value directly.

**Fix:**

```idl
// Classic MIDL (MIDL2):
HRESULT GetName([out, retval] HSTRING* name);

// Modern MIDL3:
String GetName();
```

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

### MIDL2003

**Classic MIDL attribute**

```
error MIDL2003: '[in]' is a classic MIDL attribute. In MIDL3, use the equivalent modern syntax.
```

Classic `[in]` and `[out]` parameter attributes are not needed in MIDL3. Parameters are `in` by default; use the `out` keyword for output parameters.

**Fix:**

```idl
// Classic: void M([in] Int32 x, [out] Int32* y);
// MIDL3:  void M(Int32 x, out Int32 y);
```

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

### MIDL2004

**Pointer type**

```
error MIDL2004: Pointer types ('IFoo*') are not used in MIDL3. Reference types are passed directly.
```

MIDL3 does not use pointer syntax (`*`). Reference types (interfaces, runtime classes) are passed directly by name.

**Fix:**

```idl
// Classic: void M(IFoo* pFoo);
// MIDL3:  void M(IFoo foo);
```

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

### MIDL2005

**Classic MIDL type name** ⚡ *Quick fix available*

```
error MIDL2005: Lowercase 'int' is MIDL2 syntax. Use 'Int32' (capitalized) for MIDL3.
```

MIDL3 uses capitalized type names that match WinRT naming conventions. The following replacements are available as **one-click quick fixes** in VS Code:

| Classic type | MIDL3 type | Notes |
|-------------|-----------|-------|
| `int` | `Int32` | |
| `long` | `Int32` | Note: `long` maps to 32-bit in MIDL |
| `short` | `Int16` | |
| `byte` | `UInt8` | |
| `small` | `UInt8` | |
| `hyper` | `Int64` | |
| `float` | `Single` | |
| `double` | `Double` | |
| `boolean` | `Boolean` | Lowercase `boolean` is MIDL2 |
| `char` | `Char16` | |
| `wchar_t` | `Char16` | |
| `BSTR` | `String` | |
| `LPSTR` | `String` | |
| `LPWSTR` | `String` | |
| `VARIANT` | `Object` | |
| `unsigned` | `UInt8`/`UInt16`/`UInt32`/`UInt64` | Use specific size |
| `signed` | `Int16`/`Int32`/`Int64` | Use specific size |
| `handle_t` | *(not available)* | Not used in MIDL3 |
| `error_status_t` | *(not available)* | Not used in MIDL3 |

**Fix:** Click the 💡 lightbulb in VS Code to auto-replace, or manually change the type name.

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

### MIDL2006

**#include in strict mode**

```
warning MIDL2006: #include pastes file contents into the compilation unit. Consider using 'import "file.idl";' to reference types without inlining.
```

In strict MIDL3 mode, `#include` is discouraged because it inlines the entire file contents.

**Fix:** Use `import` instead of `#include`:

```idl
// Not recommended:
#include "types.idl"

// Preferred:
import "types.idl";
```

📖 [MIDL3 documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

## Code Quality Hints (MIDL3001–MIDL3016)

These Info-level hints are shown in the LSP/editor only (not in CLI by default). Toggle with the `midl.codeQualityHints` VS Code setting.

### MIDL3001 ⚡
**Interface naming convention** — Interfaces should start with `I` followed by PascalCase. `Foo` → `IFoo`, `Ibar` → `IBar`.

### MIDL3002
**Empty runtimeclass** — A runtimeclass has no members. Consider adding constructors, methods, properties, or events.

### MIDL3003
**Empty interface** — An interface has no members.

### MIDL3005 ⚡
**No constructor** — A runtimeclass has no constructor and no `[activatable]` attribute. It won't be default-activatable. Quick fix inserts `ClassName();`.

### MIDL3006
**Event handler convention** — Event uses a custom delegate. Consider using `TypedEventHandler<TSender, TArgs>` for new APIs.

### MIDL3007 ⚡
**Setter without getter** — A property has a setter but no getter. This is unusual in WinRT. Quick fix adds `get;`.

### MIDL3008
**Method name clashes with type** — A method shares its name with the containing type, which may cause confusion.

### MIDL3009 ⚡
**Namespace casing** — Namespace segments should use PascalCase. `myApp` → `MyApp`.

### MIDL3010 ⚡
**Attribute naming** — Custom attribute types should end with `Attribute`. `MyTag` → `MyTagAttribute`. Use `[attributename("MyTag")]` to keep the short name.

### MIDL3011
**Delegate naming** — Delegates should end with `Handler` or `Callback`.

### MIDL3012
**Event args naming** — Structs used as event arguments should end with `EventArgs` by convention.

### MIDL3013 ⚡
**Async method naming** — Methods returning `IAsyncOperation`/`IAsyncAction` should end with `Async`. `DoWork` → `DoWorkAsync`.

### MIDL3014 ⚡
**Type name PascalCase** — Type names should use PascalCase without underscores. `my_type` → `MyType`.

### MIDL3015 ⚡
**Member name PascalCase** — Method and property names should start with an uppercase letter. `doWork` → `DoWork`.

### MIDL3016 ⚡
**Property shorthand** — A property with both public `get` and `set` can use shorthand: `String Name;` instead of `String Name { get; set; };`. Quick fix removes the accessor block.

> ⚡ = Quick fix available in VS Code

---

## Suppressing Warnings

### CLI

```bash
# Suppress all warnings
midl /no_warn input.idl

# Disable strict mode (suppresses MIDL2xxx)
midl /no_strict input.idl
midl /compat input.idl

# Treat warnings as errors
midl /WX input.idl
```

### VS Code

- Set `midl.strictMode` to `false` to disable MIDL2xxx strict mode errors.
- Set `midl.codeQualityHints` to `false` to disable MIDL3xxx code quality hints.

---

## See Also

- [Introduction to MIDL 3.0](https://learn.microsoft.com/en-us/uwp/midl-3/intro)
- [Transition from classic MIDLRT to MIDL 3.0](https://learn.microsoft.com/en-us/uwp/midl-3/from-midlrt)
- [Reserved keywords](https://learn.microsoft.com/en-us/uwp/midl-3/reserved-keywords)
- [Troubleshooting MIDL 3.0](https://learn.microsoft.com/en-us/uwp/midl-3/troubleshooting)
