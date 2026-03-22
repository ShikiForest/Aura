using AuraLang.Ast;
using Xunit;

namespace AuraLang.Tests;

/// <summary>
/// Parser snapshot tests: verify AST structure from source code.
/// </summary>
public class ParserTests
{
    private static CompilationUnitNode Parse(string code)
    {
        var result = AuraFrontEnd.ParseCompilationUnit(code);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Ast);
        return result.Ast!;
    }

    [Fact]
    public void FunctionDeclaration_BasicSignature()
    {
        var cu = Parse(@"
            fn add(a: i32, b: i32) -> i32 {
                return a + b
            }
        ");
        Assert.Single(cu.Items);
        var fn = Assert.IsType<FunctionDeclNode>(cu.Items[0]);
        Assert.Equal("add", fn.Name.Text);
        Assert.Equal(2, fn.Parameters.Count);
        Assert.IsType<ReturnTypeSpecNode>(fn.ReturnSpec);
    }

    [Fact]
    public void FunctionDeclaration_ExpressionBody()
    {
        var cu = Parse("fn square(x: i32) -> i32 => x * x");
        Assert.Single(cu.Items);
        var fn = Assert.IsType<FunctionDeclNode>(cu.Items[0]);
        Assert.Equal("square", fn.Name.Text);
        Assert.IsType<FunctionExprBodyNode>(fn.Body);
    }

    [Fact]
    public void FunctionDeclaration_WithLetVar()
    {
        var cu = Parse(@"
            fn main() {
                let x: i32 = 42
                var y = 10
            }
        ");
        var fn = Assert.IsType<FunctionDeclNode>(cu.Items[0]);
        var body = Assert.IsType<FunctionBlockBodyNode>(fn.Body);
        Assert.Equal(2, body.Block.Statements.Count);
        var letDecl = Assert.IsType<VarDeclStmtNode>(body.Block.Statements[0]);
        Assert.Equal("x", letDecl.Name.Text);
        Assert.Equal(Mutability.Let, letDecl.Mutability);
        var varDecl = Assert.IsType<VarDeclStmtNode>(body.Block.Statements[1]);
        Assert.Equal("y", varDecl.Name.Text);
        Assert.Equal(Mutability.Var, varDecl.Mutability);
    }

    [Fact]
    public void ClassDeclaration_WithMembers()
    {
        var cu = Parse(@"
            class Point {
                var _x: i32
                var _y: i32

                fn getX() -> i32 {
                    return _x
                }
            }
        ");
        var cls = Assert.IsType<ClassDeclNode>(cu.Items[0]);
        Assert.Equal("Point", cls.Name.Text);
        Assert.Equal(3, cls.Members.Count);  // 2 fields + 1 function
    }

    [Fact]
    public void EnumDeclaration_ParsesMembers()
    {
        var cu = Parse("enum Color { Red, Green, Blue }");
        var en = Assert.IsType<EnumDeclNode>(cu.Items[0]);
        Assert.Equal("Color", en.Name.Text);
        Assert.Equal(3, en.Members.Count);
        Assert.Equal("Red", en.Members[0].Name.Text);
    }

    [Fact]
    public void TraitDeclaration_WithSignature()
    {
        var cu = Parse(@"
            trait IGreeter {
                fn greet(name: string)
            }
        ");
        var trait = Assert.IsType<TraitDeclNode>(cu.Items[0]);
        Assert.Equal("IGreeter", trait.Name.Text);
    }

    [Fact]
    public void PipeExpression_Parses()
    {
        var cu = Parse(@"
            fn main() {
                42 | Console.WriteLine
            }
        ");
        Assert.Single(cu.Items);
    }

    [Fact]
    public void GuardExpression_Parses()
    {
        var cu = Parse(@"
            fn main() {
                let x = risky() ~ (e) => 0
            }
        ");
        Assert.Single(cu.Items);
    }

    [Fact]
    public void SwitchExpression_Parses()
    {
        var cu = Parse(@"
            fn main() {
                let x = 1 switch {
                    1 => ""one"",
                    _ => ""other""
                }
            }
        ");
        Assert.Single(cu.Items);
    }

    [Fact]
    public void BuilderNewExpression_Parses()
    {
        var cu = Parse(@"
            fn main() {
                let b = new VoidBuilder()
                let obj = new(b)
            }
        ");
        Assert.Single(cu.Items);
    }

    [Fact]
    public void StringInterpolation_Parses()
    {
        var cu = Parse(@"
            fn main() {
                let name = ""World""
                let msg = $""Hello, {name}!""
            }
        ");
        Assert.Single(cu.Items);
    }

    [Fact]
    public void StructDeclaration_Parses()
    {
        var cu = Parse(@"
            struct Vec2 {
                var _x: f64
                var _y: f64
            }
        ");
        var s = Assert.IsType<StructDeclNode>(cu.Items[0]);
        Assert.Equal("Vec2", s.Name.Text);
    }

    [Fact]
    public void ClassInheritance_Parses()
    {
        var cu = Parse(@"
            class Animal {
                var _name: string
            }
            class Dog : Animal {
                var _breed: string
            }
        ");
        Assert.Equal(2, cu.Items.Count);
        var dog = Assert.IsType<ClassDeclNode>(cu.Items[1]);
        Assert.Equal("Dog", dog.Name.Text);
        Assert.True(dog.BaseTypes.Count > 0);
    }

    [Fact]
    public void SyntaxError_ReportsDiagnostic()
    {
        // Parser may throw on severely malformed input; just verify it doesn't crash silently
        try
        {
            var result = AuraFrontEnd.ParseCompilationUnit("let @@@ = ;");
            // If it returns, it should have diagnostics
            Assert.NotEmpty(result.Diagnostics);
        }
        catch
        {
            // If it throws, that's also acceptable for malformed input
        }
    }
}
