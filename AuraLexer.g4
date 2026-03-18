lexer grammar AuraLexer;

@members {
    private int _interpDepth = 0;
    private bool _inInterpExpr = false;
}

/* =========================
 *  Fragments
 * ========================= */
fragment DIGIT : [0-9];
fragment EXP   : [eE] [+\-]? DIGIT+;
fragment HEX   : [0-9a-fA-F];
fragment ESC_SEQ
    : '\\' [btnfr"'\\]
    | '\\' 'u' HEX HEX HEX HEX
    ;

/* =========================
 *  Keywords
 * ========================= */
LET         : 'let';
VAR         : 'var';
FN          : 'fn';
PUB         : 'pub';
PROPERTY    : 'property';
TRAIT       : 'trait';
STRUCT      : 'struct';
CLASS       : 'class';

DERIVABLE   : 'derivable';
OP          : 'op';
OPERATOR    : 'operator';
DERIVATEOF  : 'derivateof';
WINDOW      : 'window';
WINDOWOF    : 'windowof';

ITEM        : 'item';
NEW         : 'new';
HANDLE      : 'handle';
SELF        : 'self';
SERIALIZE   : 'serialize';
DESERIALIZE : 'deserialize';

IF          : 'if';
ELSE        : 'else';
FOR         : 'for';
IN          : 'in';
WHILE       : 'while';
RETURN      : 'return';
BREAK       : 'break';
CONTINUE    : 'continue';
ASYNC       : 'async';
AWAIT       : 'await';

NAMESPACE   : 'namespace';
IMPORT      : 'import';
ENUM        : 'enum';

SWITCH      : 'switch';
CASE        : 'case';
DEFAULT     : 'default';
WHEN        : 'when';
USING       : 'using';

TRY         : 'try';
CATCH       : 'catch';
FINALLY     : 'finally';

NULL        : 'null';
TRUE        : 'true';
FALSE       : 'false';

IS          : 'is';
AS          : 'as';
THROW       : 'throw';

WHERE       : 'where';
GET         : 'get';
SET         : 'set';

PAT_NOT     : 'not';
PAT_AND     : 'and';
PAT_OR      : 'or';

/* =========================
 *  Builtin Types
 * ========================= */
I8          : 'i8';
I16         : 'i16';
I32         : 'i32';
I64         : 'i64';
U8          : 'u8';
U16         : 'u16';
U32         : 'u32';
U64         : 'u64';
F32         : 'f32';
F64         : 'f64';
DECIMAL_T   : 'decimal';
BOOL_T      : 'bool';
CHAR_T      : 'char';
STRING_T    : 'string';
OBJECT_T    : 'object';
VOID_T      : 'void';

/* =========================
 *  Interpolated Strings
 * ========================= */
INTERP_START
    : '$"' -> pushMode(INTERP)
    ;

STRING_LIT
    : '"' ( ESC_SEQ | ~["\\\r\n] )* '"'
    ;

/* 默认模式下：插值表达式结束用这个 token 把 mode 弹回 INTERP */
INTERP_RBRACE
    : { _inInterpExpr && _interpDepth == 1 }?
      '}' { _interpDepth = 0; _inInterpExpr = false; }
      -> popMode
    ;

/* =========================
 *  Operators & Punctuations
 * ========================= */
THINARROW       : '->';
FATARROW        : '=>';

COALESCE_ASSIGN : '??=';
COALESCE        : '??';

LE              : '<=';
GE              : '>=';
EQUAL           : '==';
NOTEQUAL        : '!=';

OROR            : '||';
ANDAND          : '&&';

ADD_ASSIGN      : '+=';
SUB_ASSIGN      : '-=';
MUL_ASSIGN      : '*=';
DIV_ASSIGN      : '/=';
MOD_ASSIGN      : '%=';

ASSIGN          : '=';

LT              : '<';
GT              : '>';

PLUS            : '+';
MINUS           : '-';
STAR            : '*';
SLASH           : '/';
PERCENT         : '%';

BANG            : '!';

PIPE            : '|';
TILDE           : '~';
QUESTION        : '?';

COLON           : ':';
SEMI            : ';';
COMMA           : ',';
DOT             : '.';

LPAREN          : '(';
RPAREN          : ')';

LBRACK          : '[';
RBRACK          : ']';

UNDERSCORE      : '_';

/* 大括号（插值表达式里要计数，支持 switch { ... } 嵌套） */
LBRACE
    : '{' { if (_inInterpExpr) _interpDepth++; }
    ;

RBRACE
    : '}' { if (_inInterpExpr) _interpDepth--; }
    ;

/* =========================
 *  Literals / Identifiers
 * ========================= */
FLOAT_LIT
    : DIGIT+ '.' DIGIT* EXP?
    | '.' DIGIT+ EXP?
    | DIGIT+ EXP
    ;

INT_LIT
    : DIGIT+
    ;

CHAR_LIT
    : '\'' ( ESC_SEQ | ~['\\\r\n] ) '\''
    ;

IDENTIFIER
    : [a-zA-Z_] [a-zA-Z_0-9]*
    ;

/* =========================
 *  Comments & WS
 * ========================= */
DOC_COMMENT
    : '///' ~[\r\n]* -> channel(HIDDEN)
    ;

LINE_COMMENT
    : '//' ~[\r\n]* -> channel(HIDDEN)
    ;

BLOCK_COMMENT
    : '/*' .*? '*/' -> channel(HIDDEN)
    ;

WS
    : [ \t\r\n\u000C]+ -> channel(HIDDEN)
    ;

/* =========================
 *  INTERP mode
 * ========================= */
mode INTERP;

INTERP_END
    : '"' -> popMode
    ;

INTERP_ESCAPED_LBRACE
    : '{{' -> type(INTERP_TEXT)
    ;

INTERP_ESCAPED_RBRACE
    : '}}' -> type(INTERP_TEXT)
    ;

/* 插值开始：进入 DEFAULT_MODE 扫表达式 */
INTERP_LBRACE
    : '{' { _inInterpExpr = true; _interpDepth = 1; } -> pushMode(DEFAULT_MODE)
    ;

INTERP_TEXT
    : ( ESC_SEQ
      | '\\' .
      | ~["\\{\r\n]
      )+
    ;