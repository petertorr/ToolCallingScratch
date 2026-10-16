namespace ToolCallingScratch
{
    record class Options(ProgramAction Action, ModelFilter Filter, bool RequireToolUse, bool BruteForce, 
        OutputModes OutputModes, bool ProgressiveOutput, int TimeoutInSeconds)
    {
        internal const int DefaultTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...

        static Options MakeDefault(ProgramAction action) => new(action, ModelFilter.Default, false,
            false, OutputModes.Default, false, DefaultTimeoutInSeconds);

        internal static Options DefaultBeforeParsing { get; } = MakeDefault(ProgramAction.Help);

        internal static Options Parse(string[] args, out List<string> ignoredArgs)
        {
            int index = 0;
            ignoredArgs = [];
            List<string> localIgnoredArgs = [];
            bool allowCpu = false;
            bool requireToolUse = false;
            bool tryAllVariants = false;
            bool bruteForce = false;
            bool ignoreToolSupport = false;
            bool progressiveOutput = false;
            OutputModes outputModes = OutputModes.Default;
            int timeout = DefaultTimeoutInSeconds;
            int maxSize = ModelFilter.DefaultMaxFileSize;
            string? skipPast = null;
            List<string> requestedModels = [];
            List<string> ignoredModels = [];
            List<string> registeredEps = []; // will be filled out after loading.
            ProgramAction action = ProgramAction.Help;

            // Specifying nothing is the same as 'run' with no other args
            if (args.Length == 0)
            {
                return MakeDefault(ProgramAction.Run);
            }
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.List;
            }
            else if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.Run;
            }
            else if (args[0].Equals("load", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.Load;
            }
            else
            {
                Program.WriteLine($"{Environment.NewLine}`wApp mode '`o{args[0]}`w' not recognized; showing help.", OutputModes.ProgramOutput);
                return MakeDefault(ProgramAction.Help);
            }

            bool parseSuccess = true;

            // Start at 1, not 0, since we got the action from args[0]
            for (index = 1; index < args.Length; ++index)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "-v":
                        tryAllVariants = true;
                        break;

                    case "-c":
                        allowCpu = true;
                        break;

                    case "-f":
                        requireToolUse = true;
                        break;

                    case "-b":
                        bruteForce = true;
                        break;

                    case "-a":
                        ignoreToolSupport = true;
                        break;

                    case "-r":
                        outputModes |= OutputModes.VerbatimOutput;
                        break;

                    case "-h":
                        outputModes &= (OutputModes.All ^ OutputModes.Timings);
                        break;

                    case "-q":
                        outputModes = OutputModes.Quiet;
                        break;

                    case "-z":
                        outputModes = OutputModes.SuperQuiet;
                        break;

                    case "-p":
                        progressiveOutput = true;

                        // Don't want anything else getting in the way
                        outputModes = OutputModes.SuperQuiet;
                        break;

                    case "-t":
                        parseSuccess = ParseArgParam("-t", s => timeout = int.Parse(s), "a time in seconds");
                        break;

                    case "-m":
                        parseSuccess = ParseArgParam("-m", requestedModels.Add, "a model alias");
                        break;

                    case "-s":
                        parseSuccess = ParseArgParam("-s", s => maxSize = int.Parse(s), "a file size in MB");
                        break;

                    case "-i":
                        parseSuccess = ParseArgParam("-i", ignoredModels.Add, "a model name or alias to ignore");
                        break;

                    case "-k":
                        parseSuccess = ParseArgParam("-k", s => skipPast = s, "a model name or alias to skip past");
                        break;

                    default:
                        localIgnoredArgs.Add(args[index]);
                        break;
                }

                if (!parseSuccess)
                {
                    action = ProgramAction.Error;
                    break;
                }
            }

            ignoredArgs = localIgnoredArgs;
            return new(action, new(requestedModels, maxSize, ignoredModels, tryAllVariants, ignoreToolSupport, skipPast, registeredEps, allowCpu),
                requireToolUse, bruteForce, outputModes, progressiveOutput, timeout);

            bool ParseArgParam(string param, Action<string> callback, string description)
            {
                if (index < args.Length - 1)
                {
                    ++index;
                    callback(args[index]);
                    return true;
                }

                Program.WriteLine($"`r{param} option needs {description}.", OutputModes.ProgramOutput);
                localIgnoredArgs.Add(args[index]);
                return false;
            }
        }
    }
}
