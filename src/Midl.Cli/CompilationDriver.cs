// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using Midl.Analysis;
using Midl.Analysis.Diagnostics;
using Midl.Analysis.Symbols;
using Midl.CodeGen.Decompiler;
using Midl.CodeGen.DllData;
using Midl.CodeGen.Header;
using Midl.CodeGen.Manifest;
using Midl.CodeGen.Proxy;
using Midl.CodeGen.Stub;
using Midl.CodeGen.WinMd;
using Midl.Syntax;

using IidEmitter = Midl.CodeGen.Iid.IidEmitter;

namespace Midl.Cli;

/// <summary>
/// Orchestrates the full MIDL compilation pipeline.
/// </summary>
public class CompilationDriver
{
    public static string Version { get; } =
        typeof(CompilationDriver).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CompilationDriver).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
    public static string Banner { get; } = $"Microsoft (R) MIDL Compiler Version {Version} (.NET 10 Rewrite)\nCopyright (c) Microsoft Corporation. All rights reserved.";

    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public CompilationDriver() : this(Console.Out, Console.Error) { }

    public CompilationDriver(TextWriter stdOut, TextWriter stdErr)
    {
        _out = stdOut;
        _err = stdErr;
    }

    public int Run(CommandLineOptions options)
    {
        // Initialize tracing
        MidlTrace.InitFromEnvironment();
        if (options.DebugTrace)
            MidlTrace.Enable(null); // stderr

        MidlTrace.Cli($"Input: {options.InputFile}");
        MidlTrace.Cli($"Mode: {options.Mode}, WinRT: {options.WinRtMode}");

        // /winmd implies /winrt
        if (options.WinMdFile != null && !options.WinRtMode)
        {
            options.WinRtMode = true;
            MidlTrace.Cli("/winmd specified — auto-enabling /winrt mode");
        }

        // Auto-discover Windows SDK (unless /no_def_idir)
        if (!options.NoDefIdir && OperatingSystem.IsWindows())
        {
            var sdk = WindowsSdkLocator.Discover();
            if (sdk != null)
            {
                MidlTrace.Cli($"Found Windows SDK {sdk.Version} at {sdk.Root}");

                // Add SDK include paths (append so user /I paths take priority)
                foreach (var path in sdk.IncludePaths)
                {
                    if (!options.IncludePaths.Contains(path))
                        options.IncludePaths.Add(path);
                }

                // Add Windows.winmd reference
                bool hasWindowsWinMd = false;
                foreach (var r in options.References) { if (Path.GetFileName(r).Equals("Windows.winmd", StringComparison.OrdinalIgnoreCase)) { hasWindowsWinMd = true; break; } }
                if (sdk.WindowsWinMdPath != null && !hasWindowsWinMd)
                {
                    options.References.Add(sdk.WindowsWinMdPath);
                    MidlTrace.Cli($"Auto-referenced {sdk.WindowsWinMdPath}");
                }
            }
        }

        // Banner (suppress for decompile/dump modes — output goes to stdout)
        if (!options.NoLogo && !options.DecompileMode && !options.DumpWinMd)
            _out.WriteLine(Banner);

        // Help
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        // Version
        if (options.ShowVersion)
        {
            _out.WriteLine(Version);
            return 0;
        }

        // Parse errors
        if (options.Errors.Count > 0)
        {
            foreach (var err in options.Errors)
                _err.WriteLine($"error: {err}");
            return 2;
        }

        // Unknown switch warnings
        foreach (var sw in options.UnknownSwitches)
            _err.WriteLine($"warning MIDL2000: unknown switch '{sw}'");

        // Confirm mode — print effective options and exit
        if (options.Confirm)
        {
            PrintConfirm(options);
            return 0;
        }

        // Validate input
        if (string.IsNullOrEmpty(options.InputFile))
        {
            _err.WriteLine("error MIDL1000: no input file specified");
            return 2;
        }

        if (!File.Exists(options.InputFile))
        {
            _err.WriteLine($"error MIDL1001: cannot open input file '{options.InputFile}'");
            return 2;
        }

        // Dump WinMD mode — canonical metadata dump
        if (options.DumpWinMd)
        {
            try
            {
                var dump = WinMdDumper.Dump(options.InputFile);
                _out.Write(dump);
                return 0;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"error MIDL0001: WinMD dump failed: {ex.Message}");
                return 2;
            }
        }

        // Decompile mode — WinMD to IDL
        if (options.DecompileMode)
        {
            try
            {
                var decompiler = new WinMdDecompiler();
                var idl = decompiler.Decompile(options.InputFile, options.DecompileNamespace);
                _out.Write(idl);
                return 0;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"error MIDL0001: decompilation failed: {ex.Message}");
                return 2;
            }
        }

        // Manifest mode — generate activation manifest from .winmd
        if (options.ManifestFile != null)
        {
            if (string.IsNullOrEmpty(options.ServerDll))
            {
                _err.WriteLine("error MIDL0001: --server_dll is required when using --manifest");
                return 2;
            }

            if (string.IsNullOrEmpty(options.InputFile))
            {
                _err.WriteLine("error MIDL1000: no input .winmd file specified");
                return 2;
            }

            if (!options.InputFile.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase))
            {
                _err.WriteLine($"error MIDL0001: --manifest requires .winmd input, got '{options.InputFile}'");
                return 2;
            }

            if (!File.Exists(options.InputFile))
            {
                _err.WriteLine($"error MIDL1001: cannot open input file '{options.InputFile}'");
                return 2;
            }

            try
            {
                var classes = ManifestEmitter.ExtractFromWinMd(options.InputFile);
                var emitter = new ManifestEmitter();
                string content;

                if (options.ManifestFile.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase))
                    content = emitter.EmitAppx(options.ServerDll, classes);
                else
                    content = emitter.EmitSxs(options.ServerDll, classes);

                File.WriteAllText(options.ManifestFile, content);
                _out.WriteLine($"Manifest written to {options.ManifestFile}");
                return 0;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"error MIDL0001: manifest generation failed: {ex.Message}");
                return 2;
            }
        }

        // Read input
        var source = File.ReadAllText(options.InputFile);

        return RunFromSource(source, options);
    }

    /// <summary>
    /// Compile from source text directly (used for testing without file I/O).
    /// </summary>
    public int RunFromSource(string source, CommandLineOptions options)
    {
        // Lex → Parse → Bind
        Compilation compilation;
        CompilationProfile? profile = options.Profile ? new CompilationProfile() : null;
        Stopwatch? totalSw = options.Profile ? Stopwatch.StartNew() : null;
        try
        {
            compilation = CompilationHelper.Compile(
                source,
                options.References.Count > 0 ? options.References : null,
                options.Mode,
                options.IncludePaths.Count > 0 ? options.IncludePaths : null,
                options.InputFile,
                options.Defines.Count > 0 ? options.Defines : null,
                emitQualityHints: options.ShowInfoDiagnostics,
                profile: profile,
                arraysEnabled: options.ArraysEnabled);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"error MIDL1002: compilation failed: {ex.Message}");
            return 2;
        }

        // Analyze mode — rich console output with source context
        if (options.AnalyzeMode)
        {
            return RunAnalyzeMode(source, compilation, options);
        }

        // Report diagnostics (standard midl-compatible format)
        int errorCount = 0;
        int warningCount = 0;
        var lineStarts = BuildLineStarts(source);

        foreach (var diag in compilation.Diagnostics)
        {
            var prefix = diag.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info"
            };

            if (diag.Severity == DiagnosticSeverity.Warning && options.NoWarnings)
                continue;

            // Build location prefix: "file(line,col): ".
            // SourceFile is only set when the source was rewritten (preprocessor SourceMap,
            // or the comment-stripping remap), so a file with no comments and no directives
            // arrives with it null. That says nothing about where the diagnostic is — the
            // input path is known either way, so fall back to it.
            string locationPrefix = "";
            var locationFile = diag.SourceFile ?? options.InputFile;
            if (locationFile != null)
            {
                var pos = diag.OriginalPosition ?? diag.Position;
                // Read the original file to compute line/col
                try
                {
                    var fileText = File.ReadAllText(locationFile);
                    var fileLineStarts = BuildLineStarts(fileText);
                    var (line, col) = OffsetToLineCol(fileLineStarts, pos);
                    locationPrefix = $"{locationFile}({line + 1},{col + 1}): ";
                }
                catch
                {
                    locationPrefix = $"{locationFile}: ";
                }
            }

            if (diag.Severity == DiagnosticSeverity.Warning && options.WarningsAsErrors)
            {
                _err.WriteLine($"{locationPrefix}error {diag.Code}: {diag.Message}");
                errorCount++;
            }
            else
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    _err.WriteLine($"{locationPrefix}{prefix} {diag.Code}: {diag.Message}");
                    errorCount++;
                }
                else if (diag.Severity == DiagnosticSeverity.Warning)
                {
                    _err.WriteLine($"{locationPrefix}{prefix} {diag.Code}: {diag.Message}");
                    warningCount++;
                }
                else if (diag.Severity == DiagnosticSeverity.Info && options.ShowInfoDiagnostics)
                {
                    _err.WriteLine($"{locationPrefix}{prefix} {diag.Code}: {diag.Message}");
                }
            }
        }

        // Syntax check only
        if (options.SyntaxCheckOnly)
        {
            PrintSummary(errorCount, warningCount);
            PrintProfile(profile, totalSw);
            return errorCount > 0 ? 2 : 0;
        }

        if (errorCount > 0)
        {
            PrintSummary(errorCount, warningCount);
            PrintProfile(profile, totalSw);
            return 2;
        }

        // Generate outputs
        var outDir = options.OutputDirectory ?? ".";
        MidlTrace.Cli($"Output dir: {outDir}");
        var baseName = options.InputFile != null
            ? Path.GetFileNameWithoutExtension(options.InputFile)
            : "output";
        var filesWritten = new List<string>();

        if (!options.NoHeader)
        {
            var headerEmitter = new HeaderEmitter();
            var headerContent = headerEmitter.Emit(compilation);
            var headerPath = options.HeaderFile ?? $"{baseName}.h";
            {
                var path = Path.Combine(outDir, headerPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                File.WriteAllText(path, headerContent);
                filesWritten.Add(path);
            }
        }

        if (!options.NoIid && !options.NoMidl)
        {
            var iidEmitter = new IidEmitter();
            var iidContent = iidEmitter.Emit(compilation);
            var iidPath = options.IidFile ?? $"{baseName}_i.c";
            {
                var path = Path.Combine(outDir, iidPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                File.WriteAllText(path, iidContent);
                filesWritten.Add(path);
            }
        }

        if (options.WinRtMode)
        {
            var winmdEmitter = new WinMdEmitter(options.Deterministic, options.EnableTrueAsync);

            if (options.WinMdFile != null)
            {
                // Explicit filename: emit single file, warn on namespace mismatch
                var winmdPath = options.WinMdFile;
                var path = Path.Combine(outDir, winmdPath);
                var winmdBaseName = Path.GetFileNameWithoutExtension(winmdPath);

                // Check for namespace mismatch
                var typeNamespaces = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in compilation.GetAllTypes())
                {
                    var fn = t.FullName;
                    var d = fn.LastIndexOf('.');
                    if (d >= 0)
                    {
                        var ns = fn[..d];
                        if (ns.Length > 0) typeNamespaces.Add(ns);
                    }
                }
                var commonNs = WinMdEmitter.GetCommonNamespacePrefix(typeNamespaces);

                if (!string.IsNullOrEmpty(commonNs) &&
                    !string.Equals(winmdBaseName, commonNs, StringComparison.OrdinalIgnoreCase))
                {
                    _err.WriteLine($"warning MIDL1071: WinMD file is named '{winmdBaseName}' but root namespace is '{commonNs}'. WinMD file name should match the root namespace.");
                    warningCount++;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                winmdEmitter.Emit(compilation, path);
                filesWritten.Add(path);
            }
            else
            {
                // Auto-derive filename(s) from root namespace(s)
                var perNs = winmdEmitter.EmitPerNamespace(compilation);
                foreach (var (ns, bytes) in perNs)
                {
                    var winmdFileName = $"{ns}.winmd";
                    var path = Path.Combine(outDir, winmdFileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                    File.WriteAllBytes(path, bytes);
                    filesWritten.Add(path);
                }
            }
        }

        // /nomidl (midl.exe): write only the .winmd and the header, even when /iid, /proxy or
        // /dlldata name files. The OS build passes all of them for every WinRT IDL directory.
        if (!options.NoDllData && !options.NoMidl)
        {
            var dllDataEmitter = new DllDataEmitter();
            var dllDataContent = dllDataEmitter.Emit(compilation);
            if (options.DllDataFile != null)
            {
                var path = Path.Combine(outDir, options.DllDataFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                File.WriteAllText(path, dllDataContent);
            }
        }

        if (!options.NoProxy && !options.NoMidl)
        {
            var stubEmitter = new StubEmitter();
            var stubContent = stubEmitter.Emit(compilation);
            if (options.ProxyFile != null)
            {
                var path = Path.Combine(outDir, options.ProxyFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
                File.WriteAllText(path, stubContent);
            }
        }

        if (options.CppProxyFile != null)
        {
            var headerFile = options.HeaderFile ?? Path.GetFileNameWithoutExtension(options.InputFile ?? "output") + ".h";
            var proxyContent = GenerateCombinedProxy(compilation, headerFile, options.UseExceptions);
            var path = Path.Combine(outDir, options.CppProxyFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
            File.WriteAllText(path, proxyContent);
            filesWritten.Add(path);

            // Copy runtime helper headers alongside the generated proxy
            CopyRuntimeHeaders(outDir);
        }

        // XDC documentation file
        if (options.DocFile != null)
        {
            var xdcEmitter = new Midl.CodeGen.Xdc.XdcEmitter();
            var assemblyName = options.InputFile != null
                ? Path.GetFileNameWithoutExtension(options.InputFile)
                : "output";
            var xdcContent = xdcEmitter.Emit(compilation, assemblyName);
            var path = Path.Combine(outDir, options.DocFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outDir);
            File.WriteAllText(path, xdcContent);
            filesWritten.Add(path);
        }

        // Print what was generated
        if (filesWritten.Count > 0 && !options.NoLogo)
        {
            foreach (var f in filesWritten)
                _out.WriteLine($"  -> {f}");
        }

        PrintSummary(errorCount, warningCount);
        PrintProfile(profile, totalSw);
        return warningCount > 0 ? 1 : 0;
    }

    /// <summary>
    /// Compile source and return generated header content (for testing without disk I/O).
    /// </summary>
    public static string? CompileToHeader(string source)
    {
        var compilation = CompilationHelper.Compile(source);
        bool hasErrors = false;
        foreach (var d in compilation.Diagnostics) { if (d.Severity == DiagnosticSeverity.Error) { hasErrors = true; break; } }
        if (hasErrors)
            return null;
        var emitter = new HeaderEmitter();
        return emitter.Emit(compilation);
    }

    /// <summary>
    /// Generates a combined proxy/stub DLL source file containing proxy classes,
    /// stub classes, factory, and DLL export plumbing — ready to compile into a proxy DLL.
    /// </summary>
    internal static string GenerateCombinedProxy(Compilation compilation, string headerFile, bool useExceptions = false)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("// Combined proxy/stub DLL source auto-generated by MIDL compiler");
        sb.AppendLine();

        // Windows headers must come before the generated header (which uses FLOAT, IID, etc.)
        sb.AppendLine("#include <windows.h>");
        sb.AppendLine("#include <objbase.h>");
        sb.AppendLine("#include <objidl.h>");
        sb.AppendLine("#include <winstring.h>");
        sb.AppendLine("#include <inspectable.h>");
        sb.AppendLine();
        sb.AppendLine($"#include \"{Path.GetFileName(headerFile)}\"");
        sb.AppendLine();

        // Emit inline constexpr IID definitions (no _i.c needed)
        EmitInlineConstexprIIDs(sb, compilation);
        sb.AppendLine();

        // Proxy classes (IRpcProxyBuffer implementations)
        var proxyGen = new ProxyGenerator();
        sb.Append(proxyGen.Generate(compilation, useExceptions));
        sb.AppendLine();

        // Stub classes (IRpcStubBuffer implementations)
        var stubGen = new StubGenerator();
        sb.Append(stubGen.Generate(compilation, useExceptions));
        sb.AppendLine();

        // Factory (IPSFactoryBuffer) + DLL exports
        var factoryGen = new FactoryGenerator();
        sb.Append(factoryGen.Generate(compilation));

        return sb.ToString();
    }

    private static void EmitInlineConstexprIIDs(System.Text.StringBuilder sb, Compilation compilation)
    {
        sb.AppendLine("// Inline constexpr IID definitions — no _i.c needed");
        foreach (var symbol in compilation.GetAllTypes().Where(t => !t.IsExternal))
        {
            Guid guid;
            string mangledName;
            if (symbol is InterfaceSymbol iface)
            {
                guid = AbiGuidComputer.ComputeGuid(iface);
                mangledName = HeaderEmitter.MangleName(iface.FullName);
            }
            else if (symbol is DelegateSymbol d)
            {
                guid = AbiGuidComputer.ComputeGuid(d);
                mangledName = HeaderEmitter.MangleName(d.FullName);
            }
            else continue;

            var bytes = guid.ToByteArray();
            uint a = BitConverter.ToUInt32(bytes, 0);
            ushort b = BitConverter.ToUInt16(bytes, 4);
            ushort c = BitConverter.ToUInt16(bytes, 6);
            sb.AppendLine($"// {{{guid.ToString("D").ToUpperInvariant()}}}");
            sb.Append($"inline constexpr IID IID_{mangledName} = {{0x{a:X8}, 0x{b:X4}, 0x{c:X4}, {{");
            sb.Append($"0x{bytes[8]:X2}, 0x{bytes[9]:X2}, 0x{bytes[10]:X2}, 0x{bytes[11]:X2}, ");
            sb.Append($"0x{bytes[12]:X2}, 0x{bytes[13]:X2}, 0x{bytes[14]:X2}, 0x{bytes[15]:X2}");
            sb.AppendLine("}};");
        }
    }

    private void PrintSummary(int errors, int warnings)
    {
        _out.WriteLine($"MIDL: {errors} error(s), {warnings} warning(s)");
    }

    private void PrintProfile(CompilationProfile? profile, Stopwatch? totalSw)
    {
        if (profile == null || totalSw == null) return;
        totalSw.Stop();
        profile.Total = totalSw.Elapsed;
        profile.Print(_out);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    private static (int line, int col) OffsetToLineCol(int[] lineStarts, int offset)
    {
        int line = Array.BinarySearch(lineStarts, offset);
        if (line < 0) line = ~line - 1;
        if (line < 0) line = 0;
        int col = offset - lineStarts[line];
        return (line, col);
    }

    /// <summary>
    /// Rich analyze mode: Rust/C#-style diagnostic output with source context and squiggly underlines.
    /// Usage: midl --analyze input.idl
    /// </summary>
    private int RunAnalyzeMode(string source, Compilation compilation, CommandLineOptions options)
    {
        var lineStarts = BuildLineStarts(source);
        var lines = source.Split('\n');
        int errorCount = 0;
        int warningCount = 0;

        if (compilation.Diagnostics.Count == 0)
        {
            _out.WriteLine("  \u2713 No diagnostics. File is valid MIDL3.");
            PrintSymbolSummary(compilation);
            return 0;
        }

        foreach (var diag in compilation.Diagnostics)
        {
            if (diag.Severity == DiagnosticSeverity.Warning && options.NoWarnings)
                continue;

            var (line, col) = OffsetToLineCol(lineStarts, diag.Position);
            var isError = diag.Severity == DiagnosticSeverity.Error ||
                         (diag.Severity == DiagnosticSeverity.Warning && options.WarningsAsErrors);

            if (isError) errorCount++; else warningCount++;

            // Header
            var label = isError ? "error" : "warning";
            _err.WriteLine($"{label}[{diag.Code}]: {diag.Message}");

            // Location — use SourceFile if available
            var fileName = diag.SourceFile ?? options.InputFile ?? "<stdin>";
            if (diag.SourceFile != null && diag.OriginalPosition.HasValue)
            {
                try
                {
                    var fileText = File.ReadAllText(diag.SourceFile);
                    var fileLineStarts = BuildLineStarts(fileText);
                    (line, col) = OffsetToLineCol(fileLineStarts, diag.OriginalPosition.Value);
                }
                catch { }
            }
            _err.WriteLine($"  --> {fileName}:{line + 1}:{col + 1}");

            // Source snippet
            if (line >= 0 && line < lines.Length)
            {
                var lineNum = (line + 1).ToString();
                var pad = new string(' ', lineNum.Length);
                var sourceLine = lines[line].TrimEnd('\r');

                _err.WriteLine($"  {pad} |");
                _err.WriteLine($"  {lineNum} | {sourceLine}");

                // Squiggly underline
                var spanLen = Math.Max(1, Math.Min(diag.Length, Math.Max(0, sourceLine.Length - col)));
                if (col >= 0 && col <= sourceLine.Length)
                {
                    var marker = new string(' ', col) + new string(isError ? '^' : '~', spanLen);
                    _err.WriteLine($"  {pad} | {marker}");
                }

                // Hint for fixable diagnostics
                if (diag.Data != null && diag.Data.TryGetValue("replacement", out var replacement))
                {
                    _err.WriteLine($"  {pad} = help: replace with `{replacement}`");
                }

                if (diag.HelpUri != null)
                {
                    _err.WriteLine($"  {pad} = see: {diag.HelpUri}");
                }
            }

            _err.WriteLine();
        }

        // Summary
        _err.Write("  ");
        if (errorCount > 0) _err.Write($"{errorCount} error(s)");
        if (errorCount > 0 && warningCount > 0) _err.Write(", ");
        if (warningCount > 0) _err.Write($"{warningCount} warning(s)");
        _err.WriteLine(" emitted");

        if (errorCount == 0)
            PrintSymbolSummary(compilation);

        return errorCount > 0 ? 2 : (warningCount > 0 ? 1 : 0);
    }

    private void PrintSymbolSummary(Compilation compilation)
    {
        var types = new List<Midl.Analysis.Symbols.ISymbol>();
        foreach (var t in compilation.GetAllTypes()) types.Add(t);
        if (types.Count > 0)
        {
            _out.WriteLine();
            _out.WriteLine("  Symbols:");
            foreach (var t in types)
                _out.WriteLine($"    {t.Kind,-15} {t.FullName}");
        }
    }

    private void PrintConfirm(CommandLineOptions options)
    {
        _out.WriteLine("Effective options:");
        if (options.InputFile != null) _out.WriteLine($"  Input file  : {options.InputFile}");
        if (options.OutputDirectory != null) _out.WriteLine($"  Output dir  : {options.OutputDirectory}");
        if (options.HeaderFile != null) _out.WriteLine($"  Header file : {options.HeaderFile}");
        if (options.WinRtMode) _out.WriteLine("  WinRT mode  : yes");
        _out.WriteLine($"  MIDL3 mode  : {(options.Mode == Midl.Analysis.CompilationMode.Strict ? "strict" : "compat")}");
        if (options.SyntaxCheckOnly) _out.WriteLine("  Syntax only : yes");
        _out.WriteLine($"  Warning lvl : {options.WarningLevel}");
        if (options.Defines.Count > 0) _out.WriteLine($"  Defines     : {string.Join(", ", options.Defines)}");
        if (options.IncludePaths.Count > 0) _out.WriteLine($"  Includes    : {string.Join(", ", options.IncludePaths)}");
        if (options.References.Count > 0) _out.WriteLine($"  References  : {string.Join(", ", options.References)}");
    }

    private static string GetExeName()
    {
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "winmidl");
        // When invoked via 'dotnet tool exec' / 'dnx', the process is dotnet.exe
        // running our DLL directly. Distinguish from global tool shim (.exe).
        if (processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Global tool shim is an .exe; dnx runs the .dll via dotnet exec.
            // AppContext.BaseDirectory for a tool points to the package cache
            // containing .dll files, not a shim .exe directory.
            var baseDir = AppContext.BaseDirectory;
            var hasDll = File.Exists(Path.Combine(baseDir, "Midl.Cli.dll"));
            var hasExe = File.Exists(Path.Combine(baseDir, "Midl.Cli.exe"));
            if (hasDll && !hasExe)
                return "dnx WinMIDL --";
            return "winmidl";
        }
        return processName;
    }

    private void PrintHelp()
    {
        var exe = GetExeName();
        _out.WriteLine();
        _out.WriteLine($"Usage: {exe} [options] <input.idl>");
        _out.WriteLine();
        _out.WriteLine("Options:");
        _out.WriteLine("  /h, /header <file>     Specify header file name");
        _out.WriteLine("  /iid <file>            Specify IID file name");
        _out.WriteLine("  /proxy <file>          Specify proxy file name");
        _out.WriteLine("  /cppproxy <file>       Generate C++ proxy/stub/factory combined file");
        _out.WriteLine("  /dlldata <file>        Specify DLL data file name");
        _out.WriteLine("  /winmd <file>          Specify WinMD output file");
        _out.WriteLine("  /doc <file>            Generate XDC XML documentation file");
        _out.WriteLine("  /out <directory>       Specify output directory");
        _out.WriteLine("  /acf <file>            Specify ACF file");
        _out.WriteLine("  /winrt                 Enable WinRT mode");
        _out.WriteLine("  /Zs, /syntax_check     Syntax check only");
        _out.WriteLine("  /D <name>[=value]      Define preprocessor symbol");
        _out.WriteLine("  /I <path>              Add include path");
        _out.WriteLine("  /U <name>              Undefine preprocessor symbol");
        _out.WriteLine("  /reference <file>      Add WinMD reference");
#if WITH_COMPAT
        _out.WriteLine("  /cpp_cmd <command>     Specify C preprocessor command");
#endif
        _out.WriteLine("  /cpp_opt <options>     Specify C preprocessor options");
        _out.WriteLine("  /no_cpp                Do not run C preprocessor");
        _out.WriteLine("  /env <target>          Target environment (win32|win64|x64|arm64)");
        _out.WriteLine("  /target <system>       Target system (NT60..WIN11)");
        _out.WriteLine("  /W0../W6               Set warning level (default: 1)");
        _out.WriteLine("  /WX                    Treat warnings as errors");
        _out.WriteLine("  /no_warn               Suppress all warnings");
        _out.WriteLine("  /nologo                Suppress banner");
        _out.WriteLine("  /confirm               Print effective options and exit");
        _out.WriteLine("  /no_header             Do not generate header file");
        _out.WriteLine("  /no_iid                Do not generate IID file");
        _out.WriteLine("  /noproxy               Do not generate proxy file");
        _out.WriteLine("  /no_dlldata            Do not generate DLL data file");
        _out.WriteLine("  /metadata_dir <dir>    Specify metadata directory");
        _out.WriteLine("  /nomidl                Write only the .winmd and header (no _i.c, proxy or dlldata)");
        _out.WriteLine("  /deterministic         Reproducible .winmd: content-derived MVID and timestamp");
        _out.WriteLine("  /strict                Pure MIDL3 mode (default) — warn on classic constructs");
#if WITH_COMPAT
        _out.WriteLine("  /no_strict, /compat    Compatibility mode — accept classic MIDL syntax");
#endif
        _out.WriteLine("  /version               Print version");
        _out.WriteLine("  /?, /help              Show this help");
        _out.WriteLine("  --lsp                  Start Language Server Protocol mode");
        _out.WriteLine("  --analyze              Rich analysis output with source context");
        _out.WriteLine("  --decompile            Decompile WinMD to IDL");
        _out.WriteLine("  --dump-winmd           Dump WinMD metadata in canonical sorted format");
        _out.WriteLine("  --manifest <file>      Generate activation manifest from .winmd (SxS or AppX)");
        _out.WriteLine("  --server_dll <name>    DLL name for manifest generation");
        _out.WriteLine("  /debug                 Enable debug trace output (or set MIDL_DEBUG=1)");
        _out.WriteLine("  /diag                  Show info-level quality diagnostics");
        _out.WriteLine("  /arrays                Enable array parameter syntax (T[])");
        _out.WriteLine("  --profile              Print compilation phase timing breakdown");
        _out.WriteLine("  /no_def_idir           Don't add default SDK include directories");
        _out.WriteLine("  @<file>                Read options from response file");
    }

    /// <summary>
    /// Extract embedded runtime helper headers (proxy_helpers.h, wil_compat.h) to the output directory.
    /// These are needed by the generated proxy .cpp file.
    /// </summary>
    private static void CopyRuntimeHeaders(string outDir)
    {
        var targetDir = Path.Combine(outDir, "winmidl");
        Directory.CreateDirectory(targetDir);

        var assembly = typeof(CompilationDriver).Assembly;
        var headers = new[] { "proxy_helpers.h", "wil_compat.h" };

        foreach (var name in headers)
        {
            var resourceName = $"winmidl.{name}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                MidlTrace.Cli($"Warning: Embedded resource '{resourceName}' not found");
                continue;
            }

            var dst = Path.Combine(targetDir, name);
            using var fs = File.Create(dst);
            stream.CopyTo(fs);
        }
    }
}
