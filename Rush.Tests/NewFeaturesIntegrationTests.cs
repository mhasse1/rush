using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Integration tests for new shell features: brace expansion, printf,
/// arithmetic expansion, ~user, process substitution, and set options.
/// All tests run via `rush -c` to exercise the full pipeline.
/// Uses ArgumentList (not Arguments) to avoid parent-shell $ interpretation.
/// </summary>
public class NewFeaturesIntegrationTests
{
    // ══════════════════════════════════════════════════════════════════
    // ── Brace Expansion ──────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("echo {a,b,c}", "a b c")]
    [InlineData("echo file.{bak,txt}", "file.bak file.txt")]
    [InlineData("echo pre{A,B}post", "preApost preBpost")]
    public void BraceExpansion_BasicPatterns(string command, string expected)
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(command);
        Assert.Equal(expected, stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void BraceExpansion_PathPattern()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo src/{a,b,c}/main.rs");
        Assert.Equal("src/a/main.rs src/b/main.rs src/c/main.rs", stdout);
    }

    [Fact]
    public void BraceExpansion_NestedBraces()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo {a,{b,c}}");
        Assert.Equal("a b c", stdout);
    }

    [Fact]
    public void BraceExpansion_NoBraces_PassThrough()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo hello world");
        Assert.Equal("hello world", stdout);
    }

    [Fact]
    public void BraceExpansion_SingleItemNoBrace()
    {
        // Single item (no comma) should not expand
        var (stdout, _, _) = TestHelper.RunRush("echo {solo}");
        Assert.Equal("{solo}", stdout);
    }

    [Fact]
    public void BraceExpansion_EmptyBraces_NoExpansion()
    {
        // Empty braces or braces without commas should pass through
        var (stdout, _, _) = TestHelper.RunRush("echo {}");
        Assert.Equal("{}", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Printf Builtin ───────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Printf_StringFormat()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf '%s' hello");
        Assert.Equal("hello", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Printf_IntegerFormat()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf '%d' 42");
        Assert.Equal("42", stdout);
    }

    [Fact]
    public void Printf_HexFormat()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf '%x' 255");
        Assert.Equal("ff", stdout);
    }

    [Fact]
    public void Printf_PercentLiteral()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf '100%%'");
        Assert.Equal("100%", stdout);
    }

    [Fact]
    public void Printf_MultipleArgs()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf '%s is %d' name 42");
        Assert.Equal("name is 42", stdout);
    }

    [Fact]
    public void Printf_FloatFormat()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf '%f' 3.14");
        Assert.StartsWith("3.14", stdout);
    }

    [Fact]
    public void Printf_NewlineEscape()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf 'line1\\nline2'");
        Assert.Contains("line1", stdout);
        Assert.Contains("line2", stdout);
    }

    [Fact]
    public void Printf_TabEscape()
    {
        var (stdout, _, _) = TestHelper.RunRush("printf 'a\\tb'");
        Assert.Contains("a\tb", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Arithmetic Expansion ─────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("echo $((2 + 3))", "5")]
    [InlineData("echo $((10 - 4))", "6")]
    [InlineData("echo $((3 * 7))", "21")]
    [InlineData("echo $((20 / 4))", "5")]
    [InlineData("echo $((17 % 5))", "2")]
    public void ArithmeticExpansion_BasicOperations(string command, string expected)
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(command);
        Assert.Equal(expected, stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ArithmeticExpansion_NestedParens()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo $(( (2 + 3) * 4 ))");
        Assert.Equal("20", stdout);
    }

    [Fact]
    public void ArithmeticExpansion_InContext()
    {
        // Arithmetic inside a larger string
        var (stdout, _, _) = TestHelper.RunRush("echo result=$((5+5))");
        Assert.Equal("result=10", stdout);
    }

    [Fact]
    public void ArithmeticExpansion_NoArithmetic_PassThrough()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo no math here");
        Assert.Equal("no math here", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Tilde Expansion ──────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void TildeExpansion_HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = TestHelper.RunRush("echo ~");
        Assert.Equal(home, stdout);
    }

    [Fact]
    public void TildeExpansion_HomeSlashPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = TestHelper.RunRush("echo ~/Documents");
        Assert.Equal($"{home}/Documents", stdout);
    }

    [Fact]
    public void TildeExpansion_QuotedTilde_NoExpansion()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo '~'");
        Assert.Equal("~", stdout);
    }

    [Fact]
    public void TildeExpansion_RegexOperator_NoExpansion()
    {
        // =~ and !~ operators should NOT trigger tilde expansion
        var (stdout, _, _) = TestHelper.RunRush("x = \"hello\"\nif x =~ \"hell\"\n  puts \"matched\"\nend");
        Assert.Equal("matched", stdout);
    }

    [Fact]
    public void TildeUser_CurrentUser_Resolves()
    {
        var username = Environment.UserName;
        var userHome = OperatingSystem.IsMacOS() ? $"/Users/{username}" : $"/home/{username}";

        // Only test if the user's home directory exists at the expected path
        if (Directory.Exists(userHome))
        {
            var (stdout, _, _) = TestHelper.RunRush($"echo ~{username}");
            Assert.Equal(userHome, stdout);
        }
    }

    [Fact]
    public void TildeUser_UnknownUser_PassThrough()
    {
        // A user that definitely doesn't exist
        var (stdout, _, _) = TestHelper.RunRush("echo ~zzz_nonexistent_user_zzz");
        Assert.Equal("~zzz_nonexistent_user_zzz", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Process Substitution ─────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessSubstitution_BasicCommand()
    {
        // <(echo hello) creates a temp file containing "hello", substitutes the path
        var (stdout, _, exitCode) = TestHelper.RunRush("cat <(echo hello)");
        Assert.Equal("hello", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ProcessSubstitution_WithSort()
    {
        // Process substitution with a different command
        var (stdout, _, _) = TestHelper.RunRush("cat <(echo sorted)");
        Assert.Equal("sorted", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Environment Variables ────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void EnvVar_HOME_Expands()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = TestHelper.RunRush("echo $HOME");
        Assert.Equal(home, stdout);
    }

    [Fact]
    public void EnvVar_PATH_Expands()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo $PATH");
        Assert.NotEmpty(stdout);
        // PATH should contain at least one path separator
        Assert.Contains(Path.PathSeparator.ToString(), stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Redirect + Expansion Combo ───────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void BraceExpansion_WithRedirect()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            TestHelper.RunRush($"echo {{alpha,beta}} > {tmpFile}");
            var content = File.ReadAllText(tmpFile).Trim();
            Assert.Equal("alpha beta", content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ArithmeticExpansion_WithRedirect()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            TestHelper.RunRush($"echo $((6 * 7)) > {tmpFile}");
            var content = File.ReadAllText(tmpFile).Trim();
            Assert.Equal("42", content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── Loop/End ──────────────────────────────────────────────────────

    [Fact]
    public void Loop_WithBreak_CountsToThree()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("x = 0\nloop\n  x += 1\n  break if x >= 3\nend\nputs x");
        Assert.Equal("3", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Loop_WithNext_SkipsOddNumbers()
    {
        var (stdout, _, _) = TestHelper.RunRush("result = \"\"\ni = 0\nloop\n  i += 1\n  break if i > 6\n  next if i % 2 != 0\n  result += i.to_s + \" \"\nend\nputs result.strip");
        Assert.Equal("2 4 6", stdout);
    }

    // ── Duration Literals ────────────────────────────────────────────

    [Fact]
    public void Duration_Hours_TotalMinutes()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 2.hours.TotalMinutes");
        Assert.Equal("120", stdout);
    }

    [Fact]
    public void Duration_Minutes_TotalSeconds()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 5.minutes.TotalSeconds");
        Assert.Equal("300", stdout);
    }

    [Fact]
    public void Duration_Days_TotalHours()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 3.days.TotalHours");
        Assert.Equal("72", stdout);
    }

    [Fact]
    public void Duration_Seconds_TotalMilliseconds()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 10.seconds.TotalMilliseconds");
        Assert.Equal("10000", stdout);
    }

    // ── Time.now ─────────────────────────────────────────────────────

    [Fact]
    public void TimeNow_ReturnsCurrentYear()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts Time.now.Year");
        Assert.Equal(DateTime.Now.Year.ToString(), stdout);
    }

    [Fact]
    public void TimeToday_ReturnsDate()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts Time.today.Year");
        Assert.Equal(DateTime.Today.Year.ToString(), stdout);
    }

    // ── ARGV / __FILE__ / __DIR__ ────────────────────────────────────

    /// <summary>
    /// Run a rush script file with arguments and capture output.
    /// </summary>
    private static (string stdout, string stderr, int exitCode) RunRushScript(string scriptContent, params string[] scriptArgs)
    {
        var tmpFile = Path.GetTempFileName();
        var scriptPath = Path.ChangeExtension(tmpFile, ".rush");
        File.Move(tmpFile, scriptPath);
        try
        {
            File.WriteAllText(scriptPath, scriptContent);
            var psi = new ProcessStartInfo
            {
                FileName = TestHelper.RushBinary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            foreach (var arg in scriptArgs)
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void ARGV_FirstArgument()
    {
        var (stdout, _, _) = RunRushScript("puts ARGV[0]", "hello");
        Assert.Equal("hello", stdout);
    }

    [Fact]
    public void ARGV_MultipleArguments()
    {
        var (stdout, _, _) = RunRushScript("puts ARGV[0]\nputs ARGV[1]", "first", "second");
        Assert.Contains("first", stdout);
        Assert.Contains("second", stdout);
    }

    [Fact]
    public void ARGV_Count()
    {
        var (stdout, _, _) = RunRushScript("puts ARGV.Count", "a", "b", "c");
        Assert.Equal("3", stdout);
    }

    [Fact]
    public void FILE_ReturnsScriptPath()
    {
        var (stdout, _, _) = RunRushScript("puts __FILE__");
        Assert.EndsWith(".rush", stdout);
        Assert.True(Path.IsPathRooted(stdout), "__FILE__ should be an absolute path");
    }

    [Fact]
    public void DIR_ReturnsScriptDirectory()
    {
        var (stdout, _, _) = RunRushScript("puts __DIR__");
        Assert.True(Directory.Exists(stdout), "__DIR__ should be an existing directory");
    }

    // ── Semicolons as Statement Separators ────────────────────────────

    [Fact]
    public void Semicolons_LoopOneLiner()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("x = 0; loop; x += 1; break if x >= 5; end; puts x");
        Assert.Equal("5", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Semicolons_DurationAssignment()
    {
        var (stdout, _, _) = TestHelper.RunRush("h = 2.hours; puts h.TotalMinutes");
        Assert.Equal("120", stdout);
    }

    [Fact]
    public void Semicolons_MultipleAssignments()
    {
        var (stdout, _, _) = TestHelper.RunRush("a = 10; b = 20; puts a + b");
        Assert.Equal("30", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── File Stdlib ──────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void File_Write_And_Read()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var script = $"File.write(\"{tmpFile}\", \"hello rush\")\nputs File.read(\"{tmpFile}\")";
            var (stdout, _, exitCode) = TestHelper.RunRush(script);
            Assert.Equal("hello rush", stdout);
            Assert.Equal(0, exitCode);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_Append()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var script = $"File.write(\"{tmpFile}\", \"line1\")\nFile.append(\"{tmpFile}\", \"line2\")\nputs File.read(\"{tmpFile}\")";
            var (stdout, _, _) = TestHelper.RunRush(script);
            Assert.Contains("line1", stdout);
            Assert.Contains("line2", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_ReadLines_Count()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmpFile, "alpha\nbeta\ngamma");
            var (stdout, _, _) = TestHelper.RunRush($"lines = File.read_lines(\"{tmpFile}\")\nputs lines.Count");
            Assert.Equal("3", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_Exist_True()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"puts File.exist?(\"{tmpFile}\")");
            Assert.Equal("True", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_Exist_False()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts File.exist?(\"/nonexistent_file_12345\")");
        Assert.Equal("False", stdout);
    }

    [Fact]
    public void File_Delete()
    {
        var tmpFile = Path.GetTempFileName();
        var script = $"File.delete(\"{tmpFile}\")\nputs File.exist?(\"{tmpFile}\")";
        var (stdout, _, _) = TestHelper.RunRush(script);
        Assert.Equal("False", stdout);
        Assert.False(System.IO.File.Exists(tmpFile));
    }

    [Fact]
    public void File_Size()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmpFile, "12345");
            var (stdout, _, _) = TestHelper.RunRush($"puts File.size(\"{tmpFile}\")");
            Assert.Equal("5", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_ReadJson_AccessProperty()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmpFile, "{\"name\": \"rush\", \"version\": 2}");
            var (stdout, _, _) = TestHelper.RunRush($"data = File.read_json(\"{tmpFile}\")\nputs data.name");
            Assert.Equal("rush", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_ReadCsv_Count()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmpFile, "Name,Age\nAlice,30\nBob,25");
            var (stdout, _, _) = TestHelper.RunRush($"rows = File.read_csv(\"{tmpFile}\")\nputs rows.Count");
            Assert.Equal("2", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void File_Standalone_Write()
    {
        // Tests triage: File.write as standalone statement (not assignment)
        var tmpFile = Path.GetTempFileName();
        try
        {
            var (_, _, exitCode) = TestHelper.RunRush($"File.write(\"{tmpFile}\", \"standalone test\")");
            Assert.Equal(0, exitCode);
            Assert.Equal("standalone test", System.IO.File.ReadAllText(tmpFile).Trim());
        }
        finally { File.Delete(tmpFile); }
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Dir Stdlib ───────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Dir_Exist_True()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"puts Dir.exist?(\"{tmpDir}\")");
            Assert.Equal("True", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_Mkdir()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"Dir.mkdir(\"{tmpDir}\")\nputs Dir.exist?(\"{tmpDir}\")");
            Assert.Equal("True", stdout);
            Assert.True(Directory.Exists(tmpDir));
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_Mkdir_Standalone()
    {
        // Tests triage: Dir.mkdir as standalone statement
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (_, _, exitCode) = TestHelper.RunRush($"Dir.mkdir(\"{tmpDir}\")");
            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(tmpDir));
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_List_All()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub1"));
        try
        {
            System.IO.File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            System.IO.File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
            var (stdout, _, _) = TestHelper.RunRush($"items = Dir.list(\"{tmpDir}\")\nputs items.Count");
            Assert.Equal("3", stdout); // 2 files + 1 dir
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_List_FilesOnly()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            System.IO.File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            System.IO.File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
            Directory.CreateDirectory(Path.Combine(tmpDir, "sub1"));
            var (stdout, _, _) = TestHelper.RunRush($"files = Dir.list(\"{tmpDir}\", type: \"file\")\nputs files.Count");
            Assert.Equal("2", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_List_Recursive()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        var subDir = Path.Combine(tmpDir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            System.IO.File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            System.IO.File.WriteAllText(Path.Combine(subDir, "b.txt"), "");
            var (stdout, _, _) = TestHelper.RunRush($"files = Dir.list(\"{tmpDir}\", type: \"file\", recursive: true)\nputs files.Count");
            Assert.Equal("2", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Dir_List_DirsOnly()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub1"));
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub2"));
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"subdirs = Dir.list(\"{tmpDir}\", type: \"dir\")\nputs subdirs.Count");
            Assert.Equal("2", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    // ── Multiple Assignment ──────────────────────────────────────────────

    [Fact]
    public void MultiAssign_ParseTwoVars()
    {
        var lexer = new Lexer("a, b = 1, 2");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().First();
        var ma = Assert.IsType<MultipleAssignmentNode>(node);
        Assert.Equal(new[] { "a", "b" }, ma.Names);
        Assert.Equal(2, ma.Values.Count);
    }

    [Fact]
    public void MultiAssign_ParseThreeVars()
    {
        var lexer = new Lexer("x, y, z = 10, 20, 30");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().First();
        var ma = Assert.IsType<MultipleAssignmentNode>(node);
        Assert.Equal(3, ma.Names.Count);
        Assert.Equal(3, ma.Values.Count);
    }

    [Fact]
    public void MultiAssign_Transpile()
    {
        var lexer = new Lexer("a, b, c = 1, 2, 3");
        var parser = new Parser(lexer.Tokenize());
        var transpiler = new RushTranspiler(new CommandTranslator());
        var ps = string.Join("\n", parser.Parse().Select(n => transpiler.TranspileNode(n)));
        Assert.Contains("$a = 1", ps);
        Assert.Contains("$b = 2", ps);
        Assert.Contains("$c = 3", ps);
    }

    [Fact]
    public void MultiAssign_Triage()
    {
        var engine = new ScriptEngine(new CommandTranslator());
        Assert.True(engine.IsRushSyntax("a, b = 1, 2"));
        Assert.True(engine.IsRushSyntax("x, y, z = 10, 20, 30"));
    }

    [Fact]
    public void MultiAssign_Integration()
    {
        var (stdout, stderr, exitCode) = TestHelper.RunRush("a, b, c = 1, 2, 3\nputs a\nputs b\nputs c");
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("1\n2\n3", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void MultiAssign_WithStrings()
    {
        var (stdout, stderr, exitCode) = TestHelper.RunRush("first, last = \"Alice\", \"Smith\"\nputs first + \" \" + last");
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Alice Smith", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void MultiAssign_FewerValues()
    {
        // More names than values — extras get $null
        var (stdout, stderr, exitCode) = TestHelper.RunRush("a, b, c = 1, 2\nputs a\nputs b\nputs c");
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        // c should be empty/null
        Assert.Contains("1", stdout);
        Assert.Contains("2", stdout);
        Assert.Equal(0, exitCode);
    }
}
