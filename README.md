# WinMIDL — Microsoft Interface Definition Language Compiler

A modern, fast, editor-friendly MIDL compiler. Ships as a single ~5.5 MB native executable — no .NET runtime required.

> **Status:** WinMIDL is an independent, actively developed MIDL3 compiler. It is not the `midl.exe` that ships in the Windows SDK, and is provided as-is (see [SUPPORT.md](SUPPORT.md)).

---

## Overview

WinMIDL is an independent implementation of a MIDL compiler. It compiles MIDL3 (and classic MIDL) source into Windows Metadata, C/C++ headers, IID files, and custom proxy/stub code for COM marshaling — all from a single self-contained binary.

### Outputs

| Artifact | Extension | Switch |
|----------|-----------|--------|
| Windows Metadata | `.winmd` | `/winmd` |
| C / C++ headers | `.h` | `/h` |
| IID definitions | `_i.c` | `/iid` |
| Custom proxy/stub | `_proxy.cpp` | `/cppproxy` |
| DllData | `dlldata.c` | (automatic) |
| Activation manifest | `.manifest` / `.appxmanifest` | `--manifest` |
| XML doc comments | `.xdc` | `/doc` (supports both XDC and Doxygen-style comments) |

The same binary also serves as an **LSP server** — editor extensions for VS Code and Visual Studio ship with it bundled.

---

## Quick Start

```bash
# Compile a MIDL file (header + IIDs)
Midl.Cli /winrt /h Widget.h /iid Widget_i.c Widget.idl

# Generate Windows Metadata
Midl.Cli /winmd Widget.winmd Widget.idl

# Generate proxy/stub code for COM marshaling
Midl.Cli /winrt /h Widget.h /iid Widget_i.c /cppproxy Widget_proxy.cpp Widget.idl

# Decompile a .winmd back to IDL
Midl.Cli --decompile Windows.Foundation.winmd

# Inspect WinMD metadata tables
Midl.Cli --dump-winmd Widget.winmd

# Syntax check only (no output)
Midl.Cli /Zs Widget.idl

# Analyze with diagnostics
Midl.Cli --analyze Widget.idl
```

---

## Editor Extensions

### VS Code

Install the **MIDL Language Support** extension from a `.vsix` package:

```bash
code --install-extension midl-language.vsix
```

### Visual Studio

Install the **WinMIDL** extension for Visual Studio 2022+:

```bash
VSIXInstaller.exe Midl.VsExtension.vsix
```

Both extensions bundle the compiler binary (x64 + ARM64) and provide:

| Feature | Status |
|---------|--------|
| Syntax highlighting | ✅ |
| Real-time diagnostics | ✅ |
| Completions (context-aware) | ✅ |
| Hover information | ✅ |
| Go-to-definition (incl. F12 into SDK `.winmd`) | ✅ |
| Signature help (methods + attributes) | ✅ |
| Semantic tokens | ✅ |
| Code folding | ✅ |
| Formatting | ✅ |
| Quick fixes | ✅ |
| Rename symbol | ✅ |
| Find references | ✅ |
| Inactive region graying (`#ifdef`) | ✅ |
| Namespace type count code lens | ✅ |
| Doc comment (`///`, `//!`, `/** */`) support + hover | ✅ |
| Interactive class diagram | ✅ |
| Microsoft Learn integration (hover docs) | ✅ |

---

## Command-Line Reference

```
Midl.Cli [switches] <input.idl>
```

### Key Switches

| Switch | Description |
|--------|-------------|
| `/winrt` | Enable Windows Runtime (MIDL3) mode |
| `/winmd <file>` | Emit Windows Metadata (`.winmd`; implies `/winrt`) |
| `/cppproxy <file>` | Emit custom proxy/stub C++ code |
| `/h <file>` | Emit C/C++ header |
| `/iid <file>` | Emit IID definitions (`_i.c`) |
| `/out <dir>` | Set output directory |
| `/reference <.winmd>` | Add a WinMD reference |
| `/Zs` | Syntax check only — no output files |
| `/W<n>` | Set warning level |
| `/WX` | Treat warnings as errors |
| `/D <name>=<val>` | Define a preprocessor macro |
| `/I <dir>` | Add an include search path |
| `/diag` | Show info-level diagnostics (code quality hints) |
| `/doc <file>` | Emit XML doc comments (`.xdc`) |
| `--manifest <file>` | Generate activation manifest (`.manifest` or `.appxmanifest`) |
| `--server_dll <name>` | Server DLL name for manifest generation |
| `--lsp` | Start as an LSP server (stdio) |
| `--analyze` | Analyze and report diagnostics |
| `--decompile <.winmd>` | Decompile a WinMD back to IDL |
| `--dump-winmd <.winmd>` | Dump WinMD metadata tables |
| `/?` | Show full help |
| `/nologo` | Suppress the banner |

---

## Generating Windows Metadata (WinMD)

Use the `/winmd` switch to emit a `.winmd` file from your IDL:

```bash
# Generate a .winmd file (implicitly enables /winrt mode)
Midl.Cli /winmd output.winmd input.idl

# Generate both header and WinMD
Midl.Cli /winrt /h output.h /winmd output.winmd input.idl
```

The emitter produces spec-compliant WinMD files with:

- **Full attribute emission** — `[contract]`, `[activatable]`, `[composable]`, `[static]`, `[deprecated]`, `[threading]`, `[marshaling_behavior]`, `[version]`, `[experimental]`, `[noexcept]`, `[remote_async]`, `[attributeusage]`, and user-defined attributes
- **Correct version encoding** — integer versions encoded as `(major << 16) | minor`, hex passthrough, `major.minor` notation
- **ApiContract types** — emitted as structs with `[ApiContract]` and `[ContractVersion]` attributes
- **Custom attribute types** — emitted with `[AttributeUsageAttribute]` using CLR `System.AttributeTargets` flags (matching MIDL.exe behavior)
- **Deterministic GUIDs** — synthesized interfaces get stable UUIDs derived from their ABI signature

### Inspecting WinMD Files

```bash
# Dump metadata tables (types, methods, attributes)
Midl.Cli --dump-winmd output.winmd

# Decompile back to IDL
Midl.Cli --decompile output.winmd
```

---

## Custom Proxy/Stub Code Generation

WinMIDL can generate custom C++ proxy/stub code for COM marshaling, replacing the traditional MIDL-generated NDR stubs with compiled, type-safe serialization.

```bash
# Generate header, IID file, and proxy/stub C++ code
Midl.Cli /winrt /h Widget.h /iid Widget_i.c /cppproxy Widget_proxy.cpp Widget.idl
```

### What's Generated

| File | Content |
|------|---------|
| `Widget.h` | C/C++ header with interface declarations |
| `Widget_i.c` | GUIDs (IIDs, CLSIDs) |
| `Widget_proxy.cpp` | Proxy and stub implementations for cross-apartment/cross-process marshaling |

### Building a Proxy/Stub DLL

The generated proxy code compiles into a standard COM proxy/stub DLL:

```bash
# Compile the proxy/stub DLL (MSVC)
cl /nologo /std:c++latest /EHsc /LD ^
    Widget_proxy.cpp Widget_i.c dlldata.c ^
    /I path/to/winmidl/include ^
    ole32.lib oleaut32.lib rpcrt4.lib ^
    /link /DEF:proxy.def /OUT:Widget_proxy.dll

# Register for in-process marshaling
regsvr32 Widget_proxy.dll
```

### Runtime Headers

The proxy code depends on `include/winmidl/proxy_helpers.h` which provides:

- Zero-copy `GetRuntimeClassName` using `WindowsCreateStringReference`
- `marshal_interface` / `unmarshal_interface` with typed IID + `IID_IUnknown` fallback
- HSTRING serialization helpers
- `EventRegistrationToken` marshaling
- Struct and array serialization

### Supported Marshaling Patterns

- **Primitives** — all WinRT value types, HSTRING, GUID, EventRegistrationToken
- **Structs** — nested structs with correct alignment
- **Interfaces** — with typed IID marshaling and `IID_IUnknown` fallback for system objects
- **Delegates** — typed callback proxies with event registration
- **Enums and flags**
- **Out parameters** — including interface out params with cleanup
- **Properties** — get/set with full round-trip
- **Events** — add/remove with `EventRegistrationToken`
- **Multiple interfaces** — QI, `GetIids`, inheritance chains

> See [docs/marshaling-architecture.md](docs/marshaling-architecture.md) and [docs/payload-format-spec.md](docs/payload-format-spec.md) for the full design.

---

## Project Configuration (`winmidl.json`)

For multi-file projects, create a `winmidl.json` in your project root:

```jsonc
{
    "inputs": ["src/Widget.idl", "src/Helper.idl"],
    "references": ["lib/MyLibrary.winmd"],
    "defines": ["FEATURE_X"],
    "includePaths": ["include"],
    "noSdk": false
}
```

The LSP discovers this file automatically, enabling cross-file symbol resolution, autocomplete, and diagnostics. For lightweight per-file hints, use `// @ref: other.idl` at the top of your source.

> See [docs/winmidl-json.md](docs/winmidl-json.md) for the full specification.

---

## Supported MIDL3 Syntax

- **Namespaces** — nested and dotted
- **Runtime classes** — `static`, `partial`, `unsealed`, base types, interface `requires`
- **Interfaces** — including `requires` clauses
- **Delegates**
- **Structs** and **enums** (including `[flags]`)
- **API contracts** and **versioned blocks**
- **Events**, **properties**, and **constructors**
- **Generics** — parameterized interfaces and delegates
- **Modern built-in types** — `String`, `Boolean`, `Int32`, `IVector<T>`, etc.
- **Custom attributes** — user-defined attribute types with `[attributeusage]`

---

## Links

- [Diagnostics Reference](docs/diagnostics.md)
- [Marshaling Architecture](docs/marshaling-architecture.md)
- [Payload Format Spec](docs/payload-format-spec.md)
- [Project Configuration (winmidl.json)](docs/winmidl-json.md)
- [MIDL3 Documentation](https://learn.microsoft.com/en-us/uwp/midl-3/)

---

## Building from Source

```bash
dotnet build
dotnet test
```

> See [CONTRIBUTING.md](CONTRIBUTING.md) for prerequisites, project structure, and development workflow.
> See [docs/architecture.md](docs/architecture.md) for compiler internals and design.

---

## License

Licensed under the [MIT License](LICENSE).

---

## Trademarks

See [TRADEMARKS.md](TRADEMARKS.md).
<!-- Test change for a pull request build check; not for merge. -->
