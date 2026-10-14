// spactl: the registry and release command line (plan row 2.6). SpactlApp holds every command.
return await Spactl.SpactlApp.RunAsync(args, Spactl.SpactlEnvironment.Live(), CancellationToken.None);
