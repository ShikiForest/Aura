namespace Aura;

/// <summary>
/// Arg builder for <see cref="RoomBuilder"/>.
/// Room requires no arguments, so this is an empty arg builder.
/// </summary>
/// <example>
/// <code>
/// let args = new RoomArgs()
/// let builder = new RoomBuilder(args: args)
/// let room = new(builder)
/// </code>
/// </example>
public class RoomArgs : CLRConstructorArgBuilder
{
}

/// <summary>
/// Builder for <see cref="Room"/>.
/// Room has no required configuration, so this builder is straightforward.
/// <code>
/// // Minimal:
/// let room = new(RoomBuilder())
///
/// // With explicit args:
/// let args = new RoomArgs()
/// let builder = new RoomBuilder(args: args)
/// let room = new(builder)
/// </code>
/// </summary>
public class RoomBuilder : IBuilder<Room>
{
    private readonly CLRConstructorArgBuilder? _argBuilder;

    public RoomBuilder()
    {
    }

    public RoomBuilder(CLRConstructorArgBuilder argBuilder)
    {
        _argBuilder = argBuilder;
    }

    public Dictionary<string, object> GetConstructorDictionary()
    {
        return _argBuilder?.GetConstructorDictionary() ?? new Dictionary<string, object>();
    }

    public Room Build(Dictionary<string, object> args)
    {
        return new Room();
    }
}
