using Cida.Platform;
if (args is ["--accessibility-worker"]) return AccessibilityWorker.Run();
return await CommandLineHost.RunAsync(args);
