namespace AntlrCompiler.Cli;

/// <summary>Options shared by compile and run subcommands.</summary>
internal abstract record BaseOptions(
    string SourceFile,
    string? OutputPath,
    string? AssemblyName,
    bool    Verbose,
    bool    NoLower
);

/// <summary>Options for `aura compile`.</summary>
internal sealed record CompileOptions(
    string SourceFile,
    string? OutputPath,
    string? AssemblyName,
    bool    Verbose,
    bool    NoLower
) : BaseOptions(SourceFile, OutputPath, AssemblyName, Verbose, NoLower);

/// <summary>Options for `aura run`.</summary>
internal sealed record RunOptions(
    string SourceFile,
    string? OutputPath,
    string? AssemblyName,
    bool    Verbose,
    bool    NoLower,
    string  TargetFramework,
    bool    SelfContained
) : BaseOptions(SourceFile, OutputPath, AssemblyName, Verbose, NoLower);

/// <summary>Options for `aura check`.</summary>
internal sealed record CheckOptions(
    string SourceFile,
    bool   Verbose
);
