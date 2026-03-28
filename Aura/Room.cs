namespace Aura;

/// <summary>
/// A publish/subscribe message bus for Aura.
/// Participants that implement <see cref="IRoomReceiver"/> can join a Room
/// and receive broadcast messages.
/// </summary>
/// <example>
/// <code>
/// // Aura usage:
/// class Logger : IRoomReceiver {
///     fn on_message(message: string, args: object) -> void {
///         Console.WriteLine(message)
///     }
/// }
///
/// let room = new(roomBuilder)
/// let logger = new Logger()
/// room.join(logger)
/// room.broadcast("hello", null)   // Logger receives "hello"
/// room.leave(logger)
/// </code>
/// </example>
public class Room
{
    private readonly List<IRoomReceiver> _members = [];

    /// <summary>
    /// Adds a receiver to this room. The receiver will receive all future broadcasts.
    /// </summary>
    public void Join(IRoomReceiver receiver)
    {
        if (!_members.Contains(receiver))
            _members.Add(receiver);
    }

    /// <summary>
    /// Removes a receiver from this room.
    /// </summary>
    public void Leave(IRoomReceiver receiver)
    {
        _members.Remove(receiver);
    }

    /// <summary>
    /// Sends a message to all members of this room.
    /// </summary>
    public void Broadcast(string message, object? args = null)
    {
        foreach (var member in _members)
            member.OnMessage(message, args!);
    }

    /// <summary>
    /// Returns the number of current members.
    /// </summary>
    public int MemberCount => _members.Count;
}

/// <summary>
/// Interface that Room participants must implement.
/// This is the C# reference; the runtime version is emitted by AuraRuntimeEmitter.
/// </summary>
public interface IRoomReceiver
{
    void OnMessage(string message, object args);
}
