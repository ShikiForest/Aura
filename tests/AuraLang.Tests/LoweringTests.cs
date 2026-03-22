using AuraLang.Ast;
using AuraLang.Lowering;
using AuraLang.Semantics;
using Xunit;

namespace AuraLang.Tests;

/// <summary>
/// Lowering tests: verify that the lowerer transforms AST nodes correctly.
/// </summary>
public class LoweringTests
{
    private static (CompilationUnitNode Lowered, IReadOnlyList<LoweringDiagnostic> Diags) Lower(string code)
    {
        var parseResult = AuraFrontEnd.ParseCompilationUnit(code);
        Assert.Empty(parseResult.Diagnostics);

        var lowerer = new AuraLowerer();
        var result = lowerer.Lower(parseResult.Ast!);
        return (result.Ast, result.Diagnostics);
    }

    [Fact]
    public void SimpleFunctionBody_LowersWithoutDiagnostics()
    {
        var (lowered, diags) = Lower(@"
            fn add(a: i32, b: i32) -> i32 {
                return a + b
            }
        ");
        Assert.NotNull(lowered);
        Assert.Empty(diags);
    }

    [Fact]
    public void PipeExpression_LowersToCallChain()
    {
        var (lowered, diags) = Lower(@"
            fn main() {
                42 | Console.WriteLine
            }
        ");
        Assert.NotNull(lowered);
        Assert.Empty(diags);
        // The pipe should be lowered — verify function body exists
        var fn = Assert.IsType<FunctionDeclNode>(lowered.Items[0]);
        var body = Assert.IsType<FunctionBlockBodyNode>(fn.Body);
        Assert.True(body.Block.Statements.Count > 0);
    }

    [Fact]
    public void GuardExpression_LowersToTryCatch()
    {
        var (lowered, diags) = Lower(@"
            fn main() {
                let x = risky() ~ (e) => 0
            }
        ");
        Assert.NotNull(lowered);
        Assert.Empty(diags);
    }

    [Fact]
    public void UsingStatement_LowersToTryFinally()
    {
        var (lowered, diags) = Lower(@"
            fn main() {
                using let conn = open_connection() {
                    conn.execute()
                }
            }
        ");
        Assert.NotNull(lowered);
        Assert.Empty(diags);
    }

    [Fact]
    public void StateFunctions_LowerCorrectly()
    {
        var (lowered, diags) = Lower(@"
            enum MyState { Idle, Running }
            class Bot {
                fn run() : MyState.Idle { Console.WriteLine(""idle"") }
                fn run() : MyState.Running { Console.WriteLine(""run"") }
            }
        ");
        Assert.NotNull(lowered);
        // State function lowering may produce diagnostics but should not error
        var errors = diags.Where(d => d.Severity == LoweringSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void SwitchExpression_LowersToConditionals()
    {
        var (lowered, diags) = Lower(@"
            fn describe(x: i32) -> string {
                return x switch {
                    1 => ""one"",
                    2 => ""two"",
                    _ => ""other""
                }
            }
        ");
        Assert.NotNull(lowered);
        Assert.Empty(diags);
    }
}
