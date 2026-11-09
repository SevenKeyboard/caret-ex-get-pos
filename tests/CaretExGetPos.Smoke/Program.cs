Console.WriteLine($"Core smoke process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
await CorePolicyTests.RunAsync();

await ParallelQueryTests.RunAsync();
