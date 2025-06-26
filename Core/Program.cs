using System;

namespace FizzleFramework2D.Core;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            using IApplication application = new Application();
            if (!application.Initialize())
            {
                Console.WriteLine("Application initialization failed");
                return;
            }

            application.LoadContent();
            application.Run();
            
            application.UnloadContent();
            Console.WriteLine("Shutdown Complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error:: {ex.Message}");
        }
    }
}