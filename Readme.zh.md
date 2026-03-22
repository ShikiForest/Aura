# **Aura 编程语言规范 (v1.2)**

[English](Readme.md) | [日本語](Readme.ja.md) | 中文

**目标平台:** .NET 10+ (CLI / CTS)

**核心理念:** 以语法实施架构约束、零隐式副作用、深度解耦、防御性编程。

---

### 实现状态说明

各章节标注了当前的实现状态：

| 标记 | 含义 |
| :---- | :---- |
| :white_check_mark: **已实现** | 已完全实现并通过测试 |
| :wrench: **部分实现** | 已解析/分析但代码生成不完整 |
| :clipboard: **计划中** | 语法已定义但尚未实现 |

---

## **基础 & 类型** :white_check_mark:

Aura 采用后缀类型声明，将基本类型直接映射到 .NET 公共类型系统 (CTS)。

### **变量声明**

* `let`: 不可变绑定（映射为 `readonly`）。
* `var`: 可变绑定。
* 支持类型推断 — 类型注解 `: type` 可省略。

```
variableDecl : (LET | VAR) identifier (COLON type)? (ASSIGN expression)? SEMI ;
```

```aura
let pi: f64 = 3.14159
var count = 0           // 推断为 i32
var name: string? = null // 可空引用类型
```

### **基本类型映射**

| Aura 类型 | .NET CTS | 说明 |
| :---- | :---- | :---- |
| `i8`, `i16`, `i32`, `i64` | SByte, Int16, Int32, Int64 | 有符号整数 |
| `u8`, `u16`, `u32`, `u64` | Byte, UInt16, UInt32, UInt64 | 无符号整数 |
| `f32`, `f64` | Single, Double | 浮点数 |
| `decimal` | System.Decimal | 高精度十进制 |
| `bool` | System.Boolean | 布尔值 |
| `char` | System.Char | Unicode 字符 |
| `string` | System.String | 不可变字符串 |
| `object` | System.Object | 根基类型 |
| `void` | System.Void | 无返回值 |
| `handle` | System.Int32 | 不透明对象句柄 |

### **类型系统**

```
type
    : functionType nullableSuffix?    // (i32, i32) -> bool
    | windowOfType nullableSuffix?    // windowof<T>
    | namedType    nullableSuffix?    // QualifiedName<TypeArgs>?
    ;
```

* **可空后缀**: 任何类型后加 `?` 即为可空类型。
* **函数类型**: `(参数类型) -> 返回类型`。
* **Window-of 类型**: `windowof<T>` — 投影类型引用。

### **注释**

```aura
// 单行注释
/* 多行注释 */
/// 文档注释（生成 XML）
```

---

## **函数 & 控制流** :white_check_mark:

函数是一等公民，底层映射为 `System.Delegate`、`Func<T>` 或 `Action<T>`。

### **函数声明**

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
// 标准函数
fn add(a: i32, b: i32) -> i32 {
    return a + b
}

// 异步函数（基于 C# Task 模型）
async fn fetch_data(url: string) -> string {
    let data = await Client.Get(url)
    return data
}

// 表达式体
fn square(x: i32) -> i32 => x * x
```

### **运算符重载** :white_check_mark:

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

### **管道运算符 (`|`)** :white_check_mark:

将前一个表达式的结果作为下一个函数的第一参数传入。

```
pipeExpression : lambdaExpression (PIPE lambdaExpression)* ;
```

* `_`: 当管道值不是第一参数时使用的占位符。

```aura
// 等价于: Console.WriteLine(Math.Abs(-10))
-10 | Math.Abs | Console.WriteLine

// 等价于: list.Add(item)
item | list.Add(_)
```

### **异常守护 (`~`)** :white_check_mark:

基于表达式的异常处理。替代 `try/catch` 的推荐模式。

```
guardExpression : pipeExpression (TILDE pipeExpression)* ;
```

右侧必须是 `(Exception) -> T` 类型的函数。

```aura
// 尝试执行任务；失败时回退到 handle_error
let result = perform_task() ~ handle_error

// 内联 lambda 处理器
let data = parse(input) ~ (e) => default_value
```

> **注意:** `try/catch` 仍为兼容性而保留，但已**弃用**（警告 AUR5001）。
> 请使用 `~` 代替。

### **控制流**

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
for item in collection { ... }   // 映射为 C# foreach
while condition { ... }
return, break, continue
```

### **Switch 语句 & 表达式** :wrench:

```
// 语句形式
switchStatement : SWITCH (LPAREN expression RPAREN | expression) switchBlock ;
switchLabel     : CASE pattern (WHEN expression)? COLON | DEFAULT COLON ;

// 表达式形式（产生值）
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

> :wrench: **注意:** switch **表达式** 已完全实现（转换为条件表达式）。switch **语句** (`switch(x) { case ... }`) 已完成解析和验证，但代码生成尚未完成。

### **模式匹配** :white_check_mark:

```
primaryPattern
    : UNDERSCORE                                 // 丢弃
    | VAR identifier?                            // var 绑定
    | typeReference identifier                   // 类型测试 + 绑定
    | typeReference                              // 类型测试
    | (LT | LE | GT | GE) constantExpression    // 关系运算
    | constantExpression                         // 常量
    | LBRACE propertySubpatternList? RBRACE      // 属性
    | LBRACK patternList? RBRACK                 // 列表
    ;
patternOr  : patternAnd (OR patternAnd)* ;
patternAnd : patternNot (AND patternNot)* ;
patternNot : NOT patternNot | primaryPattern ;
```

---

## **面向对象核心** :white_check_mark:

### **类、结构体、特征**

```
classDecl  : attributeSection* visibilityModifier? CLASS  identifier typeParameters? (COLON typeList)? classBody ;
structDecl : attributeSection* visibilityModifier? STRUCT identifier typeParameters? (COLON typeList)? classBody ;
traitDecl  : attributeSection* visibilityModifier? TRAIT  identifier traitBody ;

classMember : fieldDecl | propertyDecl | functionDecl | operatorDecl | enumDecl | windowDecl ;
traitMember : functionSignature SEMI ;
```

* `class`: 引用类型。
* `struct`: 值类型。
* `trait`: 接口（仅函数签名，无实现）。

### **访问修饰符**

严格的可见性约束。不存在 `protected` 修饰符。

* `pub`: 公开。
* **默认**: 私有 / 内部。

### **字段与属性**

```
fieldDecl    : visibilityModifier? (LET | VAR) identifier (COLON type)? (ASSIGN expression)? SEMI ;
propertyDecl : visibilityModifier? PROPERTY identifier COLON type propertyAccessorBlock? SEMI ;

accessorDecl
    : GET (FATARROW expression SEMI | block)?
    | SET (FATARROW expression SEMI | block)?
    ;
```

### **严格成员约束**

* **禁止公开字段** (AUR4001): 所有字段必须为私有。
* **属性必需**: 公开数据必须通过 `property` 暴露。
* **类型白名单** (AUR4002): `pub property` 只能暴露 CTS 基本类型、`trait` 或委托。严禁暴露具体类或结构体。

```aura
trait ILogger { fn log(msg: string) }

class Service {
    var _count: i32

    pub property count: i32 { get => _count }
    pub property logger: ILogger           // OK: trait
    // pub property impl: FileLogger       // ERROR AUR4002: 禁止具体类
}
```

### **枚举**

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

## **实例化 — 构建器系统** :white_check_mark:

直接使用 `new` 受到限制。所有对象创建都通过**构建器链**。

```
newExpression
    : NEW typeReference LPAREN argumentList? RPAREN   // 普通 new（编译器限制）
    | NEW LPAREN expression RPAREN                     // 构建器 new: new(builder)
    ;
```

### **构建器类型（自动导入）**

| 类型 | 角色 |
| :---- | :---- |
| `VoidBuilder` | 引导 — `new VoidBuilder()` 是**唯一**允许无参 `new` 的类型 |
| `CLRConstructorArgBuilder` | CLR 构造函数参数构建的抽象基类（仅供继承） |
| `CLRExternalTypeBuilder<T>` | 使用反射构建 CLR 外部类型 |
| `IBuilder<T>` | 接口: `GetConstructorDictionary() -> Dictionary<string, object>` + `Build(args) -> T` |

### **实例化规则**

| 模式 | 结果 |
| :---- | :---- |
| `new VoidBuilder()` | OK — 唯一允许的无参 `new` |
| `new MyAuraType(prop: val)` | OK — 命名参数属性初始化 |
| `new MyAuraType()` | AUR4031 — 禁止无参 new |
| `new SomeCLRType(...)` | AUR4032 — CLR 类型必须使用构建器链 |
| `new(builder)` | OK — 标准的基于构建器的实例化 |

### **构建器语法**

```aura
// new(builder) 调用 builder.GetConstructorDictionary() 然后调用 builder.Build(args)
let b = new VoidBuilder()
let obj = new(b)         // 默认构造
```

### **CLR 外部类型 — 完整构建器链**

对于 CLR 类型（`System.*` 等），通过继承 `CLRConstructorArgBuilder` 来定义构造函数参数：

```aura
// 1. 定义参数构建器（继承 CLRConstructorArgBuilder）
class MyFormArgs : CLRConstructorArgBuilder {
    property text: string
    property width: i32
}

// 2. 填充参数并创建类型构建器
let args = new MyFormArgs(text: "Hello", width: 400)
let builder = new CLRExternalTypeBuilder<System.Windows.Forms.Form>(args: args)

// 3. 实例化
let form = new(builder)
```

### **[BuildMe] — 全局服务注册**

```aura
[BuildMe(builder: MyBuilder, name: "core")]
class User { ... }

// 之后: 通过句柄注册表获取
let user = Global.getInstance<User>("core")
```

---

## **高级架构特性**

### **Window（投影）** :wrench:

原生的、严格安全的投影代理。Window 必须是目标类公开成员的子集。

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

### **Handle & Decode** :wrench:

对象的不透明整数引用，确保安全隔离。

* `Global.FindObject<T>(handle)`: 通过句柄查找。
* `self(DecodedHandle)`: 类实现此方法返回类型化的 Window。

```aura
enum AccessLevel { Admin, Guest }

class Data {
    fn self(level: AccessLevel) -> windowof<Data> {
        // 根据访问级别返回 Window
    }
}
```

### **Room** :white_check_mark:

内置的消息总线和广播系统。参与的类必须实现 `IRoomReceiver`。

```aura
Room.createRoom("Lobby")
Room["Lobby"].addObject(user)
Room["Lobby"].sendMessage("greet", args)
```

### **Derivable 函数** :white_check_mark:

语法原生支持的面向切面模板方法。

```
functionModifier : ASYNC | DERIVABLE ;
opDeclStatement  : OP identifier COLON functionType SEMI ;
```

* `derivable`: 声明可扩展的函数。
* `op`: 声明内部运算符（钩子）。
* `derivateof`: 获取运算符元组用于注入。

```aura
derivable fn process() {
    op before: () -> void
    before()
    // ... 核心逻辑
}
```

### **状态函数** :white_check_mark:

原生状态机支持。实现绑定到特定的枚举值。

```
functionReturnOrState
    : THINARROW type         // -> 返回类型
    | COLON qualifiedName    // : State.Value
    ;
```

```aura
fn run() : State.Idle    { Console.WriteLine("Idling...") }
fn run() : State.Running { Console.WriteLine("Working...") }
```

---

## **数据处理 & 集合** :white_check_mark:

### **谓词索引器**

在索引器中集成 LINQ 查询语法。

* `item`: 表示集合中当前元素的关键字。

```aura
let list = [1, 2, 3, 4, 5]
let result = list[item > 2 && item < 5]   // 返回 IEnumerable<i32>
```

### **列表字面量**

```
listLiteral : LBRACK (expression (COMMA expression)*)? COMMA? RBRACK ;
```

```aura
let nums = [1, 2, 3]
let empty: List<i32> = []
```

### **字符串插值**

```
interpolatedString : INTERP_START interpolatedStringPart* INTERP_END ;
```

```aura
let name = "World"
let msg = $"Hello, {name}! 2+2={2+2}"
```

### **序列化** :clipboard:

```aura
obj.serialize()        // -> string/bytes
T.deserialize(data)    // -> T
```

---

## **表达式**

### **运算符优先级（由高到低）**

| 级别 | 运算符 | 结合性 |
| :---- | :---- | :---- |
| 一元 | `+x`, `-x`, `!x`, `await x`, `throw x`, `derivateof x` | 右 |
| Switch | `x switch { ... }` | 左 |
| 乘除 | `*`, `/`, `%` | 左 |
| 加减 | `+`, `-` | 左 |
| 关系 | `<`, `>`, `<=`, `>=`, `is`, `as` | 左 |
| 等值 | `==`, `!=` | 左 |
| 逻辑与 | `&&` | 左 |
| 逻辑或 | `\|\|` | 左 |
| 空合并 | `??` | 右 |
| Lambda | `(params) => expr` | 右 |
| 管道 | `\|` | 左 |
| 守护 | `~` | 左 |
| 三元 | `? :` | 右 |
| 赋值 | `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `??=` | 右 |

### **Using 语句**

RAII 风格的资源管理。

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

## **泛型 & 约束** :wrench:

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

> :wrench: **注意:** 泛型已完全支持，但 `where` 约束仅完成了解析。使用 `where` 子句目前会产生 AUR5002 错误。CLR 泛型参数约束的生成正在计划中。

---

## **从 C# 吸收的特性**

Aura 保持与以下特性的完全兼容：

* **泛型**: `class List<T>`、`fn map<T>(item: T) -> T`
* **命名空间**: `namespace MyProject.Core { ... }`
* **导入**: `import System`（等价于 `using System`）
* **枚举**: `enum Color { Red, Green, Blue }`
* **特性标注**: `[AttributeName(Arg=Val)]`
* **反射**: 与 .NET 反射机制完全兼容。
* **异步模型**: `async` / `await`，基于 `System.Threading.Tasks.Task`。
* **运算符重载**: 支持标准运算符（`+`, `-`, `*` 等）。

---

## **i18n / 编译器本地化** :white_check_mark:

Aura 编译器支持三种语言的诊断和 CLI 消息输出。

```bash
aura compile --lang ja samples/main.aura   # 日语
aura compile --lang zh samples/main.aura   # 中文
aura compile --lang en samples/main.aura   # 英语（默认）
# 或设置环境变量: AURA_LANG=ja
```

---

## **关键字表**

**Aura 独有:**

`let`, `var`, `fn`, `pub`, `property`, `trait`, `struct`, `class`, `derivable`, `op`, `operator`, `derivateof`, `window`, `windowof`, `item`, `new`（重定义语义）, `handle`, `self`, `serialize`, `deserialize`

**从 C# 吸收:**

`if`, `else`, `for`, `in`, `while`, `return`, `break`, `continue`, `async`, `await`, `namespace`, `import`, `enum`, `switch`, `case`, `default`, `when`, `using`, `null`, `true`, `false`, `is`, `as`, `throw`, `where`, `get`, `set`

**模式关键字:**

`not`, `and`, `or`

**已弃用（仍可编译，会发出警告）:**

`try`, `catch`, `finally` — 请使用 `~` 代替

---

## **诊断代码**

| 代码 | 严重性 | 说明 |
| :---- | :---- | :---- |
| AUR1010 | 错误 | 类型/成员声明重复 |
| AUR1020 | 错误 | 参数/变量名重复 |
| AUR1030 | 错误 | 函数重载签名冲突 |
| AUR2220 | 错误 | 在 `async` 函数外使用 `await` |
| AUR2321 | 错误 | 条件表达式分支类型不匹配 |
| AUR2510 | 警告 | Switch 分支类型无法合并 |
| AUR2511 | 错误 | Switch 表达式不穷尽（缺少 `_`） |
| AUR2630 | 错误 | catch 类型必须派生自 Exception |
| AUR2640 | 错误 | 返回类型不匹配 |
| AUR2641 | 警告 | 非 void 函数中的无值返回 |
| AUR4001 | 错误 | 禁止公开字段 |
| AUR4002 | 错误 | pub property 类型不在白名单内 |
| AUR4010 | 错误 | 特征成员未实现 |
| AUR4020 | 错误 | struct 不能继承 class |
| AUR4031 | 错误 | 禁止的 `new` 用法 |
| AUR4032 | 错误 | CLR 类型必须使用构建器链 |
| AUR4050 | 错误 | 禁止用户定义构造函数 |
| AUR4100 | 错误 | Window 验证错误 |
| AUR4200 | 错误 | self decode 函数验证 |
| AUR4300 | 错误 | 保留关键字 `item` 误用 |
| AUR4400 | 错误 | 不支持位运算符 |
| AUR5001 | 警告 | try/catch 已弃用，请使用 `~` |

---

## **示例: Hello Aura**

```aura
import System

// 1. 定义特征
trait IGreeter {
    fn say_hello()
}

// 2. Robot 的参数构建器
class RobotArgs : CLRConstructorArgBuilder {
    property name: string
}

// 3. 实现类（通过 [BuildMe] 注册）
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

// 4. 投影 Window
window PublicView : Robot {
    state: RobotState
}

// 5. 入口点
pub fn main() {
    // 基于构建器的构造
    let vb      = new VoidBuilder()
    let builder = new CLRExternalTypeBuilder<Robot>(args: new RobotArgs(name: "R1"))
    let bot     = new(builder)

    // 管道执行 + 异常守护 (~)
    bot.say_hello() ~ (e) => Console.WriteLine($"Error: {e.Message}")

    // 谓词索引器
    let robots = [bot]
    let working = robots[item.state == RobotState.Working]
}
```
