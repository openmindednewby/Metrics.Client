namespace Metrics.Client.Tests.Middleware;

/// <summary>
/// Serializes every test class that exercises <c>HttpMetricsMiddleware</c>.
/// </summary>
/// <remarks>
/// <para>
/// The middleware writes to the process-wide <c>Prometheus.Metrics.DefaultRegistry</c>
/// and reads its app label from the STATIC
/// <c>HttpMetricsMiddleware.ServiceName</c>. xUnit runs test classes in parallel by
/// default, so two classes each doing "set ServiceName, invoke, scrape, assert on my
/// unique label" can interleave: class A sets its name, class B overwrites it, and
/// A's request is then recorded under B's label. A's assertion fails against a
/// registry that is actually correct.
/// </para>
/// <para>
/// This is not flakiness to be retried away — it is a genuine race on shared static
/// state, and the label-uniqueness trick both classes use does not protect against
/// it. Sharing one collection makes xUnit run them sequentially. The alternative —
/// threading a non-static registry and label through the middleware — is a
/// production API change that this test concern does not justify.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PrometheusRegistryCollection
{
    public const string Name = "PrometheusRegistry";
}
