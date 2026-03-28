namespace Aura;

/// <summary>
/// Base class for types that are allowed as public function parameters and return values.
/// Any class or struct that inherits from FuncArgsBase is whitelisted for use
/// in pub function signatures (AUR4003/AUR4004 type restrictions).
/// </summary>
/// <example>
/// <code>
/// // Aura usage:
/// class UserQuery : FuncArgsBase {
///     pub prop name: string { get }
///     pub prop age: i32 { get }
/// }
///
/// class UserService {
///     pub fn find(query: UserQuery) -> string {  // OK: UserQuery extends FuncArgsBase
///         // ...
///     }
/// }
/// </code>
/// </example>
public abstract class FuncArgsBase
{
}
