# **Aura Programming Language Specification (v1.0)**

**Target Platform:** .NET 10+ (CLI / CTS)

**Core Philosophy:** Architecture constraint as syntax, zero implicit side effects, deep decoupling, and defensive programming.

## **Basics & Types**

Aura uses postfix type declarations and maps its fundamental types directly to the .NET Common Type System (CTS).

### **Variable Declarations**

* let: Immutable binding (maps to readonly).  
* var: Mutable binding.  
* Supports type inference.

let pi: f64 \= 3.14159  
var count \= 0           // Inferred as i32  
var name: string? \= null // Nullable reference type

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

// Single-line comment  
/\* Multi-line comment \*/  
/// Documentation comment (Generates XML)

## **Functions & Flow Control**

Functions are first-class citizens, mapped underlyingly to System.Delegate, Func\<T\>, or Action\<T\>.

### **Function Declarations**

// Standard function  
fn add(a: i32, b: i32) \-\> i32 {  
    return a \+ b  
}

// Async function (Absorbed from C\# Task model)  
async fn fetch\_data(url: string) \-\> string {  
    let data \= await Client.Get(url)  
    return data  
}

// Expression body  
fn square(x: i32) \-\> i32 \=\> x \* x

### **Pipe Operator (|)**

Passes the result of the preceding expression as the first argument to the next function.

* \_: Placeholder used when the piped value is not the first argument.

// Equivalent to: Console.WriteLine(Math.Abs(-10))  
\-10 | Math.Abs | Console.WriteLine

// Equivalent to: list.Add(item)  
item | list.Add(\_)

### **Exception Guard (\~)**

Expression-based exception handling. The right side must be a function of type (Exception) \-\> T.

// Attempts to execute the task; falls back to handle\_error on failure  
let result \= perform\_task() \~ handle\_error 

### **Control Flow**

Aura absorbs C\# control flow structures but eliminates the requirement for redundant parentheses.

* if condition { ... } else { ... }  
* for item in collection { ... } (Maps to C\# foreach)  
* while condition { ... }  
* return, break, continue

## **Object-Oriented Core**

### **Classes and Traits**

* class: Reference type.  
* struct: Value type.  
* trait: Interface.

### **Access Modifiers**

Strict visibility constraints. The protected modifier does not exist.

* pub: Public.  
* **Default**: Private / Internal.

### **Strict Member Constraints**

* **No Public Fields**: All fields must be private.  
* **Mandatory Properties**: Public data must be exposed via property.  
* **Type Whitelist**: A pub property can only expose a CTS Primitive, a trait, or a Delegate (function). Exposing concrete classes or structs is strictly forbidden.

trait ILogger { fn log(msg: string) }

class Service {  
    // Private field  
    var \_count: i32   
      
    // Public properties (Compliant types)  
    pub property count: i32 { get \=\> \_count }  
    pub property logger: ILogger // Correct: Trait  
    // pub property log\_impl: FileLogger // ERROR: Concrete class  
}

### **Instantiation constraints**

Defining a new() constructor is forbidden. Instantiation must occur via an IBuilder interface. Aura utilizes the \[BuildMe\] attribute for global service registration.

// Automatic injection registration  
\[BuildMe(builder: MyBuilder, name: "core")\]  
class User { ... }

// Instantiation  
let user \= new User(builder\_instance)

## **Advanced Architectural Features**

### **Window (Projection)**

A native, strictly-safe projection proxy. A window must be a subset of the target class's public members.

class User { pub property name: string; pub property age: i32 }

// Define a projection view  
window PublicInfo : User {  
    name: string  
}

// Usage  
fn print(info: PublicInfo) { ... }

### **Handle & Decode**

Opaque integer references for objects, ensuring secure isolation.

* Global.FindObject\<T\>(handle): Lookup via handle.  
* self(DecodedHandle): Classes must implement this function to return a specific window based on a permission enum.

enum AccessLevel { Admin, Guest }

class Data {  
    fn self(level: AccessLevel) \-\> windowof\<Data\> {  
        // Return corresponding window based on access level  
    }  
}

### **Room**

A built-in message bus and broadcasting system. Classes must implement IRoomReceiver to participate.

Room.createRoom("Lobby")  
Room\["Lobby"\].addObject(user)  
Room\["Lobby"\].sendMessage("greet", args)

### **Derivable Functions**

Aspect-oriented template methods natively supported by the syntax.

* derivable: Declares an extensible function.  
* op: Declares an internal operator (hook).  
* derivateof: Retrieves operator tuples for injection.

derivable fn process() {  
    op before: () \-\> void  
    before()  
    // ... core logic  
}

### **State Functions**

Native state machine support. Implementations are bound directly to specific enum values. State transitions occur seamlessly when the enum property changes.

fn run() : State.Idle { ... }  
fn run() : State.Running { ... }

## **Data Processing & Collections**

### **Predicate Indexer**

Integrated LINQ query syntax directly within indexers.

* item: Keyword representing the current element in the collection.

let list \= \[1, 2, 3, 4, 5\]  
let result \= list\[item \> 2 && item \< 5\] // Returns IEnumerable\<i32\>

### **Serialization**

Native support for object state snapshots and restoration.

* obj.serialize() \-\> string/bytes  
* T.deserialize(data) \-\> T

## **Absorbed C\# Features**

To maximize productivity, Aura maintains full compatibility with the following C\# features:

* **Generics**: class List\<T\>, fn map\<T\>(item: T) \-\> T  
* **Namespaces**: namespace MyProject.Core { ... }  
* **Imports**: import System (Equivalent to using System)  
* **Enums**: enum Color { Red, Green, Blue }  
* **Attributes**: \[AttributeName(Arg=Val)\]  
* **Reflection**: Fully compatible with the .NET reflection mechanism.  
* **Async Model**: async / await backed by System.Threading.Tasks.Task.  
* **Operator Overloading**: Support for standard operators (+, \-, \*, etc.).

## **Keywords Table**

**Aura Specific:**

let, var, fn, pub, property, trait, struct, class, derivable, op, derivateof, window, windowof, item, new (redefined semantics), handle, self, serialize, deserialize

**Absorbed from C\#:**

if, else, for, while, return, break, continue, async, await, namespace, import, enum, null, true, false, is, as, throw, try (deprecated by \~ but supported), catch

## **Example: Hello Aura**

import System

// 1\. Define Trait  
trait IGreeter {  
    fn say\_hello()  
}

// 2\. Implementation Class (No constructors, registered via \[BuildMe\])  
\[BuildMe(builder: DefaultBuilder, name: "greeter")\]  
class Robot : IGreeter, IRoomReceiver {  
      
    // Private state  
    var \_name: string  
      
    // State signal  
    pub property state: RobotState

    // State Function Implementations  
    fn run() : RobotState.Idle { Console.WriteLine("Idling...") }  
    fn run() : RobotState.Working { Console.WriteLine("Working...") }

    // Trait Implementation  
    fn say\_hello() {  
        "Hello from Aura\!" | Console.WriteLine  
    }

    // Room message receiver  
    fn receiveRoomMessage(msg: string, args: MsgArgsBase) {  
        if msg \== "wakeup" { self.state \= RobotState.Working }  
    }  
}

// 3\. Projection Window  
window PublicView : Robot {  
    state: RobotState  
}

// 4\. Entry Point  
fn main() {  
    // Construction  
    let bot \= Global.getInstance\<Robot\>("greeter")  
      
    // Piped execution with exception guard  
    bot.say\_hello() \~ (e) \=\> Console.WriteLine($"Error: {e.Message}")  
      
    // Predicate indexer demonstration  
    let robots \= \[bot\]  
    let working\_bots \= robots\[item.state \== RobotState.Working\]  
}  
