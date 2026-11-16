// Scaffold: replaced by the macOS agent host in the same change.
using System.Reflection;

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
Console.WriteLine(version);
return 0;
