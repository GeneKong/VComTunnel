namespace VComTunnel.Core;

public static class AppPaths
{
    public static string ProgramDataRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return overrideRoot;
            }

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                programData = Environment.CurrentDirectory;
            }

            return Path.Combine(programData, "VComTunnel");
        }
    }

    public static string ConfigPath => Path.Combine(ProgramDataRoot, "config.json");
    public static string LogsDirectory => Path.Combine(ProgramDataRoot, "logs");
    public static string SerialTrafficLogsDirectory => Path.Combine(LogsDirectory, "serial");
    public static string OperationsDirectory => Path.Combine(ProgramDataRoot, "operations");
    public static string ToolsDirectory => Path.Combine(ProgramDataRoot, "tools");
    public static string DownloadsDirectory => Path.Combine(ProgramDataRoot, "downloads");
    public static string BundledDependenciesDirectory => Path.Combine(AppContext.BaseDirectory, "dependencies");
}
