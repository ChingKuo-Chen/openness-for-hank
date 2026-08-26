using System;

namespace PioneerM.OpennessHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var cmd = args.Length > 0 ? args[0] : "help";
            switch (cmd)
            {
                case "help":
                case "--help":
                    Console.WriteLine("OpennessHost (skeleton)");
                    Console.WriteLine("  help     this text");
                    Console.WriteLine("  compile  not implemented in skeleton — fill after onboarding");
                    return 0;
                case "compile":
                    Console.Error.WriteLine("ERR compile not implemented; copy this host into a project and wire TIA V21 Openness");
                    return 2;
                default:
                    Console.Error.WriteLine("ERR unknown command: " + cmd);
                    return 2;
            }
        }
    }
}
