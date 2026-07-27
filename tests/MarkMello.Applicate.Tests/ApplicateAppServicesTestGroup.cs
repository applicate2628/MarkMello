using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Serialises every test that installs a provider into <c>App.Services</c>. That property is
/// process-global mutable state (<c>src/MarkMello.Presentation/App.axaml.cs:20</c>), so two such
/// tests running concurrently would each observe the other's provider. xUnit runs distinct
/// collections in parallel but never two tests of the SAME collection, which is exactly the
/// guarantee needed here.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplicateAppServicesTestGroup
{
    public const string Name = "applicate-app-services";
}
