parser grammar AuraParser;

options { tokenVocab=AuraLexer; }

/* =========================
 *  Compilation Unit
 * ========================= */

compilationUnit
    : (importDecl
      | namespaceDecl
      | topLevelDecl
      | SEMI
      )* EOF
    ;

importDecl
    : IMPORT qualifiedName SEMI
    ;

namespaceDecl
    : NAMESPACE qualifiedName namespaceBody
    ;

namespaceBody
    : LBRACE (importDecl | topLevelDecl | SEMI)* RBRACE
    ;

topLevelDecl
    : traitDecl
    | classDecl
    | structDecl
    | enumDecl
    | windowDecl
    | functionDecl
    ;

/* ---------- Attributes ---------- */

attributeSection
    : LBRACK attribute (COMMA attribute)* RBRACK
    ;

attribute
    : qualifiedName (LPAREN attributeArgumentList? RPAREN)?
    ;

attributeArgumentList
    : attributeArgument (COMMA attributeArgument)*
    ;

attributeArgument
    : identifier ( (ASSIGN | COLON) expression )
    | expression
    ;

/* ---------- Decls ---------- */

visibilityModifier
    : PUB
    ;

traitDecl
    : attributeSection* visibilityModifier? TRAIT identifier traitBody
    ;

traitBody
    : LBRACE (traitMember | SEMI)* RBRACE
    ;

traitMember
    : attributeSection* functionSignature SEMI
    ;

classDecl
    : attributeSection* visibilityModifier? CLASS identifier typeParameters?
      (COLON typeList)?
      classBody
    ;

structDecl
    : attributeSection* visibilityModifier? STRUCT identifier typeParameters?
      (COLON typeList)?
      classBody
    ;

classBody
    : LBRACE (classMember | SEMI)* RBRACE
    ;

classMember
    : fieldDecl
    | propertyDecl
    | functionDecl
    | operatorDecl
    | enumDecl
    | windowDecl
    ;

fieldDecl
    : attributeSection* visibilityModifier?
      (LET | VAR)
      identifier
      (COLON type)?
      (ASSIGN expression)?
      SEMI
    ;

propertyDecl
    : attributeSection* visibilityModifier?
      PROPERTY identifier COLON type
      propertyAccessorBlock?
      SEMI
    ;

propertyAccessorBlock
    : LBRACE (accessorDecl | SEMI)* RBRACE
    ;

accessorDecl
    : GET (FATARROW expression SEMI | block)?
    | SET (FATARROW expression SEMI | block)?
    ;

enumDecl
    : attributeSection* visibilityModifier? ENUM identifier enumBody
    ;

enumBody
    : LBRACE enumMember (COMMA enumMember)* COMMA? RBRACE
    ;

enumMember
    : identifier (ASSIGN expression)?
    ;

windowDecl
    : attributeSection* visibilityModifier?
      WINDOW identifier COLON typeReference windowBody
    ;

windowBody
    : LBRACE (windowMemberDecl | SEMI)* RBRACE
    ;

windowMemberDecl
    : identifier COLON type SEMI
    ;

/* ---------- Functions ---------- */

functionDecl
    : attributeSection* visibilityModifier?
      functionModifier*
      FN identifier typeParameters?
      LPAREN parameterList? RPAREN
      functionReturnOrState?
      whereClause*
      functionBody
    ;

functionSignature
    : visibilityModifier?
      functionModifier*
      FN identifier typeParameters?
      LPAREN parameterList? RPAREN
      functionReturnOrState?
      whereClause*
    ;

functionModifier
    : ASYNC
    | DERIVABLE
    ;

functionReturnOrState
    : THINARROW type
    | COLON qualifiedName
    ;

functionBody
    : block
    | FATARROW expression SEMI
    ;

operatorDecl
    : attributeSection* visibilityModifier?
      FN OPERATOR overloadableOp
      LPAREN parameterList? RPAREN
      functionReturnOrState?
      functionBody
    ;

overloadableOp
    : PLUS | MINUS | STAR | SLASH | PERCENT
    | EQUAL | NOTEQUAL
    | LT | GT | LE | GE
    ;

parameterList
    : parameter (COMMA parameter)*
    ;

parameter
    : identifier (COLON type)? (ASSIGN expression)?
    ;

/* ---------- Generics ---------- */

typeParameters
    : LT typeParameter (COMMA typeParameter)* GT
    ;

typeParameter
    : identifier
    ;

typeArguments
    : LT typeList? GT
    ;

whereClause
    : WHERE identifier COLON constraintList
    ;

constraintList
    : typeConstraint (COMMA typeConstraint)*
    ;

typeConstraint
    : typeReference
    | NEW LPAREN RPAREN
    | CLASS
    | STRUCT
    ;

/* ---------- Statements ---------- */

block
    : LBRACE statement* RBRACE
    ;

statement
    : variableDecl
    | ifStatement
    | forStatement
    | whileStatement
    | switchStatement
    | usingStatement
    | returnStatement
    | breakStatement
    | continueStatement
    | throwStatement
    | tryStatement
    | opDeclStatement
    | expressionStatement
    | block
    | SEMI
    ;

variableDecl
    : (LET | VAR) identifier (COLON type)? (ASSIGN expression)? SEMI
    ;

ifStatement
    : IF expression block (ELSE (ifStatement | block))?
    ;

forStatement
    : FOR identifier IN expression block
    ;

whileStatement
    : WHILE expression block
    ;

switchStatement
    : SWITCH (LPAREN expression RPAREN | expression) switchBlock
    ;

switchBlock
    : LBRACE switchSection* RBRACE
    ;

switchSection
    : switchLabel+ statement*
    ;

switchLabel
    : CASE pattern (WHEN expression)? COLON
    | DEFAULT COLON
    ;

usingStatement
    : AWAIT? USING usingResource (block | SEMI)
    ;

usingResource
    : LPAREN usingResourceInner RPAREN
    | usingResourceInner
    ;

usingResourceInner
    : usingLocalDecl (COMMA usingLocalDecl)*
    | expression
    ;

usingLocalDecl
    : (LET | VAR) identifier (COLON type)? ASSIGN expression
    ;

returnStatement
    : RETURN expression? SEMI
    ;

breakStatement
    : BREAK SEMI
    ;

continueStatement
    : CONTINUE SEMI
    ;

throwStatement
    : THROW expression? SEMI
    ;

tryStatement
    : TRY block catchClause+ finallyClause?
    ;

catchClause
    : CATCH (LPAREN identifier (COLON type)? RPAREN)? block
    ;

finallyClause
    : FINALLY block
    ;

opDeclStatement
    : OP identifier COLON functionType SEMI
    ;

expressionStatement
    : expression SEMI
    ;

/* =========================
 *  Expressions
 * ========================= */

expression
    : assignmentExpression
    ;

assignmentExpression
    : conditionalExpression (assignmentOperator assignmentExpression)?
    ;

assignmentOperator
    : ASSIGN
    | COALESCE_ASSIGN
    | ADD_ASSIGN
    | SUB_ASSIGN
    | MUL_ASSIGN
    | DIV_ASSIGN
    | MOD_ASSIGN
    ;

conditionalExpression
    : guardExpression (QUESTION expression COLON conditionalExpression)?
    ;

guardExpression
    : pipeExpression (TILDE pipeExpression)*
    ;

pipeExpression
    : lambdaExpression (PIPE lambdaExpression)*
    ;

lambdaExpression
    : lambdaParameters FATARROW expression
    | nullCoalescingExpression
    ;

lambdaParameters
    : identifier
    | LPAREN lambdaParameterList? RPAREN
    ;

lambdaParameterList
    : lambdaParameter (COMMA lambdaParameter)*
    ;

lambdaParameter
    : identifier (COLON type)?
    ;

nullCoalescingExpression
    : logicalOrExpression (COALESCE nullCoalescingExpression)?
    ;

logicalOrExpression
    : logicalAndExpression (OROR logicalAndExpression)*
    ;

logicalAndExpression
    : equalityExpression (ANDAND equalityExpression)*
    ;

equalityExpression
    : relationalExpression ((EQUAL | NOTEQUAL) relationalExpression)*
    ;

relationalExpression
    : additiveExpression (
          (LT | GT | LE | GE) additiveExpression
        | IS pattern
        | AS type
      )*
    ;

additiveExpression
    : multiplicativeExpression ((PLUS | MINUS) multiplicativeExpression)*
    ;

multiplicativeExpression
    : switchExpression ((STAR | SLASH | PERCENT) switchExpression)*
    ;

switchExpression
    : unaryExpression (SWITCH switchExpressionBlock)?
    ;

switchExpressionBlock
    : LBRACE switchExpressionArm (COMMA switchExpressionArm)* COMMA? RBRACE
    ;

switchExpressionArm
    : pattern (WHEN expression)? FATARROW expression
    ;

unaryExpression
    : (PLUS | MINUS | BANG) unaryExpression
    | AWAIT unaryExpression
    | THROW unaryExpression
    | DERIVATEOF unaryExpression
    | postfixExpression
    ;

postfixExpression
    : primaryExpression postfixSuffix*
    ;

postfixSuffix
    : LPAREN argumentList? RPAREN
    | LBRACK expression RBRACK
    | DOT identifier typeArguments?
    ;

primaryExpression
    : literal
    | identifier
    | LPAREN expression RPAREN
    | listLiteral
    | newExpression
    ;

newExpression
    : NEW typeReference LPAREN argumentList? RPAREN
    ;

argumentList
    : argument (COMMA argument)*
    ;

argument
    : identifier ( (ASSIGN | COLON) expression )
    | UNDERSCORE
    | expression
    ;

listLiteral
    : LBRACK (expression (COMMA expression)*)? COMMA? RBRACK
    ;

/* ----- strings ----- */

literal
    : NULL
    | TRUE
    | FALSE
    | INT_LIT
    | FLOAT_LIT
    | stringLiteral
    | CHAR_LIT
    ;

stringLiteral
    : STRING_LIT
    | interpolatedString
    ;

interpolatedString
    : INTERP_START interpolatedStringPart* INTERP_END
    ;

interpolatedStringPart
    : INTERP_TEXT
    | INTERP_LBRACE expression INTERP_RBRACE
    ;

/* =========================
 *  Patterns
 * ========================= */

pattern
    : patternOr
    ;

patternOr
    : patternAnd (PAT_OR patternAnd)*
    ;

patternAnd
    : patternNot (PAT_AND patternNot)*
    ;

patternNot
    : PAT_NOT patternNot
    | primaryPattern
    ;

primaryPattern
    : LPAREN pattern RPAREN
    | UNDERSCORE
    | VAR identifier?
    | typeReference identifier
    | typeReference
    | (LT | LE | GT | GE) constantExpression
    | constantExpression
    | LBRACE propertySubpatternList? RBRACE
    | LBRACK patternList? RBRACK
    ;

propertySubpatternList
    : propertySubpattern (COMMA propertySubpattern)* COMMA?
    ;

propertySubpattern
    : identifier COLON pattern
    ;

patternList
    : pattern (COMMA pattern)* COMMA?
    ;

constantExpression
    : constLiteral
    | qualifiedName
    | PLUS constantExpression
    | MINUS constantExpression
    ;

constLiteral
    : NULL
    | TRUE
    | FALSE
    | INT_LIT
    | FLOAT_LIT
    | STRING_LIT
    | CHAR_LIT
    ;

/* =========================
 *  Types
 * ========================= */

type
    : functionType nullableSuffix?
    | windowOfType nullableSuffix?
    | namedType nullableSuffix?
    ;

nullableSuffix
    : QUESTION
    ;

windowOfType
    : WINDOWOF LT type GT
    ;

functionType
    : LPAREN typeList? RPAREN THINARROW type
    ;

typeList
    : type (COMMA type)*
    ;

typeReference
    : namedType
    ;

namedType
    : builtinType
    | qualifiedName typeArguments?
    ;

builtinType
    : I8 | I16 | I32 | I64
    | U8 | U16 | U32 | U64
    | F32 | F64
    | DECIMAL_T
    | BOOL_T
    | CHAR_T
    | STRING_T
    | OBJECT_T
    | VOID_T
    | HANDLE
    ;

/* =========================
 *  Names
 * ========================= */

qualifiedName
    : identifier (DOT identifier)*
    ;

identifier
    : IDENTIFIER
    | SELF
    | ITEM
    | SERIALIZE
    | DESERIALIZE
    | HANDLE
    | OPERATOR
    ;