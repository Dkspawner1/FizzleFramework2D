using System;
using FizzleFramework2D.Configuration;
using Serilog;

namespace FizzleFramework2D.Core;
    public static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var settings = new GameSettings()
            {
                Window = new()
                {
                    Title = "FizzleFramework2D - With Shader System",
                    Width = 1600,
                    Height = 900,
                    Resizable = true
                },
                Rendering = new()
                {
                    VSync = true
                },
                Content = new()
                {
                    ShadersDirectory = "assets/shaders",
                },
                Development = new DevelopmentSettings()
                {
                    EnableHotReload = true
                }
            };

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithThreadId()
                .Enrich.WithThreadName()
                .Enrich.WithMachineName()
                .WriteTo.Console(
                    outputTemplate:
                    "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{ThreadId:00}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/fizzle-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{ThreadId:00}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("FizzleFramework2D starting...");

            try
            {
                using var application = new Application(settings);

                if (!application.Initialize())
                {
                    Log.Error("Application initialization failed");
                    Console.WriteLine("Application initialization failed");
                    return;
                }

                application.LoadContent();
                application.Run();
                application.UnloadContent();
                
                Log.Information("Shutdown Complete");
                Console.WriteLine("Shutdown Complete");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error occurred");
                Console.WriteLine($"Fatal error:: {ex.Message}");
            }
            finally
            {
                Log.CloseAndFlush();
            }
    }
}
