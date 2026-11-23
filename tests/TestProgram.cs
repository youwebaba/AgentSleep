using System;

namespace AgentSleep
{
    internal static class TestProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                int result;
                if (RecoveryTests.TryRun(args, out result)) return result;
                DisplayTests.Run();
                TrayDryRunTests.Run();
#if !DRY_RUN_ONLY
                TrayTests.Run();
#endif
                RecoveryTests.Run();
                Console.WriteLine("AgentSleep tests passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
