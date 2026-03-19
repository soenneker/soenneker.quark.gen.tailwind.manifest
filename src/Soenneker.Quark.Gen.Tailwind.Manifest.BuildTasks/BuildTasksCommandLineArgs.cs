namespace Soenneker.Quark.Gen.Tailwind.Manifest.BuildTasks;

public sealed class BuildTasksCommandLineArgs
{
    public string[] Args { get; }

    public BuildTasksCommandLineArgs(string[] args)
    {
        Args = args;
    }
}
