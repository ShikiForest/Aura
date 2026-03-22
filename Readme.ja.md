# **Aura プログラミング言語仕様 (v1.1)**

[English](Readme.md) | 日本語 | [中文](Readme.zh.md)

**対象プラットフォーム:** .NET 10+ (CLI / CTS)

**設計思想:** 構文によるアーキテクチャ制約、暗黙的な副作用ゼロ、徹底的な疎結合、防御的プログラミング。

---

## **基本 & 型**

Aura は後置型宣言を採用し、基本型を .NET Common Type System (CTS) に直接マッピングします。

### **変数宣言**

* `let`: 不変バインディング（`readonly` にマッピング）。
* `var`: 可変バインディング。
* 型推論をサポート — 型注釈 `: type` は省略可能。

```
variableDecl : (LET | VAR) identifier (COLON type)? (ASSIGN expression)? SEMI ;
```

```aura
let pi: f64 = 3.14159
var count = 0           // i32 と推論
var name: string? = null // Nullable 参照型
```

### **プリミティブ型マッピング**

| Aura 型 | .NET CTS | 説明 |
| :---- | :---- | :---- |
| `i8`, `i16`, `i32`, `i64` | SByte, Int16, Int32, Int64 | 符号付き整数 |
| `u8`, `u16`, `u32`, `u64` | Byte, UInt16, UInt32, UInt64 | 符号なし整数 |
| `f32`, `f64` | Single, Double | 浮動小数点数 |
| `decimal` | System.Decimal | 高精度十進数 |
| `bool` | System.Boolean | 真偽値 |
| `char` | System.Char | Unicode 文字 |
| `string` | System.String | 不変文字列 |
| `object` | System.Object | ルート基底型 |
| `void` | System.Void | 戻り値なし |
| `handle` | System.Int32 | 不透明オブジェクトハンドル |

### **型システム**

```
type
    : functionType nullableSuffix?    // (i32, i32) -> bool
    | windowOfType nullableSuffix?    // windowof<T>
    | namedType    nullableSuffix?    // QualifiedName<TypeArgs>?
    ;
```

* **Nullable 接尾辞**: 任意の型に `?` を付けると Nullable になる。
* **関数型**: `(引数型) -> 戻り値型`。
* **Window-of 型**: `windowof<T>` — プロジェクション型参照。

### **コメント**

```aura
// 単一行コメント
/* 複数行コメント */
/// ドキュメントコメント（XML 生成）
```

---

## **関数 & 制御フロー**

関数は第一級オブジェクトで、内部的に `System.Delegate`、`Func<T>`、`Action<T>` にマッピングされます。

### **関数宣言**

```
functionDecl
    : attributeSection* visibilityModifier?
      functionModifier*                          // async | derivable
      FN identifier typeParameters?
      LPAREN parameterList? RPAREN
      functionReturnOrState?                     // -> type | : StateName
      whereClause*
      functionBody                               // block | => expression ;
    ;
```

```aura
// 標準関数
fn add(a: i32, b: i32) -> i32 {
    return a + b
}

// 非同期関数（C# Task モデル基盤）
async fn fetch_data(url: string) -> string {
    let data = await Client.Get(url)
    return data
}

// 式本体
fn square(x: i32) -> i32 => x * x
```

### **演算子オーバーロード**

```
operatorDecl
    : FN OPERATOR overloadableOp LPAREN parameterList? RPAREN functionReturnOrState? functionBody ;
overloadableOp : PLUS | MINUS | STAR | SLASH | PERCENT | EQUAL | NOTEQUAL | LT | GT | LE | GE ;
```

```aura
fn operator +(other: Vec2) -> Vec2 {
    return new Vec2(x: self.x + other.x, y: self.y + other.y)
}
```

### **パイプ演算子 (`|`)**

前の式の結果を次の関数の第一引数として渡します。

```
pipeExpression : lambdaExpression (PIPE lambdaExpression)* ;
```

* `_`: パイプされた値が第一引数でない場合のプレースホルダー。

```aura
// Console.WriteLine(Math.Abs(-10)) と等価
-10 | Math.Abs | Console.WriteLine

// list.Add(item) と等価
item | list.Add(_)
```

### **例外ガード (`~`)**

式ベースの例外処理。`try/catch` に代わる推奨パターン。

```
guardExpression : pipeExpression (TILDE pipeExpression)* ;
```

右辺は `(Exception) -> T` 型の関数でなければなりません。

```aura
// タスクを実行し、失敗時は handle_error にフォールバック
let result = perform_task() ~ handle_error

// インラインラムダハンドラー
let data = parse(input) ~ (e) => default_value
```

> **注意:** `try/catch` は互換性のために引き続きサポートされますが、**非推奨**です（警告 AUR5001）。
> 代わりに `~` を使用してください。

### **制御フロー**

```
ifStatement    : IF expression block (ELSE (ifStatement | block))? ;
forStatement   : FOR identifier IN expression block ;
whileStatement : WHILE expression block ;
returnStatement : RETURN expression? SEMI ;
breakStatement  : BREAK SEMI ;
continueStatement : CONTINUE SEMI ;
```

```aura
if condition { ... } else { ... }
for item in collection { ... }   // C# foreach にマッピング
while condition { ... }
return, break, continue
```

### **Switch 文 & 式**

```
// 文形式
switchStatement : SWITCH (LPAREN expression RPAREN | expression) switchBlock ;
switchLabel     : CASE pattern (WHEN expression)? COLON | DEFAULT COLON ;

// 式形式（値を返す）
switchExpression    : unaryExpression SWITCH switchExpressionBlock ;
switchExpressionArm : pattern (WHEN expression)? FATARROW expression ;
```

```aura
let name = status switch {
    Status.Active => "active",
    Status.Banned when is_admin => "banned (admin)",
    _ => "unknown"
}
```

### **パターンマッチング**

```
primaryPattern
    : UNDERSCORE                                 // 破棄
    | VAR identifier?                            // var バインディング
    | typeReference identifier                   // 型テスト + バインディング
    | typeReference                              // 型テスト
    | (LT | LE | GT | GE) constantExpression    // 関係演算
    | constantExpression                         // 定数
    | LBRACE propertySubpatternList? RBRACE      // プロパティ
    | LBRACK patternList? RBRACK                 // リスト
    ;
patternOr  : patternAnd (OR patternAnd)* ;
patternAnd : patternNot (AND patternNot)* ;
patternNot : NOT patternNot | primaryPattern ;
```

---

## **オブジェクト指向コア**

### **クラス、構造体、トレイト**

```
classDecl  : attributeSection* visibilityModifier? CLASS  identifier typeParameters? (COLON typeList)? classBody ;
structDecl : attributeSection* visibilityModifier? STRUCT identifier typeParameters? (COLON typeList)? classBody ;
traitDecl  : attributeSection* visibilityModifier? TRAIT  identifier traitBody ;

classMember : fieldDecl | propertyDecl | functionDecl | operatorDecl | enumDecl | windowDecl ;
traitMember : functionSignature SEMI ;
```

* `class`: 参照型。
* `struct`: 値型。
* `trait`: インターフェース（関数シグネチャのみ、実装なし）。

### **アクセス修飾子**

厳格な可視性制約。`protected` 修飾子は存在しません。

* `pub`: 公開。
* **デフォルト**: 非公開 / 内部。

### **フィールドとプロパティ**

```
fieldDecl    : visibilityModifier? (LET | VAR) identifier (COLON type)? (ASSIGN expression)? SEMI ;
propertyDecl : visibilityModifier? PROPERTY identifier COLON type propertyAccessorBlock? SEMI ;

accessorDecl
    : GET (FATARROW expression SEMI | block)?
    | SET (FATARROW expression SEMI | block)?
    ;
```

### **厳格なメンバー制約**

* **公開フィールド禁止** (AUR4001): 全てのフィールドは非公開でなければならない。
* **プロパティ必須**: 公開データは `property` を通じて公開しなければならない。
* **型ホワイトリスト** (AUR4002): `pub property` は CTS プリミティブ、`trait`、またはデリゲートのみ公開可能。具象クラスや構造体の公開は厳禁。

```aura
trait ILogger { fn log(msg: string) }

class Service {
    var _count: i32

    pub property count: i32 { get => _count }
    pub property logger: ILogger           // OK: trait
    // pub property impl: FileLogger       // ERROR AUR4002: 具象クラス禁止
}
```

### **列挙型**

```
enumDecl   : visibilityModifier? ENUM identifier enumBody ;
enumBody   : LBRACE enumMember (COMMA enumMember)* COMMA? SEMI? RBRACE ;
enumMember : identifier (ASSIGN expression)? ;
```

```aura
enum Color { Red, Green, Blue }
enum Priority { Low = 0, Medium = 5, High = 10 }
```

---

## **インスタンス化 — ビルダーシステム**

直接の `new` は制限されています。全てのオブジェクト生成は**ビルダーチェーン**を経由します。

```
newExpression
    : NEW typeReference LPAREN argumentList? RPAREN   // 通常の new（コンパイラが制限）
    | NEW LPAREN expression RPAREN                     // ビルダー new: new(builder)
    ;
```

### **ビルダー型（自動インポート）**

| 型 | 役割 |
| :---- | :---- |
| `VoidBuilder` | ブートストラップ — `new VoidBuilder()` で引数なし `new` が許される**唯一の**型 |
| `CLRConstructorArgBuilder` | CLR コンストラクタ引数構築用の抽象基底クラス（継承専用） |
| `CLRExternalTypeBuilder<T>` | リフレクションを使用して CLR 外部型を構築 |
| `IBuilder<T>` | インターフェース: `GetConstructorDictionary() -> Dictionary<string, object>` + `Build(args) -> T` |

### **インスタンス化ルール**

| パターン | 結果 |
| :---- | :---- |
| `new VoidBuilder()` | OK — 唯一許可された引数なし `new` |
| `new MyAuraType(prop: val)` | OK — 名前付き引数によるプロパティ初期化 |
| `new MyAuraType()` | AUR4031 — 引数なし new 禁止 |
| `new SomeCLRType(...)` | AUR4032 — CLR 型はビルダーチェーンを使用する必要がある |
| `new(builder)` | OK — 標準的なビルダーベースのインスタンス化 |

### **ビルダー構文**

```aura
// new(builder) は builder.GetConstructorDictionary() を呼び出し、次に builder.Build(args) を呼び出す
let b = new VoidBuilder()
let obj = new(b)         // デフォルト構築
```

### **CLR 外部型 — フルビルダーチェーン**

CLR 型（`System.*` など）の場合、`CLRConstructorArgBuilder` をサブクラス化してコンストラクタ引数を定義します：

```aura
// 1. 引数ビルダーを定義（CLRConstructorArgBuilder を継承）
class MyFormArgs : CLRConstructorArgBuilder {
    property text: string
    property width: i32
}

// 2. 引数を設定し型ビルダーを作成
let args = new MyFormArgs(text: "Hello", width: 400)
let builder = new CLRExternalTypeBuilder<System.Windows.Forms.Form>(args: args)

// 3. インスタンス化
let form = new(builder)
```

### **[BuildMe] — グローバルサービス登録**

```aura
[BuildMe(builder: MyBuilder, name: "core")]
class User { ... }

// 後から: ハンドルレジストリ経由で取得
let user = Global.getInstance<User>("core")
```

---

## **高度なアーキテクチャ機能**

### **Window（プロジェクション）**

ネイティブかつ厳格に安全なプロジェクションプロキシ。Window は対象クラスの公開メンバーのサブセットでなければなりません。

```
windowDecl      : visibilityModifier? WINDOW identifier COLON typeReference windowBody ;
windowMemberDecl : identifier COLON type SEMI ;
```

```aura
class User { pub property name: string; pub property age: i32 }

window PublicInfo : User {
    name: string
}

fn print(info: PublicInfo) { ... }
```

### **Handle & Decode**

オブジェクトの不透明な整数参照で、安全な分離を保証します。

* `Global.FindObject<T>(handle)`: ハンドルによる検索。
* `self(DecodedHandle)`: クラスがこれを実装し、型付き Window を返す。

```aura
enum AccessLevel { Admin, Guest }

class Data {
    fn self(level: AccessLevel) -> windowof<Data> {
        // アクセスレベルに基づいて Window を返す
    }
}
```

### **Room**

組み込みのメッセージバス＆ブロードキャストシステム。参加するクラスは `IRoomReceiver` を実装する必要があります。

```aura
Room.createRoom("Lobby")
Room["Lobby"].addObject(user)
Room["Lobby"].sendMessage("greet", args)
```

### **Derivable 関数**

構文によってネイティブにサポートされるアスペクト指向のテンプレートメソッド。

```
functionModifier : ASYNC | DERIVABLE ;
opDeclStatement  : OP identifier COLON functionType SEMI ;
```

* `derivable`: 拡張可能な関数を宣言。
* `op`: 内部オペレーター（フック）を宣言。
* `derivateof`: インジェクション用のオペレータータプルを取得。

```aura
derivable fn process() {
    op before: () -> void
    before()
    // ... コアロジック
}
```

### **状態関数**

ネイティブなステートマシンサポート。実装は特定の列挙値にバインドされます。

```
functionReturnOrState
    : THINARROW type         // -> 戻り値型
    | COLON qualifiedName    // : State.Value
    ;
```

```aura
fn run() : State.Idle    { Console.WriteLine("Idling...") }
fn run() : State.Running { Console.WriteLine("Working...") }
```

---

## **データ処理 & コレクション**

### **述語インデクサー**

インデクサー内に統合された LINQ クエリ構文。

* `item`: コレクション内の現在の要素を表すキーワード。

```aura
let list = [1, 2, 3, 4, 5]
let result = list[item > 2 && item < 5]   // IEnumerable<i32> を返す
```

### **リストリテラル**

```
listLiteral : LBRACK (expression (COMMA expression)*)? COMMA? RBRACK ;
```

```aura
let nums = [1, 2, 3]
let empty: List<i32> = []
```

### **文字列補間**

```
interpolatedString : INTERP_START interpolatedStringPart* INTERP_END ;
```

```aura
let name = "World"
let msg = $"Hello, {name}! 2+2={2+2}"
```

### **シリアライゼーション**

```aura
obj.serialize()        // -> string/bytes
T.deserialize(data)    // -> T
```

---

## **式**

### **演算子の優先順位（高い順）**

| レベル | 演算子 | 結合性 |
| :---- | :---- | :---- |
| 単項 | `+x`, `-x`, `!x`, `await x`, `throw x`, `derivateof x` | 右 |
| Switch | `x switch { ... }` | 左 |
| 乗除算 | `*`, `/`, `%` | 左 |
| 加減算 | `+`, `-` | 左 |
| 関係 | `<`, `>`, `<=`, `>=`, `is`, `as` | 左 |
| 等値 | `==`, `!=` | 左 |
| 論理 AND | `&&` | 左 |
| 論理 OR | `\|\|` | 左 |
| Null 合体 | `??` | 右 |
| ラムダ | `(params) => expr` | 右 |
| パイプ | `\|` | 左 |
| ガード | `~` | 左 |
| 三項 | `? :` | 右 |
| 代入 | `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `??=` | 右 |

### **Using 文**

RAII スタイルのリソース管理。

```
usingStatement : AWAIT? USING usingResource (block | SEMI) ;
```

```aura
using (let conn = open_connection()) {
    conn.execute(query)
}
await using stream { ... }
```

---

## **ジェネリクス & 制約**

```
typeParameters : LT typeParameter (COMMA typeParameter)* GT ;
whereClause    : WHERE identifier COLON constraintList ;
typeConstraint : typeReference | NEW LPAREN RPAREN | CLASS | STRUCT ;
```

```aura
fn find<T>(list: List<T>, pred: (T) -> bool) -> T?
    where T : IComparable
{
    for item in list {
        if pred(item) { return item }
    }
    return null
}
```

---

## **C# からの吸収機能**

Aura は以下との完全な互換性を維持しています：

* **ジェネリクス**: `class List<T>`、`fn map<T>(item: T) -> T`
* **名前空間**: `namespace MyProject.Core { ... }`
* **インポート**: `import System`（`using System` に相当）
* **列挙型**: `enum Color { Red, Green, Blue }`
* **属性**: `[AttributeName(Arg=Val)]`
* **リフレクション**: .NET リフレクション機構と完全互換。
* **非同期モデル**: `async` / `await`、`System.Threading.Tasks.Task` 基盤。
* **演算子オーバーロード**: 標準演算子のサポート（`+`, `-`, `*` など）。

---

## **i18n / コンパイラローカライゼーション**

Aura コンパイラは診断メッセージと CLI メッセージを3言語で出力できます。

```bash
aura compile --lang ja samples/main.aura   # 日本語
aura compile --lang zh samples/main.aura   # 中国語
aura compile --lang en samples/main.aura   # 英語（デフォルト）
# または環境変数で設定: AURA_LANG=ja
```

---

## **キーワード一覧**

**Aura 独自:**

`let`, `var`, `fn`, `pub`, `property`, `trait`, `struct`, `class`, `derivable`, `op`, `operator`, `derivateof`, `window`, `windowof`, `item`, `new`（再定義されたセマンティクス）, `handle`, `self`, `serialize`, `deserialize`

**C# から吸収:**

`if`, `else`, `for`, `in`, `while`, `return`, `break`, `continue`, `async`, `await`, `namespace`, `import`, `enum`, `switch`, `case`, `default`, `when`, `using`, `null`, `true`, `false`, `is`, `as`, `throw`, `where`, `get`, `set`

**パターンキーワード:**

`not`, `and`, `or`

**非推奨（コンパイル可能、警告あり）:**

`try`, `catch`, `finally` — 代わりに `~` を使用

---

## **診断コード**

| コード | 重要度 | 説明 |
| :---- | :---- | :---- |
| AUR1010 | エラー | 型/メンバー宣言の重複 |
| AUR1020 | エラー | パラメータ/変数名の重複 |
| AUR1030 | エラー | 関数オーバーロードのシグネチャ競合 |
| AUR2220 | エラー | `async` 関数外での `await` 使用 |
| AUR2321 | エラー | 条件式の分岐型不一致 |
| AUR2510 | 警告 | Switch 分岐型のマージ不可 |
| AUR2511 | エラー | Switch 式が網羅的でない（`_` なし） |
| AUR2630 | エラー | catch 型は Exception の派生型でなければならない |
| AUR2640 | エラー | 戻り値の型不一致 |
| AUR2641 | 警告 | 非 void 関数での値なし return |
| AUR4001 | エラー | 公開フィールド禁止 |
| AUR4002 | エラー | pub property の型がホワイトリスト外 |
| AUR4010 | エラー | トレイトメンバー未実装 |
| AUR4020 | エラー | struct は class を継承できない |
| AUR4031 | エラー | 禁止された `new` 使用法 |
| AUR4032 | エラー | CLR 型はビルダーチェーンを使用する必要がある |
| AUR4050 | エラー | ユーザー定義コンストラクタ禁止 |
| AUR4100 | エラー | Window 検証エラー |
| AUR4200 | エラー | self decode 関数の検証 |
| AUR4300 | エラー | 予約済み `item` キーワードの誤用 |
| AUR4400 | エラー | ビット演算子非サポート |
| AUR5001 | 警告 | try/catch 非推奨、`~` を使用 |

---

## **例: Hello Aura**

```aura
import System

// 1. トレイト定義
trait IGreeter {
    fn say_hello()
}

// 2. Robot 用引数ビルダー
class RobotArgs : CLRConstructorArgBuilder {
    property name: string
}

// 3. 実装クラス（[BuildMe] で登録）
[BuildMe(builder: DefaultBuilder, name: "greeter")]
class Robot : IGreeter, IRoomReceiver {

    var _name: string

    pub property state: RobotState

    fn run() : RobotState.Idle    { Console.WriteLine("Idling...") }
    fn run() : RobotState.Working { Console.WriteLine("Working...") }

    fn say_hello() {
        "Hello from Aura!" | Console.WriteLine
    }

    fn OnMessage(msg: string, args: object) {
        if msg == "wakeup" { self.state = RobotState.Working }
    }
}

// 4. プロジェクション Window
window PublicView : Robot {
    state: RobotState
}

// 5. エントリーポイント
pub fn main() {
    // ビルダーベースの構築
    let vb      = new VoidBuilder()
    let builder = new CLRExternalTypeBuilder<Robot>(args: new RobotArgs(name: "R1"))
    let bot     = new(builder)

    // パイプ実行 + 例外ガード (~)
    bot.say_hello() ~ (e) => Console.WriteLine($"Error: {e.Message}")

    // 述語インデクサー
    let robots = [bot]
    let working = robots[item.state == RobotState.Working]
}
```
