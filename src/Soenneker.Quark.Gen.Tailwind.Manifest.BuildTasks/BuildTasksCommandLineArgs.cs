namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

/// <summary>
/// Represents the build tasks command line args.
/// </summary>
public sealed class BuildTasksCommandLineArgs
{
    /// <summary>
    /// Gets args.
    /// </summary>
    public string[] Args { get; }

    public BuildTasksCommandLineArgs(string[] args)
    {
        Args = args;
    }
}
