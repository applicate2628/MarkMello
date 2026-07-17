using Xunit;

// Disable xUnit test-collection parallelization for this assembly. Rationale:
// IpcContractTests' tolerant-log-handler and renderId-drop tests capture
// process-global Console.Error (via Console.SetError) to observe the ONLY
// side effect those handlers produce. A per-collection [Collection] attribute is
// insufficient — xUnit still runs different collections concurrently, so another
// class writing to Console.Error inside the capture window could interleave. The
// assembly-wide switch is the robust fix (and it is compiled into the test DLL,
// so `dotnet test` honors it in CI too, with no CLI flag to forget). The suite
// runs well under a second, so serial execution is free.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
