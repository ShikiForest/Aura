using System.IO;
using Antlr4.Runtime;
using AuraLang.Ast;

public static class AuraFrontEnd
{
    public static ParseResult<CompilationUnitNode> ParseCompilationUnit(string code)
    {
        var diags = new List<Diagnostic>();

        var input = new AntlrInputStream(code);
        var lexer = new AuraLexer(input);
        var tokens = new CommonTokenStream(lexer);
        var parser = new AuraParser(tokens);

        parser.RemoveErrorListeners();
        parser.AddErrorListener(new CollectingParserErrorListener(diags));

        var tree = parser.compilationUnit();

        CompilationUnitNode? ast = null;
        try
        {
            var builder = new AuraAstBuilder();
            ast = builder.BuildCompilationUnit(tree);
        }
        catch (Exception ex)
        {
            // Convert AST builder exceptions into diagnostics instead of crashing the CLI
            var pos = new SourcePos(0, 1, 0);
            var span = new SourceSpan(pos, pos);
            diags.Add(new Diagnostic(span, $"Internal error during AST construction: {ex.Message}"));
        }

        return new ParseResult<CompilationUnitNode>(ast, diags);
    }

    private sealed class CollectingParserErrorListener : BaseErrorListener
    {
        private readonly List<Diagnostic> _diags;
        public CollectingParserErrorListener(List<Diagnostic> diags) => _diags = diags;

        public override void SyntaxError(
            TextWriter output,
            IRecognizer recognizer,
            IToken offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            var span = SpanFactory.From(offendingSymbol);
            _diags.Add(new Diagnostic(span, msg));
        }
    }
}