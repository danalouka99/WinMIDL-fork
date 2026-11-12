// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Midl.Analysis;
using Midl.Cli;

namespace Midl.Integration.Tests;

/// <summary>
/// Integration tests for CLI switches exercised through <see cref="CompilationDriver"/>.
/// Each test writes a small IDL to a temp directory, invokes the compiler, and
/// asserts the expected output files / diagnostics.
/// </summary>
public class CliSwitchTests : IDisposable
{
    private readonly string _tempDir;

    public CliSwitchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "midl_cli_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Minimal WinRT IDL that compiles without errors.
    private const string SimpleIdl = """
        namespace TestNs
        {
            runtimeclass Widget
            {
                Widget();
                String Name { get; };
            }
        }
        """;

    /// <summary>
    /// Write <paramref name="content"/> to a temp .idl file and return its path.
    /// </summary>
    private string WriteIdl(string content, string fileName = "input.idl")
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Run the compiler with the given args array, capturing stdout/stderr.
    /// Returns (exitCode, stdout, stderr).
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var options = CommandLineParser.Parse(args);
        var stdOut = new StringWriter();
        var stdErr = new StringWriter();
        var driver = new CompilationDriver(stdOut, stdErr);
        var exitCode = driver.Run(options);
        return (exitCode, stdOut.ToString(), stdErr.ToString());
    }

    // -----------------------------------------------------------------------
    // 1. /winmd output.winmd — generates a .winmd file
    // -----------------------------------------------------------------------
    [Fact]
    public void WinMd_GeneratesFile()
    {
        var idl = WriteIdl(SimpleIdl);
        var winmd = Path.Combine(_tempDir, "TestNs.winmd");

        var (exit, _, _) = Run("/nologo", "/winmd", winmd, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(winmd), "Expected .winmd file to be generated");
        var bytes = File.ReadAllBytes(winmd);
        Assert.True(bytes.Length > 0, "WinMD file should not be empty");
    }

    // -----------------------------------------------------------------------
    // 2. /h output.h — generates a C header file
    // -----------------------------------------------------------------------
    [Fact]
    public void Header_GeneratesFile_WithPragmaOnce()
    {
        var idl = WriteIdl(SimpleIdl);
        var header = Path.Combine(_tempDir, "output.h");

        var (exit, _, _) = Run("/nologo", "/h", header, "/no_iid", "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(header), "Expected .h file to be generated");
        var content = File.ReadAllText(header);
        Assert.Contains("#pragma once", content);
    }

    [Fact]
    public void HeaderLongForm_GeneratesFile()
    {
        var idl = WriteIdl(SimpleIdl);
        var header = Path.Combine(_tempDir, "output.h");

        var (exit, _, _) = Run("/nologo", "/header", header, "/no_iid", "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(header), "Expected .h file from /header switch");
        var content = File.ReadAllText(header);
        Assert.Contains("#pragma once", content);
    }

    // -----------------------------------------------------------------------
    // 3. /iid output_i.c — generates IID file
    // -----------------------------------------------------------------------
    [Fact]
    public void Iid_GeneratesFileWithConstants()
    {
        var idl = WriteIdl(SimpleIdl);
        var iidFile = Path.Combine(_tempDir, "output_i.c");

        var (exit, _, _) = Run("/nologo", "/iid", iidFile, "/no_header", "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(iidFile), "Expected IID file to be generated");
        var content = File.ReadAllText(iidFile);
        Assert.Contains("IID_", content);
    }

    // -----------------------------------------------------------------------
    // 4. /cppproxy output_proxy.cpp — generates proxy/stub code
    // -----------------------------------------------------------------------
    [Fact]
    public void CppProxy_GeneratesProxyStubCode()
    {
        var idl = WriteIdl(SimpleIdl);
        var proxyFile = Path.Combine(_tempDir, "output_proxy.cpp");

        var (exit, _, _) = Run("/nologo", "/cppproxy", proxyFile, "/no_header", "/no_iid", "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(proxyFile), "Expected proxy file to be generated");
        var content = File.ReadAllText(proxyFile);
        // Proxy/stub code references IRpcProxyBuffer or similar proxy plumbing
        Assert.True(
            content.Contains("Proxy") || content.Contains("proxy") || content.Contains("IRpcProxyBuffer"),
            "Proxy file should contain proxy-related code");
    }

    // -----------------------------------------------------------------------
    // 5. /decompile input.winmd — decompiles WinMD to IDL
    // -----------------------------------------------------------------------
    [Fact]
    public void Decompile_OutputsIdlWithNamespace()
    {
        // First generate a .winmd
        var idl = WriteIdl(SimpleIdl);
        var winmd = Path.Combine(_tempDir, "TestNs.winmd");

        var (exit1, _, _) = Run("/nologo", "/winmd", winmd, "/no_header", "/no_iid", "/no_def_idir", idl);
        Assert.Equal(0, exit1);
        Assert.True(File.Exists(winmd), "Pre-requisite: WinMD must exist");

        // Now decompile it
        var (exit2, stdout, _) = Run("--decompile", winmd);

        Assert.Equal(0, exit2);
        Assert.Contains("namespace", stdout);
    }

    // -----------------------------------------------------------------------
    // 6. /analyze input.idl — analyzes without generating output
    // -----------------------------------------------------------------------
    [Fact]
    public void Analyze_PrintsDiagnosticsForBadInput()
    {
        // IDL with a deliberate error (undefined type reference)
        var badIdl = """
            namespace TestNs
            {
                runtimeclass Widget
                {
                    UndefinedType BadProp { get; };
                }
            }
            """;
        var idl = WriteIdl(badIdl);

        var (exit, _, stderr) = Run("/nologo", "--analyze", "/no_def_idir", idl);

        // Analyze mode with errors returns non-zero exit code
        Assert.NotEqual(0, exit);
        // Stderr should contain diagnostic output
        Assert.False(string.IsNullOrWhiteSpace(stderr), "Analyze mode should produce diagnostic output");
    }

    // -----------------------------------------------------------------------
    // 7. /strict — strict mode rejects classic constructs (MIDL2001)
    // -----------------------------------------------------------------------
    [Fact]
    public void Strict_RejectsClassicCoclass()
    {
        var classicIdl = """
            [uuid(12345678-1234-1234-1234-123456789abc)]
            coclass MyCoclass
            {
                interface IUnknown;
            };
            """;
        var idl = WriteIdl(classicIdl);

        var (exit, _, stderr) = Run("/nologo", "/strict", "/no_def_idir", idl);

        Assert.NotEqual(0, exit);
        Assert.Contains("MIDL2001", stderr);
    }

    // -----------------------------------------------------------------------
    // 8. /no_strict — compat mode allows classic constructs
    // -----------------------------------------------------------------------
    [Fact]
    public void NoStrict_AllowsClassicCoclass()
    {
        var classicIdl = """
            [uuid(12345678-1234-1234-1234-123456789abc)]
            coclass MyCoclass
            {
                interface IUnknown;
            };
            """;
        var idl = WriteIdl(classicIdl);

        var (exit, _, stderr) = Run("/nologo", "/no_strict", "/no_def_idir", idl);

        // Should not have MIDL2001 error
        Assert.DoesNotContain("MIDL2001", stderr);
    }

    // -----------------------------------------------------------------------
    // 9. /winmd without /winrt — auto-enables WinRT mode
    // -----------------------------------------------------------------------
    [Fact]
    public void WinMd_AutoEnablesWinRtMode()
    {
        var idl = WriteIdl(SimpleIdl);
        var winmd = Path.Combine(_tempDir, "TestNs.winmd");

        // Deliberately omit /winrt — /winmd should auto-enable it
        var (exit, _, _) = Run("/nologo", "/winmd", winmd, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(winmd), "WinMD should be generated even without explicit /winrt");
    }

    // -----------------------------------------------------------------------
    // 10. /no_header — suppresses header generation
    // -----------------------------------------------------------------------
    [Fact]
    public void NoHeader_SuppressesHeaderFile()
    {
        var idl = WriteIdl(SimpleIdl);

        var (exit, _, _) = Run("/nologo", "/no_header", "/no_iid", "/out", _tempDir, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        // No .h file should exist in the output directory
        var headerFiles = Directory.GetFiles(_tempDir, "*.h");
        Assert.Empty(headerFiles);
    }

    // -----------------------------------------------------------------------
    // 11. /no_iid — suppresses IID file generation
    // -----------------------------------------------------------------------
    [Fact]
    public void NoIid_SuppressesIidFile()
    {
        var idl = WriteIdl(SimpleIdl);

        var (exit, _, _) = Run("/nologo", "/no_iid", "/no_header", "/out", _tempDir, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        // No _i.c file should exist
        var iidFiles = Directory.GetFiles(_tempDir, "*_i.c");
        Assert.Empty(iidFiles);
    }

    // -----------------------------------------------------------------------
    // 12. /zs — syntax check only, no output files
    // -----------------------------------------------------------------------
    [Fact]
    public void SyntaxCheck_Zs_NoOutputFiles()
    {
        var idl = WriteIdl(SimpleIdl);
        var outDir = Path.Combine(_tempDir, "zs_out");
        Directory.CreateDirectory(outDir);

        var (exit, _, _) = Run("/nologo", "/Zs", "/out", outDir, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        // No output files should be generated
        var files = Directory.GetFiles(outDir);
        Assert.Empty(files);
    }

    [Fact]
    public void SyntaxCheck_LongForm_NoOutputFiles()
    {
        var idl = WriteIdl(SimpleIdl);
        var outDir = Path.Combine(_tempDir, "sc_out");
        Directory.CreateDirectory(outDir);

        var (exit, _, _) = Run("/nologo", "/syntax_check", "/out", outDir, "/no_def_idir", idl);

        Assert.Equal(0, exit);
        var files = Directory.GetFiles(outDir);
        Assert.Empty(files);
    }

    // -----------------------------------------------------------------------
    // 13. /version — prints version string
    // -----------------------------------------------------------------------
    [Fact]
    public void Version_PrintsVersionString()
    {
        var (exit, stdout, _) = Run("/version");

        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "/version should print output");
        // Version string should contain digits and dots (e.g. "1.0.0")
        Assert.Matches(@"\d+\.\d+", stdout);
    }

    // -----------------------------------------------------------------------
    // 14. /help — prints help text containing "Usage"
    // -----------------------------------------------------------------------
    [Fact]
    public void Help_PrintsUsageText()
    {
        var (exit, stdout, _) = Run("/help");

        Assert.Equal(0, exit);
        Assert.Contains("Usage", stdout);
    }

    [Fact]
    public void Help_QuestionMark_PrintsUsageText()
    {
        var (exit, stdout, _) = Run("/?");

        Assert.Equal(0, exit);
        Assert.Contains("Usage", stdout);
    }

    // -----------------------------------------------------------------------
    // 15. /D MYDEFINE — preprocessor define
    // -----------------------------------------------------------------------
    [Fact]
    public void Define_PreprocessorDefineWorks()
    {
        var idlWithIfdef = """
            #ifdef MYDEFINE
            namespace TestNs
            {
                runtimeclass Widget
                {
                    Widget();
                    String Name { get; };
                }
            }
            #endif
            """;
        var idl = WriteIdl(idlWithIfdef);

        // With /D MYDEFINE the code is visible and should compile
        var (exit, _, _) = Run("/nologo", "/D", "MYDEFINE", "/Zs", "/no_def_idir", idl);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Define_WithoutDefine_CodeIsExcluded()
    {
        var idlWithIfdef = """
            #ifdef MYDEFINE
            namespace TestNs
            {
                runtimeclass Widget
                {
                    Widget();
                    String Name { get; };
                }
            }
            #endif
            """;
        var idl = WriteIdl(idlWithIfdef);

        // Without /D MYDEFINE the namespace block is excluded — the file is effectively empty
        var (exit, _, _) = Run("/nologo", "/Zs", "/no_def_idir", idl);
        Assert.Equal(0, exit);
    }

    // -----------------------------------------------------------------------
    // 16. /I path — include path resolution
    // -----------------------------------------------------------------------
    [Fact]
    public void IncludePath_ResolvesIncludes()
    {
        // Create an included file in a subdirectory
        var includeDir = Path.Combine(_tempDir, "inc");
        Directory.CreateDirectory(includeDir);
        var includedContent = """
            namespace IncNs
            {
                runtimeclass IncWidget
                {
                    IncWidget();
                    String Label { get; };
                }
            }
            """;
        File.WriteAllText(Path.Combine(includeDir, "included.idl"), includedContent);

        // Use import syntax which is the recommended way to include files
        var mainIdl = """
            import "included.idl";
            """;
        var idl = WriteIdl(mainIdl);

        var (exit, _, _) = Run("/nologo", "/Zs", "/I", includeDir, "/no_def_idir", idl);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void IncludePath_FailsWithoutCorrectPath()
    {
        var mainIdl = """
            #include "nonexistent.idl"
            """;
        var idl = WriteIdl(mainIdl);

        var (exit, _, stderr) = Run("/nologo", "/Zs", "/no_def_idir", idl);

        // Should fail because the include can't be resolved
        Assert.NotEqual(0, exit);
    }

    // -----------------------------------------------------------------------
    // /nomidl — only the .winmd and the header, as midl.exe does
    // -----------------------------------------------------------------------
    [Fact]
    public void NoMidl_WritesOnlyWinMdAndHeader_EvenWithIidProxyAndDllDataPaths()
    {
        var idl = WriteIdl(SimpleIdl);
        string P(string name) => Path.Combine(_tempDir, name);

        var (exit, _, _) = Run("/nologo", "/winrt", "/nomidl", "/winmd", P("TestNs.winmd"), "/h", P("out.h"),
            "/iid", P("out_i.c"), "/proxy", P("out_p.c"), "/dlldata", P("dlldata.c"), "/no_def_idir", idl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(P("TestNs.winmd")));
        Assert.True(File.Exists(P("out.h")));
        Assert.False(File.Exists(P("out_i.c")));
        Assert.False(File.Exists(P("out_p.c")));
        Assert.False(File.Exists(P("dlldata.c")));
    }

    // -----------------------------------------------------------------------
    // /deterministic — two builds of the same input are byte-identical
    // -----------------------------------------------------------------------
    [Fact]
    public void Deterministic_ProducesByteIdenticalWinMd()
    {
        var idl = WriteIdl(SimpleIdl);

        // Emit the same output filename twice into separate directories so the
        // module name (which embeds the filename) is constant across runs.
        var dir1 = Path.Combine(_tempDir, "d1");
        var dir2 = Path.Combine(_tempDir, "d2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        var winmd1 = Path.Combine(dir1, "TestNs.winmd");
        var winmd2 = Path.Combine(dir2, "TestNs.winmd");

        var (exit1, _, _) = Run("/nologo", "/winmd", winmd1, "/deterministic", "/no_def_idir", idl);
        var (exit2, _, _) = Run("/nologo", "/winmd", winmd2, "/deterministic", "/no_def_idir", idl);

        Assert.Equal(0, exit1);
        Assert.Equal(0, exit2);
        Assert.Equal(File.ReadAllBytes(winmd1), File.ReadAllBytes(winmd2));
    }
}
