using System.Collections.Generic;
using Antlr4.Runtime;

/// <summary>
/// Go-style Automatic Semicolon Insertion (ASI) for the Aura lexer.
/// After certain token types, if a newline (NL) follows, a synthetic SEMI token
/// is injected. This lets the parser require mandatory SEMI while users write
/// semicolon-free code.
/// </summary>
public partial class AuraLexer
{
    private IToken? _lastNonHiddenToken;
    private readonly Queue<IToken> _pendingTokens = new();

    public override IToken NextToken()
    {
        // Drain any queued tokens first (the NL that triggered ASI)
        if (_pendingTokens.Count > 0)
            return _pendingTokens.Dequeue();

        var token = base.NextToken();

        // ASI: insert SEMI before NL when previous non-hidden token is a trigger
        // Skip ASI inside interpolation expressions to avoid breaking $"...{expr}..."
        if (token.Type == NL && !_inInterpExpr
            && _lastNonHiddenToken is not null
            && IsAsiTrigger(_lastNonHiddenToken.Type))
        {
            var semi = new CommonToken(SEMI, ";")
            {
                Line = _lastNonHiddenToken.Line,
                Column = _lastNonHiddenToken.Column + (_lastNonHiddenToken.Text?.Length ?? 0),
            };
            _lastNonHiddenToken = semi;
            _pendingTokens.Enqueue(token); // queue the NL for later
            return semi;
        }

        // ASI before EOF: ensure the last statement gets a SEMI
        if (token.Type == Eof && !_inInterpExpr
            && _lastNonHiddenToken is not null
            && IsAsiTrigger(_lastNonHiddenToken.Type))
        {
            var semi = new CommonToken(SEMI, ";")
            {
                Line = _lastNonHiddenToken.Line,
                Column = _lastNonHiddenToken.Column + (_lastNonHiddenToken.Text?.Length ?? 0),
            };
            _lastNonHiddenToken = null; // prevent double-insert
            _pendingTokens.Enqueue(token); // queue EOF
            return semi;
        }

        if (token.Channel == TokenConstants.DefaultChannel)
            _lastNonHiddenToken = token;

        return token;
    }

    /// <summary>
    /// Token types that trigger automatic semicolon insertion when followed by a newline.
    /// Modelled after Go's spec: identifiers, literals, certain keywords, closing brackets,
    /// and type-ending tokens.
    /// </summary>
    private static bool IsAsiTrigger(int type)
    {
        return type switch
        {
            // Identifiers (including context-sensitive ones)
            IDENTIFIER or SELF or ITEM or HANDLE
                or SERIALIZE or DESERIALIZE or OPERATOR => true,

            // Literals
            INT_LIT or FLOAT_LIT or STRING_LIT or CHAR_LIT
                or INTERP_END => true,

            // Literal keywords
            NULL or TRUE or FALSE => true,

            // Statement-ending keywords
            RETURN or BREAK or CONTINUE or THROW => true,

            // Closing brackets
            RPAREN or RBRACK or RBRACE => true,

            // Builtin type keywords (can end type annotations)
            I8 or I16 or I32 or I64
                or U8 or U16 or U32 or U64
                or F32 or F64
                or DECIMAL_T or BOOL_T or CHAR_T or STRING_T
                or OBJECT_T or VOID_T => true,

            // Type suffix tokens
            GT => true,       // closes generic: List<string>
            QUESTION => true, // nullable suffix: string?

            // Wildcard
            UNDERSCORE => true,

            _ => false
        };
    }
}
