# **Aura Programming Language Specification (v1.1)**

**Target Platform:** .NET 10+ (CLI / CTS)

**Core Philosophy:** Architecture constraint as syntax, zero implicit side effects, deep decoupling, and defensive programming.

---

## **Basics & Types**

Aura uses postfix type declarations and maps its fundamental types directly to the .NET Common Type System (CTS).

### **Variable Declarations**

* `let`: Immutable binding (maps to `readonly`).
* `var`: Mutable binding.
* Supports type inference.

```aura
let pi: f64 = 3.14159
var count = 0           // Inferred as i32
var name: string? = null // Nullable reference type
```

### **Primitive Type Mapping**

| Aura Type | .NET CTS | Description |
| :---- | :---- | :---- |
| i8, i16, i32, i64 | SByte, Int16, Int32, Int64 | Signed integers |
| u8, u16, u32, u64 | Byte, UInt16, UInt32, UInt64 | Unsigned integers |
| f32, f64 | Single, Double | Floating-point numbers |
| decimal | System.Decimal | High-precision decimal |
| bool | System.Boolean | Boolean value |
| char | System.Char | Unicode character |
| string | System.String | Immutable string |
| object | System.Object | Root base type |

### **Comments**

```aura
// Single-line comment
/* Multi-line comment */
/// Documentation comment (Generates XML)
```

---

## **Functions & Flow Control**

Functions are first-class citizens, mapped underlyingly to `System.Delegate`, `Func<T>`, or `Action<T>`.

### **Function Declarations**

```aura
// Standard function
fn add(a: i32, b: i32) -> i32 {
    return a + b
}

// Async function (backed by C# Task model)
async fn fetch_data(url: string) -> string {
    let data = await Client.Get(url)
    return data
}

// Expression body
fn square(x: i32) -> i32 => x * x
```

### **Pipe Operator (|)**

Passes the result of the preceding expression as the first argument to the next function.

* `_`: Placeholder used when the piped value is not the first argument.

```aura
// Equivalent to: Console.WriteLine(Math.Abs(-10))
-10 | Math.Abs | Console.WriteLine

// Equivalent to: list.Add(item)
item | list.Add(_)
```

### **Exception Guard (~)**

Expression-based exception handling. Replaces `try/catch` as the recommended pattern.
The right side must be a function of type `(Exception) -> T`.

```aura
// Attempts to execute the task; falls back to handle_error on failure
let result = perform_task() ~ handle_error
```

> **Note:** `try/catch` is still supported for compatibility but is **deprecated** (warning AUR5001).
> Use `~` instead.

### **Control Flow**

```aura
if condition { ... } else { ... }
for item in collection { ... }   // Maps to C# foreach
while condition { ... }
return, break, continue
```

---

## **Object-Oriented Core**

### **Classes and Traits**

* `class`: Reference type.
* `struct`: Value type.
* `trait`: Interface.

### **Access Modifiers**

Strict visibility constraints. The `protected` modifier does not exist.

* `pub`: Public.
* **Default**: Private / Internal.

### **Strict Member Constraints**

* **No Public Fields**: All fields must be private.
* **Mandatory Properties**: Public data must be exposed via `property`.
* **Type Whitelist**: A `pub property` can only expose a CTS Primitive, a `trait`, or a Delegate. Exposing concrete classes or structs is strictly forbidden.

```aura
trait ILogger { fn log(msg: string) }

class Service {
    var _count: i32

    pub property count: i32 { get => _count }
    pub property logger: ILogger           // OK: trait
    // pub property impl: FileLogger       // ERROR AUR4002: concrete class forbidden
}
```

---

## **Instantiation — Builder System**

Direct `new` is restricted. All object creation goes through the **builder chain**.

### **Builder Types (auto-imported)**

| Type | Role |
| :---- | :---- |
| `VoidBuilder` | Bootstrap — the **only** type that can `new VoidBuilder()` with zero args |
| `CLRConstructorArgBuilder` | Abstract base for building CLR constructor arguments (designed for inheritance) |
| `CLRExternalTypeBuilder<T>` | Builds CLR external types using reflection |
| `IBuilder<T>` | Interface: `GetConstructorDictionary() -> Dictionary<string, object>` + `Build(args) -> T` |

### **Instantiation Rules**

| Pattern | Result |
| :---- | :---- |
| `new VoidBuilder()` | ✅ OK — the only allowed zero-arg `new` |
| `new MyAuraType(prop: val)` | ✅ OK — named-arg property initialisation |
| `new MyAuraType()` | ❌ AUR4031 — zero-arg new forbidden |
| `new SomeCLRType(...)` | ❌ AUR4032 — CLR types must use builder chain |
| `new(builder)` | ✅ OK — canonical builder-based instantiation |

### **Builder Syntax**

```aura
// new(builder) calls builder.GetConstructorDictionary() then builder.Build(args)
let b = new VoidBuilder()
let obj = new(b)         // default construction
```

### **CLR External Type — Full Builder Chain**

For CLR types (`System.*` etc.), define constructor args by subclassing `CLRConstructorArgBuilder`:

```aura
// 1. Define arg builder (inherits from CLRConstructorArgBuilder)
class MyFormArgs : CLRConstructorArgBuilder {
    property text: string
    property width: i32
}

// 2. Fill args and create type builder
let args = new MyFormArgs(text: "Hello", width: 400)
let builder = new CLRExternalTypeBuilder<System.Windows.Forms.Form>(args: args)

// 3. Instantiate
let form = new(builder)
```

`CLRConstructorArgBuilder.GetConstructorDictionary()` scans the subclass's properties and populates the internal `Args` dictionary automatically.

### **[BuildMe] — Global Service Registration**

```aura
[BuildMe(builder: MyBuilder, name: "core")]
class User { ... }

// Later: retrieve via handle registry
let user = Global.getInstance<User>("core")
```

---

## **Advanced Architectural Features**

### **Window (Projection)**

A native, strictly-safe projection proxy. A window must be a subset of the target class's public members.

```aura
class User { pub property name: string; pub property age: i32 }

window PublicInfo : User {
    name: string
}

fn print(info: PublicInfo) { ... }
```

### **Handle & Decode**

Opaque integer references for objects, ensuring secure isolation.

* `Global.FindObject<T>(handle)`: Lookup via handle.
* `self(DecodedHandle)`: Classes implement this to return a typed window.

```aura
enum AccessLevel { Admin, Guest }

class Data {
    fn self(level: AccessLevel) -> windowof<Data> {
        // Return window based on access level
    }
}
```

### **Room**

A built-in message bus and broadcasting system. Classes must implement `IRoomReceiver` to participate.

```aura
Room.createRoom("Lobby")
Room["Lobby"].addObject(user)
Room["Lobby"].sendMessage("greet", args)
```

### **Derivable Functions**

Aspect-oriented template methods natively supported by the syntax.

* `derivable`: Declares an extensible function.
* `op`: Declares an internal operator (hook).
* `derivateof`: Retrieves operator tuples for injection.

```aura
derivable fn process() {
    op before: () -> void
    before()
    // ... core logic
}
```

### **State Functions**

Native state machine support. Implementations are bound to specific enum values.

```aura
fn run() : State.Idle    { Console.WriteLine("Idling...") }
fn run() : State.Running { Console.WriteLine("Working...") }
```

---

## **Data Processing & Collections**

### **Predicate Indexer**

Integrated LINQ query syntax directly within indexers.

* `item`: Keyword representing the current element in the collection.

```aura
let list = [1, 2, 3, 4, 5]
let result = list[item > 2 && item < 5]   // Returns IEnumerable<i32>
```

### **Serialization**

```aura
obj.serialize()        // -> string/bytes
T.deserialize(data)    // -> T
```

---

## **Absorbed C# Features**

Aura maintains full compatibility with:

* **Generics**: `class List<T>`, `fn map<T>(item: T) -> T`
* **Namespaces**: `namespace MyProject.Core { ... }`
* **Imports**: `import System` (equivalent to `using System`)
* **Enums**: `enum Color { Red, Green, Blue }`
* **Attributes**: `[AttributeName(Arg=Val)]`
* **Reflection**: Fully compatible with the .NET reflection mechanism.
* **Async Model**: `async` / `await` backed by `System.Threading.Tasks.Task`.
* **Operator Overloading**: Support for standard operators (`+`, `-`, `*`, etc.).

---

## **i18n / Compiler Localization**

The Aura compiler supports three output languages for diagnostics and CLI messages.

```bash
aura compile --lang ja samples/main.aura   # Japanese
aura compile --lang zh samples/main.aura   # Chinese
aura compile --lang en samples/main.aura   # English (default)
# Or set environment variable: AURA_LANG=ja
```

---

## **Keywords Table**

**Aura Specific:**

`let`, `var`, `fn`, `pub`, `property`, `trait`, `struct`, `class`, `derivable`, `op`, `derivateof`, `window`, `windowof`, `item`, `new` (redefined semantics), `handle`, `self`, `serialize`, `deserialize`

**Absorbed from C#:**

`if`, `else`, `for`, `while`, `return`, `break`, `continue`, `async`, `await`, `namespace`, `import`, `enum`, `null`, `true`, `false`, `is`, `as`

**Deprecated (still compiled, warning emitted):**

`try`, `catch` → use `~` instead

---

## **Example: Hello Aura**

```aura
import System

// 1. Define Trait
trait IGreeter {
    fn say_hello()
}

// 2. Arg builder for Robot
class RobotArgs : CLRConstructorArgBuilder {
    property name: string
}

// 3. Implementation Class (registered via [BuildMe])
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

// 4. Projection Window
window PublicView : Robot {
    state: RobotState
}

// 5. Entry Point
pub fn main() {
    // Builder-based construction
    let vb      = new VoidBuilder()
    let builder = new CLRExternalTypeBuilder<Robot>(args: new RobotArgs(name: "R1"))
    let bot     = new(builder)

    // Piped execution with exception guard (~)
    bot.say_hello() ~ (e) => Console.WriteLine($"Error: {e.Message}")

    // Predicate indexer
    let robots = [bot]
    let working = robots[item.state == RobotState.Working]
}
```
