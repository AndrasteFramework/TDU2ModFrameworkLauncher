﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Andraste.Host;
using Andraste.Host.Logging;
using Andraste.Shared.ModManagement;
using Andraste.Shared.ModManagement.Json;

namespace Launcher
{
    #nullable enable
    public class Launcher : EntryPoint
    {
        public static void Main(string[] args)
        {
            var launcher = new Launcher();
            try
            {
                switch (args.Length)
                {
                    case 0:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(
                            "No argument passed that contains the path to the game! Drag the game exe onto of this file or create a shortcut!");
                        Console.WriteLine("The second argument can be used to override the local DLL file name");
#if !DEBUG
                        Console.WriteLine("Press ANY key to exit");
                        Console.ReadKey();
#endif
                        break;
                    case 1:
                    {
                        if (int.TryParse(args[0], out var pid))
                        {
                            launcher.Attach(pid, "Andraste.Payload.Generic.dll");
                        }
                        else
                        {
                            launcher.Launch(args[0], "Andraste.Payload.Generic.dll");
                        }
                    }
                        break;
                    case 2:
                    {
                        if (int.TryParse(args[0], out var pid))
                        {
                            launcher.Attach(pid, args[1]);
                        }
                        else
                        {
                            launcher.Launch(args[0], args[1]);
                        }
                    }
                        break;
                    default: // at least 3 arguments
                        if (!args[2].Equals("--"))
                        {
                            throw new ArgumentException("Invalid Parameter passed. Expecting the third parameter" +
                                                        " to be '--' to enable passing further parameters to the application");
                        }

                        var appArgs = string.Join(" ", args.Skip(3));
                        launcher.Launch(args[0], args[1], appArgs);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    "An Exception happened while launching the game:");
                Console.WriteLine(ex.ToString());
#if !DEBUG
                Console.WriteLine("Press ANY key to exit");
                Console.ReadKey();
#endif
            }
        }
        
        public void Attach(int processId, string dllName)
        {
            var proc = Process.GetProcessById(processId);
            if (proc == null)
            {
                throw new ArgumentException("Could not find a process with id " + processId, nameof(processId));
            }

            var exePath = proc.MainModule!.FileName;
            
            var actualDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);
            if (!File.Exists(actualDllPath))
            {
                throw new FileNotFoundException("Could not find mod framework dll file", dllName);
            }
            
            // Boot up Andraste
            Initialize();
            Console.WriteLine($"Attaching to {exePath} with pid {processId}, injecting {actualDllPath}");

            if (!WriteModsJson())
            {
                return;
            }

            SetupBindingRedirect(exePath, dllName);
            
            try
            {
                AttachToApplication(proc, actualDllPath);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("There was an error while injecting into target:");
                Console.ResetColor();
                Console.WriteLine(e.ToString());
                Console.WriteLine("Press any key to continue");
                Console.ReadLine();
            }

            LogAndBlockUntilTerminated(proc);

#if !DEBUG
            Console.WriteLine("Process exited");
            Console.WriteLine("Press any key to continue");
            Console.ReadLine();
#endif
        }

        public void Launch(string exePath, string dllName, string launchArgs = "")
        {
            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException("Could not find application executable file", exePath);
            }

            var actualDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);
            if (!File.Exists(actualDllPath))
            {
                throw new FileNotFoundException("Could not find mod framework dll file", dllName);
            }
            
            // Boot up Andraste
            Initialize();
            
            Console.WriteLine($"Launching {exePath} with args {launchArgs}, injecting {actualDllPath}");

            if (!WriteModsJson())
            {
                return;
            }

            SetupBindingRedirect(exePath, dllName);

            var online = false;
            var mutex = new Mutex(false, online ? "44938b8f" : "957e4cc3"); // TDU2 specific hack.

            Process? proc = null;
            try
            {
                proc = StartApplication(exePath, launchArgs, actualDllPath);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("There was an error while injecting into target:");
                Console.ResetColor();
                Console.WriteLine(e.ToString());
                Console.WriteLine("Press any key to continue");
                Console.ReadLine();
            }

            if (proc == null)
            {
                Console.WriteLine("Could not find the game process - Has it crashed?");
                Console.WriteLine("Press any key to continue");
                Console.ReadLine();
                return;
            }

            LogAndBlockUntilTerminated(proc);

            #if !DEBUG
            Console.WriteLine("Process exited");
            Console.WriteLine("Press any key to continue");
            Console.ReadLine();
            #endif
        }

        private static void LogAndBlockUntilTerminated(Process proc)
        {
            Console.Title = $"Andraste Console Launcher - Attached to PID {proc.Id}";

            #region Logging

            var output = new FileLoggingHost(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output.log"));
            var err = new FileLoggingHost(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"));
            output.LoggingEvent += (sender, args) => Console.WriteLine(args.Text);
            err.LoggingEvent += (sender, args) => Console.Error.WriteLine(args.Text);
            output.StartListening();
            err.StartListening();

            #endregion

            // Keep this thread (and thus the application) running
            proc.WaitForExit();

            // Dispose/Cleanup
            output.StopListening();
            err.StopListening();
        }

        private static void SetupBindingRedirect(string exePath, string dllName)
        {
            // Unfortunately, .NET FX requires us to add the config file with the bindings redirect, otherwise it fails to load assemblies.
            // This fails when you run the game multiple times with different .configs (or if the .config is locked by the file?), but that's a corner case.
            // TODO: In theory we'd need to merge files, because here, dllName.config does not containing transitive rewrites that are part in Andraste.Shared.dll.config
            var bindingRedirectFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName + ".config");
            var bindingRedirectShared = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Andraste.Shared.dll.config");
            if (File.Exists(bindingRedirectFile))
            {
                File.Copy(bindingRedirectFile, exePath + ".config", true);
                // For some reason, debugging has shown that sometimes, it tries to resolve the .configs in the Launcher directory. Is that dependant on the app?
                File.Copy(bindingRedirectFile,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.GetFileName(exePath)) + ".config", true);
                //File.Copy(bindingRedirectShared, Path.Combine(Path.GetDirectoryName(exePath)!, "Andraste.Shared.dll.config"), true);
            }
            else if (File.Exists(bindingRedirectShared))
            {
                Console.WriteLine("Warning: Framework does not have a specific binding redirect file. Trying Andraste.Shared");
                File.Copy(bindingRedirectShared, exePath + ".config", true);
            }
            else
            {
                Console.WriteLine(
                    $"Warning: Could not find a binding redirect file at {bindingRedirectFile}. Try to have your IDE generate one.");
            }
        }

        private static bool WriteModsJson()
        {
            var settings = new ModSettings();
            var enabledMods = new List<ModSetting>();
            var modsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "mods");

            if (!Directory.Exists(modsFolder))
            {
                Console.WriteLine("Creating \"mods\" folder");
                Directory.CreateDirectory(modsFolder);
            }
            
            foreach (var mod in PotentialModEnumerator.FindAllPotentialMods(modsFolder))
            {
                var modInfo = ModInformationParser.ParseString(File.ReadAllText(Path.Combine(mod, "mod.json")));
                Console.WriteLine($"Found a potential mod in {mod}: {modInfo.Slug ?? "Could not load mod.json"}");
                if (modInfo.Slug != null)
                {
                    Console.WriteLine($"Enabling {modInfo.Name} by {string.Join(", ", modInfo.Authors)}");
                    // TODO: if mod has more than one configuration, check if one is called default etc.

                    string active;
                    if (modInfo.Configurations.Count == 1)
                    {
                        active = modInfo.Configurations.First().Key;
                    }
                    else
                    {
                        if (modInfo.Configurations.ContainsKey("default"))
                        {
                            active = "default";
                        }
                        else
                        {
                            Console.Error.WriteLine($"{modInfo.Slug} has {modInfo.Configurations.Count} configurations, " +
                                                    "but none of it is called `default`. Manually picking one is TODO");
                            return false;
                        }
                    }

                    enabledMods.Add(new ModSetting
                    {
                        ModPath = mod,
                        ActiveConfiguration = active
                    });
                }
            }

            // TODO: Do shit with the features to resolve conflicts
            settings.EnabledMods = enabledMods.ToArray();

            File.WriteAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mods.json"),
                JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
    }
    #nullable restore
}
